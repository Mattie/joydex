#include "joydex_lan_audio.h"

#include "esphome/components/audio/audio.h"
#include "esphome/core/application.h"
#include "esphome/core/helpers.h"
#include "esphome/core/log.h"

#include <algorithm>
#include <cerrno>
#include <cstring>

#include <esp_heap_caps.h>
#include <freertos/idf_additions.h>
#include <lwip/sockets.h>

namespace esphome {
namespace joydex_lan_audio {

static const char *const TAG = "joydex.lan_audio";
static const char *const DUPLEX_HELLO_MESSAGE =
    "{\"type\":\"hello\",\"protocol\":1,\"path\":\"/joydex/audio\","
    "\"uplink\":{\"encoding\":\"pcm_s16le\",\"sampleRate\":16000,\"channels\":1,\"frameMs\":20},"
    "\"downlink\":{\"encoding\":\"pcm_s16le\",\"sampleRate\":24000,\"channels\":1,\"frameMs\":20}}";
static const char *const UPLINK_ONLY_HELLO_MESSAGE =
    "{\"type\":\"hello\",\"protocol\":1,\"path\":\"/joydex/audio\",\"mode\":\"uplink_only\"," 
    "\"uplink\":{\"encoding\":\"pcm_s16le\",\"sampleRate\":16000,\"channels\":1,\"frameMs\":20},"
    "\"downlink\":null}";

constexpr size_t stale_complete_frame_bytes(size_t fill, size_t frame_size) {
  return frame_size != 0 && fill >= frame_size ? ((fill / frame_size) - 1) * frame_size : 0;
}

static_assert(stale_complete_frame_bytes(1024, 640) == 0,
              "A normal 1024-byte microphone callback must retain its partial next frame");
static_assert(stale_complete_frame_bytes(1280, 640) == 640,
              "Only complete stale microphone frames may be discarded");
static_assert(stale_complete_frame_bytes(1664, 640) == 640,
              "A partial newest microphone frame must survive stale-frame removal");

void JoydexLanAudio::setup() {
  if (this->microphone_source_ == nullptr || (!this->uplink_only_ && this->speaker_ == nullptr) ||
      this->announcement_media_player_ == nullptr || this->barge_in_switch_ == nullptr) {
    ESP_LOGE(TAG, "Required audio or lifecycle component is missing");
    this->mark_failed();
    return;
  }

  const auto microphone_info = this->microphone_source_->get_audio_stream_info();
  if (microphone_info.get_sample_rate() != UPLINK_SAMPLE_RATE || microphone_info.get_bits_per_sample() != BITS_PER_SAMPLE ||
      microphone_info.get_channels() != CHANNELS) {
    ESP_LOGE(TAG, "wake_word_mic must be 16 kHz, mono, signed 16-bit PCM (got %u Hz, %u bit, %u channel)",
             microphone_info.get_sample_rate(), microphone_info.get_bits_per_sample(), microphone_info.get_channels());
    this->mark_failed();
    return;
  }

  this->websocket_send_mutex_ = xSemaphoreCreateMutexStatic(&this->websocket_send_mutex_storage_);
  if (this->websocket_send_mutex_ == nullptr) {
    ESP_LOGE(TAG, "Could not create the WebSocket send mutex");
    this->mark_failed();
    return;
  }

  this->uplink_ring_.capacity = UPLINK_QUEUE_BYTES;
  this->downlink_ring_.capacity = this->uplink_only_ ? 0 : DOWNLINK_QUEUE_BYTES;
  this->uplink_ring_.data = static_cast<uint8_t *>(heap_caps_malloc(UPLINK_QUEUE_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  if (!this->uplink_only_) {
    this->downlink_ring_.data =
        static_cast<uint8_t *>(heap_caps_malloc(DOWNLINK_QUEUE_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  }
  this->receive_buffer_ =
      static_cast<uint8_t *>(heap_caps_malloc(MAX_DOWNLINK_FRAME_BYTES + 1, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  if (!this->uplink_only_) {
    this->downlink_scratch_ =
        static_cast<uint8_t *>(heap_caps_malloc(DOWNLINK_FRAME_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  }
  if (this->uplink_ring_.data == nullptr || this->receive_buffer_ == nullptr ||
      (!this->uplink_only_ && (this->downlink_ring_.data == nullptr || this->downlink_scratch_ == nullptr))) {
    ESP_LOGE(TAG, "Could not allocate bounded PCM queues in PSRAM");
    this->mark_failed();
    return;
  }

  if (!this->uplink_only_) {
    this->speaker_->set_audio_stream_info(audio::AudioStreamInfo(BITS_PER_SAMPLE, CHANNELS, DOWNLINK_SAMPLE_RATE));
    this->speaker_->start();
    this->speaker_start_requested_ = true;
  }

  this->microphone_source_->add_data_callback(
      [this](const std::vector<uint8_t> &data) { this->handle_microphone_(data); });

  this->announcement_active_.store(
      this->announcement_media_player_->state == media_player::MEDIA_PLAYER_STATE_ANNOUNCING);
  this->announcement_ended_ms_.store(millis());
  this->announcement_media_player_->add_on_state_callback([this]() { this->update_announcement_state_(); });
  this->barge_in_enabled_.store(this->barge_in_switch_->state);
  this->barge_in_switch_->add_on_state_callback([this](bool state) {
    this->barge_in_enabled_.store(state);
    if (!state && this->playback_busy_.load()) {
      this->flush_ring_(this->uplink_ring_, this->uplink_mux_);
    }
  });

  httpd_config_t config = HTTPD_DEFAULT_CONFIG();
  config.server_port = this->port_;
  config.ctrl_port = ESP_HTTPD_DEF_CTRL_PORT + 2;  // Port 80 uses +0; Sendspin uses +1.
  config.max_open_sockets = 1;
  config.lru_purge_enable = true;
  config.recv_wait_timeout = 2;
  config.send_wait_timeout = 2;
  config.open_fn = JoydexLanAudio::open_callback_;
  config.close_fn = JoydexLanAudio::close_callback_;
  config.global_user_ctx = this;
  // ESP-IDF calls free(global_user_ctx) when this callback is null. ESPHome
  // owns this component for the process lifetime, so the HTTP server must not.
  config.global_user_ctx_free_fn = JoydexLanAudio::release_global_context_;
  config.task_caps = MALLOC_CAP_SPIRAM;

  const httpd_uri_t websocket_uri = {
      .uri = "/joydex/audio",
      .method = HTTP_GET,
      .handler = JoydexLanAudio::websocket_handler_,
      .user_ctx = this,
      .is_websocket = true,
  };

  if (httpd_start(&this->server_, &config) != ESP_OK) {
    ESP_LOGE(TAG, "Could not start trusted-LAN WebSocket server on port %u", this->port_);
    this->mark_failed();
    return;
  }
  if (httpd_register_uri_handler(this->server_, &websocket_uri) != ESP_OK) {
    ESP_LOGE(TAG, "Could not register /joydex/audio");
    httpd_stop(this->server_);
    this->server_ = nullptr;
    this->mark_failed();
    return;
  }

  this->task_running_.store(true);
  TaskHandle_t uplink_task_handle = nullptr;
  const BaseType_t task_result = xTaskCreatePinnedToCoreWithCaps(
      JoydexLanAudio::uplink_task_, "joydex_audio_tx", 4096, this, UPLINK_TASK_PRIORITY, &uplink_task_handle,
      tskNO_AFFINITY, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
  if (task_result != pdPASS) {
    ESP_LOGE(TAG, "Could not start microphone uplink task");
    this->task_running_.store(false);
    httpd_stop(this->server_);
    this->server_ = nullptr;
    this->mark_failed();
    return;
  }
  this->uplink_task_handle_.store(uplink_task_handle);

  ESP_LOGI(TAG, "Listening at ws://<device>:%u/joydex/audio", this->port_);
}

void JoydexLanAudio::loop() {
  const bool connection_opened = this->connection_opened_pending_.exchange(false);
  const bool connection_lost = this->connection_lost_pending_.exchange(false);
  if (connection_opened || connection_lost) {
    this->stop_session_(false);
  }
  if (this->close_requested_.exchange(false)) {
    this->stop_session_(true);
  }
  if (this->open_command_pending_.exchange(false)) {
    this->stop_session_(false);
    this->open_requested_.store(true);
  }
  if (this->flush_requested_.exchange(false)) {
    this->flush_audio_();
    if (this->uplink_only_) {
      this->playback_busy_.store(false);
      this->playback_end_received_.store(true);
      this->playback_source_busy_ = false;
      this->playback_tail_active_ = false;
    } else {
      this->begin_playback_pause_();
      this->playback_source_busy_ = true;
      this->speaker_stop_pending_ = true;
      this->speaker_stop_observed_ = false;
      this->speaker_start_requested_ = false;
      this->speaker_->stop();
    }
  }

  const int socket = this->client_socket_.load();
  if (this->hello_pending_.load() && this->client_is_websocket_(socket)) {
    const char *hello = this->uplink_only_ ? UPLINK_ONLY_HELLO_MESSAGE : DUPLEX_HELLO_MESSAGE;
    if (this->send_text_(hello)) {
      this->hello_pending_.store(false);
    }
  }

  this->update_session_state_();

  if (this->opened_notification_pending_.exchange(false)) {
    this->send_text_("{\"type\":\"opened\"}");
  }
  if (this->closed_notification_pending_.exchange(false)) {
    this->send_text_("{\"type\":\"closed\"}");
  }
  if (this->pong_pending_.exchange(false)) {
    this->send_text_("{\"type\":\"pong\"}");
  }

  if (!this->session_active_.load()) {
    // Session teardown stops the resampler to discard its internal buffered
    // audio. Complete that asynchronous stop/start cycle while the device is
    // idle so the next wake never has to bootstrap the speaker pipeline while
    // live Codex audio is already arriving.
    if (!this->uplink_only_ && this->speaker_ != nullptr &&
        (this->speaker_stop_pending_ || !this->speaker_->is_running())) {
      this->ensure_speaker_ready_();
    }
    this->playback_busy_.store(false);
    this->last_active_loop_ms_ = 0;
    return;
  }

  if (this->uplink_only_) {
    this->playback_busy_.store(false);
    this->last_active_loop_ms_ = 0;
    return;
  }

  const uint32_t loop_now = millis();
  if (this->last_active_loop_ms_ != 0) {
    update_maximum_(this->downlink_max_loop_gap_ms_, loop_now - this->last_active_loop_ms_);
  }
  this->last_active_loop_ms_ = loop_now;

  if (!this->ensure_speaker_ready_()) {
    this->playback_busy_.store(true);
    return;
  }

  size_t drained_this_loop = 0;
  while (drained_this_loop < MAX_DOWNLINK_DRAIN_BYTES_PER_LOOP) {
    if (this->downlink_pending_offset_ >= this->downlink_pending_length_) {
      this->downlink_pending_length_ =
          this->pop_(this->downlink_ring_, this->downlink_mux_, this->downlink_scratch_, DOWNLINK_FRAME_BYTES);
      this->downlink_pending_offset_ = 0;
    }

    const size_t pending = this->downlink_pending_length_ - this->downlink_pending_offset_;
    if (pending == 0) {
      break;
    }

    const size_t budget = MAX_DOWNLINK_DRAIN_BYTES_PER_LOOP - drained_this_loop;
    const size_t offered = std::min(pending, budget);
    const size_t accepted =
        std::min(this->speaker_->play(this->downlink_scratch_ + this->downlink_pending_offset_, offered), offered);
    if (accepted == 0) {
      this->downlink_speaker_backpressure_events_.fetch_add(1);
      break;
    }
    if (accepted < offered) {
      this->downlink_speaker_partial_writes_.fetch_add(1);
    }
    this->downlink_pending_offset_ += accepted;
    drained_this_loop += accepted;
    this->downlink_speaker_accepted_bytes_.fetch_add(static_cast<uint32_t>(accepted));
  }

  const size_t queued = this->ring_fill_(this->downlink_ring_, this->downlink_mux_) +
                        (this->downlink_pending_length_ - this->downlink_pending_offset_);
  update_maximum_(this->downlink_queue_high_water_bytes_, static_cast<uint32_t>(queued));
  const bool speaker_buffered = this->speaker_->has_buffered_data();
  const uint32_t since_downlink = millis() - this->last_downlink_ms_.load();
  const bool source_busy = queued > 0 || speaker_buffered ||
                           (!this->playback_end_received_.load() && since_downlink < PLAYBACK_GAP_HOLD_MS);
  if (source_busy) {
    this->playback_source_busy_ = true;
    this->playback_tail_active_ = false;
    this->playback_busy_.store(true);
  } else if (this->playback_source_busy_) {
    this->playback_source_busy_ = false;
    this->playback_tail_active_ = true;
    this->playback_drained_ms_ = millis();
    this->playback_busy_.store(true);
  } else if (this->playback_tail_active_ && (millis() - this->playback_drained_ms_) < PLAYBACK_TAIL_MS) {
    this->playback_busy_.store(true);
  } else {
    if (this->playback_tail_active_ && !this->barge_in_enabled_.load()) {
      // Discard any callback that raced with the initial playback gate so the
      // first post-tail frame begins on a fresh PCM sample boundary.
      this->flush_ring_(this->uplink_ring_, this->uplink_mux_);
    }
    this->playback_tail_active_ = false;
    this->playback_busy_.store(false);
  }

  if (!this->playback_end_received_.load() && queued == 0 && !speaker_buffered && since_downlink > 80 &&
      !this->underrun_reported_.exchange(true)) {
    this->downlink_underruns_.fetch_add(1);
  }
}

void JoydexLanAudio::on_shutdown() {
  this->task_running_.store(false);
  this->set_close_reason_(CloseReason::SHUTDOWN);
  this->stop_session_(false);
  if (this->server_ != nullptr) {
    const int socket = this->client_socket_.exchange(-1);
    if (socket >= 0) {
      httpd_sess_trigger_close(this->server_, socket);
    }

    // The uplink task only queues one persistent-buffer send at a time. Stop
    // the producer before destroying the HTTP server that owns any queued
    // worker callback.
    for (uint16_t wait = 0; wait < 250 && this->uplink_task_handle_.load() != nullptr; wait++) {
      vTaskDelay(pdMS_TO_TICKS(10));
    }
    if (this->uplink_task_handle_.load() == nullptr) {
      httpd_stop(this->server_);
      this->server_ = nullptr;
    } else {
      ESP_LOGW(TAG, "Uplink task did not stop before shutdown; leaving HTTP server to platform teardown");
    }
  }
}

void JoydexLanAudio::dump_config() {
  ESP_LOGCONFIG(TAG, "Joydex LAN audio:");
  ESP_LOGCONFIG(TAG, "  Endpoint: ws://<device>:%u/joydex/audio", this->port_);
  ESP_LOGCONFIG(TAG, "  Uplink: 16 kHz PCM16 mono, 20 ms frames");
  ESP_LOGCONFIG(TAG, "  Mode: %s", this->uplink_only_ ? "microphone/control only" : "raw PCM duplex");
  if (!this->uplink_only_) {
    ESP_LOGCONFIG(TAG, "  Downlink: 24 kHz PCM16 mono, 20 ms frames");
  }
  ESP_LOGCONFIG(TAG, "  Uplink queue bound: 400 ms");
  ESP_LOGCONFIG(TAG, "  Barge-in default: %s", this->barge_in_switch_->state ? "ON" : "OFF");
}

bool JoydexLanAudio::is_connected() const {
  return this->client_is_websocket_(this->client_socket_.load());
}

const char *JoydexLanAudio::get_last_close_reason() const {
  return close_reason_name_(this->last_close_reason_.load());
}

esp_err_t JoydexLanAudio::websocket_handler_(httpd_req_t *request) {
  auto *component = static_cast<JoydexLanAudio *>(request->user_ctx);
  return component->handle_websocket_(request);
}

esp_err_t JoydexLanAudio::open_callback_(httpd_handle_t server, int socket) {
  auto *component = static_cast<JoydexLanAudio *>(httpd_get_global_user_ctx(server));
  // ESP-IDF serializes open_fn, close_fn, and URI handlers on the HTTPD task.
  // client_mux_ covers their ownership transition against the separate uplink
  // task, which is the only concurrent writer of socket-failure state.
  // A TCP/WebSocket reconnect is always a fresh protocol session. Shut the
  // microphone gate synchronously, then let the ESPHome loop flush audio and
  // stop the speaker without touching those components from the HTTPD task.
  component->session_active_.store(false);
  component->open_requested_.store(false);
  component->connection_opened_pending_.store(true);
  component->websocket_connections_.fetch_add(1);
  portENTER_CRITICAL(&component->client_mux_);
  const int previous = component->client_socket_.exchange(socket);
  component->client_generation_.fetch_add(1);
  component->connection_lost_pending_.store(false);
  component->active_close_reason_.store(CloseReason::NONE);
  component->active_socket_errno_.store(0);
  portEXIT_CRITICAL(&component->client_mux_);
  if (previous >= 0 && previous != socket) {
    httpd_sess_trigger_close(server, previous);
  }
  int no_delay = 1;
  if (setsockopt(socket, IPPROTO_TCP, TCP_NODELAY, &no_delay, sizeof(no_delay)) < 0) {
    ESP_LOGW(TAG, "Could not enable TCP_NODELAY for socket %d", socket);
  }
  // ESP-IDF's default WebSocket sender treats any non-negative send() result
  // as a complete frame. Sustained PCM can produce a short TCP write, which
  // drops the rest of that payload and corrupts every following frame boundary.
  if (httpd_sess_set_send_override(server, socket, JoydexLanAudio::send_all_) != ESP_OK) {
    ESP_LOGE(TAG, "Could not install complete-write WebSocket sender for socket %d", socket);
    return ESP_FAIL;
  }
  component->hello_pending_.store(true);
  ESP_LOGI(TAG, "Joydex audio client connected");
  return ESP_OK;
}

void JoydexLanAudio::close_callback_(httpd_handle_t server, int socket) {
  auto *component = static_cast<JoydexLanAudio *>(httpd_get_global_user_ctx(server));
  int expected = socket;
  const bool active_socket = component->client_socket_.compare_exchange_strong(expected, -1);
  if (active_socket) {
    if (component->active_close_reason_.load() == CloseReason::NONE) {
      component->set_close_reason_(CloseReason::PEER_DISCONNECT);
    }
    component->connection_lost_pending_.store(true);
  }
  close(socket);
  if (active_socket) {
    ESP_LOGI(TAG, "Joydex audio client disconnected; reason=%s; errno=%d",
             component->get_last_close_reason(), static_cast<int>(component->last_socket_errno_.load()));
  } else {
    ESP_LOGI(TAG, "Replaced Joydex audio client disconnected; socket=%d", socket);
  }
}

int JoydexLanAudio::send_all_(httpd_handle_t server, int socket, const char *data, size_t length, int flags) {
  auto *component = static_cast<JoydexLanAudio *>(httpd_get_global_user_ctx(server));
  size_t offset = 0;
  while (offset < length) {
    const int written = send(socket, data + offset, length - offset, flags);
    if (written < 0 && errno == EINTR) {
      continue;
    }
    if (written <= 0) {
      if (component != nullptr) {
        component->record_socket_errno_if_active_(socket, written == 0 ? 0 : errno);
      }
      return -1;
    }
    offset += static_cast<size_t>(written);
  }
  return static_cast<int>(offset);
}

void JoydexLanAudio::release_global_context_(void *context) {
  // The ESPHome application owns the component. httpd_stop() owns no context.
  (void) context;
}

esp_err_t JoydexLanAudio::handle_websocket_(httpd_req_t *request) {
  if (request->method == HTTP_GET) {
    return ESP_OK;  // WebSocket handshake.
  }

  const int socket = httpd_req_to_sockfd(request);
  httpd_ws_frame_t frame{};
  esp_err_t result = httpd_ws_recv_frame(request, &frame, 0);
  if (result != ESP_OK) {
    this->websocket_receive_failures_.fetch_add(1);
    if (this->client_socket_.load() == socket) {
      this->set_close_reason_(CloseReason::RECEIVE_HEADER_FAILED);
      this->connection_lost_pending_.store(true);
    }
    ESP_LOGW(TAG, "WebSocket frame header receive failed; result=%s", esp_err_to_name(result));
    return result;
  }
  if (frame.len > MAX_DOWNLINK_FRAME_BYTES) {
    this->websocket_receive_failures_.fetch_add(1);
    if (this->client_socket_.load() == socket) {
      this->set_close_reason_(CloseReason::RECEIVE_OVERSIZED);
      this->connection_lost_pending_.store(true);
    }
    ESP_LOGW(TAG, "Rejected oversized WebSocket frame: %u bytes", static_cast<unsigned>(frame.len));
    return ESP_ERR_INVALID_SIZE;
  }

  frame.payload = this->receive_buffer_;
  result = httpd_ws_recv_frame(request, &frame, frame.len);
  if (result != ESP_OK) {
    this->websocket_receive_failures_.fetch_add(1);
    if (this->client_socket_.load() == socket) {
      this->set_close_reason_(CloseReason::RECEIVE_PAYLOAD_FAILED);
      this->connection_lost_pending_.store(true);
    }
    ESP_LOGW(TAG, "WebSocket frame payload receive failed; result=%s; bytes=%u", esp_err_to_name(result),
             static_cast<unsigned>(frame.len));
    return result;
  }
  this->receive_buffer_[frame.len] = 0;

  // The HTTP server can finish a frame from the descriptor that a reconnect
  // just replaced. Never let that stale client control or feed the new session.
  if (this->client_socket_.load() != socket) {
    ESP_LOGD(TAG, "Ignored WebSocket frame from replaced socket %d", socket);
    return ESP_OK;
  }

  if (frame.type == HTTPD_WS_TYPE_TEXT) {
    this->handle_text_(reinterpret_cast<const char *>(frame.payload), frame.len);
  } else if (frame.type == HTTPD_WS_TYPE_BINARY) {
    this->handle_downlink_(frame.payload, frame.len);
  } else if (frame.type == HTTPD_WS_TYPE_CLOSE) {
    if (this->client_socket_.load() == socket) {
      this->set_close_reason_(CloseReason::PEER_CLOSE);
      this->connection_lost_pending_.store(true);
    }
  }
  return ESP_OK;
}

void JoydexLanAudio::handle_text_(const char *text, size_t length) {
  const std::string message(text, length);
  const bool is_type = message.find("\"type\"") != std::string::npos;
  if (is_type && message.find("\"open\"") != std::string::npos) {
    this->open_command_pending_.store(true);
    this->session_active_.store(false);
  } else if (is_type && message.find("\"close\"") != std::string::npos) {
    this->close_requested_.store(true);
  } else if (is_type && message.find("\"flush\"") != std::string::npos) {
    this->flush_requested_.store(true);
  } else if (is_type && message.find("\"playback_end\"") != std::string::npos) {
    this->playback_end_received_.store(true);
  } else if (is_type && message.find("\"ping\"") != std::string::npos) {
    this->pong_pending_.store(true);
  }
}

void JoydexLanAudio::handle_downlink_(const uint8_t *data, size_t length) {
  if (this->uplink_only_ || !this->session_active_.load() || length == 0 ||
      (length % sizeof(int16_t)) != 0) {
    this->downlink_dropped_bytes_.fetch_add(static_cast<uint32_t>(length));
    return;
  }
  this->begin_playback_pause_();
  this->playback_end_received_.store(false);
  this->last_downlink_ms_.store(millis());
  this->underrun_reported_.store(false);
  const size_t queued =
      this->push_latest_(this->downlink_ring_, this->downlink_mux_, data, length, this->downlink_dropped_bytes_);
  update_maximum_(this->downlink_queue_high_water_bytes_, static_cast<uint32_t>(queued));
  this->downlink_frames_.fetch_add(1);
}

void JoydexLanAudio::handle_microphone_(const std::vector<uint8_t> &data) {
  if (!this->microphone_allowed_() || data.empty()) {
    return;
  }
  if ((data.size() % sizeof(int16_t)) != 0) {
    this->uplink_dropped_bytes_.fetch_add(static_cast<uint32_t>(data.size()));
    return;
  }
  const size_t queued =
      this->push_latest_(this->uplink_ring_, this->uplink_mux_, data.data(), data.size(), this->uplink_dropped_bytes_);
  update_maximum_(this->uplink_queue_high_water_bytes_, static_cast<uint32_t>(queued));
}

void JoydexLanAudio::update_announcement_state_() {
  const bool announcing = this->announcement_media_player_->state == media_player::MEDIA_PLAYER_STATE_ANNOUNCING;
  const bool was_announcing = this->announcement_active_.exchange(announcing);
  if (!was_announcing && announcing) {
    // A local cue is device feedback, not conversation input. Discard queued
    // microphone samples before the announcement reaches the speaker so the
    // cue cannot be sent back into Codex when barge-in is enabled.
    this->flush_ring_(this->uplink_ring_, this->uplink_mux_);
  } else if (was_announcing && !announcing) {
    this->announcement_ended_ms_.store(millis());
  }
}

void JoydexLanAudio::update_session_state_() {
  if (!this->open_requested_.load() || this->session_active_.load()) {
    return;
  }
  if (this->announcement_active_.load()) {
    return;
  }
  if ((millis() - this->announcement_ended_ms_.load()) < ANNOUNCEMENT_TAIL_MS) {
    return;
  }
  if (!this->is_connected()) {
    return;
  }
  if (!this->uplink_only_ && !this->ensure_speaker_ready_()) {
    return;
  }
  this->open_requested_.store(false);
  this->session_active_.store(true);
  this->opened_notification_pending_.store(true);
  ESP_LOGI(TAG, "PCM session opened after wake acknowledgement drained");
}

bool JoydexLanAudio::ensure_speaker_ready_() {
  if (this->speaker_stop_pending_) {
    if (!this->speaker_->is_stopped()) {
      this->speaker_stop_observed_ = false;
      return false;
    }

    // The resampler reaches STOPPED before its downstream mixer source and
    // shared mixer. Let that cascade settle before queuing START; otherwise the
    // mixer's final STOP cleanup can erase the already-queued START command.
    if (!this->speaker_stop_observed_) {
      this->speaker_stop_observed_ = true;
      this->speaker_stopped_ms_ = millis();
      return false;
    }
    if ((millis() - this->speaker_stopped_ms_) < SPEAKER_RESTART_SETTLE_MS) {
      return false;
    }
    this->speaker_stop_pending_ = false;
    this->speaker_stop_observed_ = false;
    ESP_LOGD(TAG, "Speaker stop cascade settled; restarting Joydex mixer lane");
  }
  if (this->speaker_->is_running()) {
    this->speaker_start_requested_ = false;
    return true;
  }
  if (this->speaker_->is_stopped() && !this->speaker_start_requested_) {
    this->speaker_->set_audio_stream_info(audio::AudioStreamInfo(BITS_PER_SAMPLE, CHANNELS, DOWNLINK_SAMPLE_RATE));
    this->speaker_->start();
    this->speaker_start_requested_ = true;
  }
  return false;
}

void JoydexLanAudio::begin_playback_pause_() {
  const bool was_busy = this->playback_busy_.exchange(true);
  if (!was_busy && !this->barge_in_enabled_.load()) {
    this->flush_ring_(this->uplink_ring_, this->uplink_mux_);
  }
}

void JoydexLanAudio::stop_session_(bool notify_host) {
  const bool was_active = this->session_active_.exchange(false);
  // Logical sessions reuse the same WebSocket. Invalidate work queued by the
  // previous session even when the socket descriptor and connection generation
  // are unchanged.
  this->uplink_session_generation_.fetch_add(1);
  const bool was_opening = this->open_requested_.exchange(false);
  const bool had_session = was_active || was_opening;
  this->playback_busy_.store(false);
  this->playback_end_received_.store(true);
  this->playback_source_busy_ = false;
  this->playback_tail_active_ = false;
  this->flush_audio_();
  // The HTTP server's open callback runs for a raw TCP connection before a
  // WebSocket session exists. Stopping the asynchronous resampler for those
  // empty connections can leave queued STOP commands ahead of the next START,
  // which strands the first real reply until a later session. Only cycle the
  // speaker when an actual PCM session was open or opening.
  if (!this->uplink_only_ && this->speaker_ != nullptr && had_session) {
    this->speaker_stop_pending_ = true;
    this->speaker_stop_observed_ = false;
    this->speaker_start_requested_ = false;
    this->speaker_->stop();
  }
  if (notify_host && had_session) {
    this->closed_notification_pending_.store(true);
  }
}

void JoydexLanAudio::flush_audio_() {
  this->flush_ring_(this->uplink_ring_, this->uplink_mux_);
  this->flush_ring_(this->downlink_ring_, this->downlink_mux_);
  this->uplink_drop_backlog_pending_.store(false);
  this->downlink_pending_length_ = 0;
  this->downlink_pending_offset_ = 0;
}

void JoydexLanAudio::flush_ring_(ByteRing &ring, portMUX_TYPE &mux) {
  portENTER_CRITICAL(&mux);
  ring.head = 0;
  ring.tail = 0;
  ring.fill = 0;
  portEXIT_CRITICAL(&mux);
}

size_t JoydexLanAudio::push_latest_(ByteRing &ring, portMUX_TYPE &mux, const uint8_t *data, size_t length,
                                    std::atomic<uint32_t> &dropped_bytes) {
  if (ring.data == nullptr || ring.capacity == 0 || length == 0) {
    return 0;
  }
  if (length > ring.capacity) {
    const size_t excess = length - ring.capacity;
    data += excess;
    length = ring.capacity;
    dropped_bytes.fetch_add(static_cast<uint32_t>(excess));
  }

  portENTER_CRITICAL(&mux);
  if (length > (ring.capacity - ring.fill)) {
    size_t discard = length - (ring.capacity - ring.fill);
    discard += discard % sizeof(int16_t);
    discard = std::min(discard, ring.fill);
    ring.head = (ring.head + discard) % ring.capacity;
    ring.fill -= discard;
    dropped_bytes.fetch_add(static_cast<uint32_t>(discard));
  }
  const size_t first = std::min(length, ring.capacity - ring.tail);
  std::memcpy(ring.data + ring.tail, data, first);
  if (first < length) {
    std::memcpy(ring.data, data + first, length - first);
  }
  ring.tail = (ring.tail + length) % ring.capacity;
  ring.fill += length;
  const size_t fill = ring.fill;
  portEXIT_CRITICAL(&mux);
  return fill;
}

size_t JoydexLanAudio::pop_(ByteRing &ring, portMUX_TYPE &mux, uint8_t *destination, size_t maximum) {
  portENTER_CRITICAL(&mux);
  const size_t length = std::min(maximum, ring.fill);
  const size_t first = std::min(length, ring.capacity - ring.head);
  std::memcpy(destination, ring.data + ring.head, first);
  if (first < length) {
    std::memcpy(destination + first, ring.data, length - first);
  }
  ring.head = (ring.head + length) % ring.capacity;
  ring.fill -= length;
  portEXIT_CRITICAL(&mux);
  return length;
}

size_t JoydexLanAudio::pop_latest_frame_(ByteRing &ring, portMUX_TYPE &mux, uint8_t *destination, size_t frame_size,
                                         std::atomic<uint32_t> &dropped_bytes) {
  portENTER_CRITICAL(&mux);
  if (ring.fill < frame_size) {
    portEXIT_CRITICAL(&mux);
    return 0;
  }

  // Microphone callbacks are not aligned to our 640-byte WebSocket frames;
  // this board normally supplies 1024 bytes at a time. Preserve the partial
  // next frame and discard only whole stale frames after real backpressure.
  const size_t discard = stale_complete_frame_bytes(ring.fill, frame_size);
  ring.head = (ring.head + discard) % ring.capacity;
  ring.fill -= discard;
  const size_t first = std::min(frame_size, ring.capacity - ring.head);
  std::memcpy(destination, ring.data + ring.head, first);
  if (first < frame_size) {
    std::memcpy(destination + first, ring.data, frame_size - first);
  }
  ring.head = (ring.head + frame_size) % ring.capacity;
  ring.fill -= frame_size;
  portEXIT_CRITICAL(&mux);

  if (discard > 0) {
    dropped_bytes.fetch_add(static_cast<uint32_t>(discard));
  }
  return frame_size;
}

size_t JoydexLanAudio::ring_fill_(ByteRing &ring, portMUX_TYPE &mux) {
  portENTER_CRITICAL(&mux);
  const size_t fill = ring.fill;
  portEXIT_CRITICAL(&mux);
  return fill;
}

void JoydexLanAudio::update_maximum_(std::atomic<uint32_t> &target, uint32_t value) {
  uint32_t current = target.load();
  while (value > current && !target.compare_exchange_weak(current, value)) {
  }
}

bool JoydexLanAudio::queue_uplink_frame_(const uint8_t *data, size_t length) {
  if (data == nullptr || length != UPLINK_FRAME_BYTES || this->server_ == nullptr ||
      this->uplink_send_in_flight_.exchange(true)) {
    return false;
  }

  int socket = -1;
  uint32_t generation = 0;
  portENTER_CRITICAL(&this->client_mux_);
  socket = this->client_socket_.load();
  generation = this->client_generation_.load();
  portEXIT_CRITICAL(&this->client_mux_);
  if (!this->client_is_websocket_(socket)) {
    this->uplink_send_in_flight_.store(false);
    return false;
  }

  std::memcpy(this->uplink_send_buffer_, data, length);
  this->uplink_send_socket_.store(socket);
  this->uplink_send_generation_.store(generation);
  this->uplink_send_session_generation_.store(this->uplink_session_generation_.load());
  this->uplink_send_queued_ms_.store(millis());
  const esp_err_t result = httpd_queue_work(this->server_, JoydexLanAudio::uplink_send_work_, this);
  if (result != ESP_OK) {
    this->websocket_send_failures_.fetch_add(1);
    this->uplink_send_in_flight_.store(false);
    ESP_LOGW(TAG, "Could not queue microphone WebSocket frame; result=%s; socket=%d", esp_err_to_name(result), socket);
    return false;
  }
  return true;
}

void JoydexLanAudio::uplink_send_work_(void *argument) {
  auto *component = static_cast<JoydexLanAudio *>(argument);
  const int socket = component->uplink_send_socket_.load();
  const uint32_t generation = component->uplink_send_generation_.load();
  const uint32_t session_generation = component->uplink_send_session_generation_.load();
  esp_err_t result = ESP_FAIL;
  const uint32_t send_queued_ms = component->uplink_send_queued_ms_.load();
  // Socket descriptors can be reused after a reconnect. A generation match
  // prevents queued work from crossing that protocol-session boundary.
  const bool active_target = socket == component->client_socket_.load() &&
                             generation == component->client_generation_.load() &&
                             session_generation == component->uplink_session_generation_.load() &&
                             component->session_active_.load() &&
                             component->client_is_websocket_(socket);
  if (active_target) {
    httpd_ws_frame_t frame{};
    frame.type = HTTPD_WS_TYPE_BINARY;
    frame.payload = component->uplink_send_buffer_;
    frame.len = UPLINK_FRAME_BYTES;
    // This callback already runs on the HTTP server task, matching ESP-IDF's
    // documented out-of-request WebSocket send pattern without allocating an
    // event group and blocking a second task for every 20 ms microphone frame.
    result = httpd_ws_send_frame_async(component->server_, socket, &frame);
  }
  // Preserve the old metric's meaning: queue wait plus socket write. Measuring
  // only callback execution would hide HTTP-worker congestion from the canary.
  const uint32_t send_duration_ms = millis() - send_queued_ms;
  update_maximum_(component->websocket_send_max_duration_ms_, send_duration_ms);
  if (send_duration_ms >= WEBSOCKET_SEND_STALL_MS) {
    component->websocket_send_stalls_.fetch_add(1);
    if (active_target && result == ESP_OK) {
      // A delayed HTTP-worker send lets old microphone audio accumulate. Mark
      // one compaction for the sender after this in-flight frame completes.
      // Normal 1024-byte source callbacks are drained FIFO and never treated
      // as backpressure merely because they contain 1.6 wire frames.
      component->uplink_drop_backlog_pending_.store(true);
    }
    ESP_LOGW(TAG, "Microphone WebSocket output stalled; duration=%ums; socket=%d; bytes=%u",
             static_cast<unsigned>(send_duration_ms), socket, static_cast<unsigned>(UPLINK_FRAME_BYTES));
  }

  if (result == ESP_OK) {
    component->uplink_frames_.fetch_add(1);
  } else if (active_target) {
    component->websocket_send_failures_.fetch_add(1);
    component->uplink_dropped_bytes_.fetch_add(static_cast<uint32_t>(UPLINK_FRAME_BYTES));
    if (component->fail_socket_if_active_(socket, CloseReason::SEND_FAILED)) {
      component->websocket_forced_closes_.fetch_add(1);
      ESP_LOGE(TAG, "Microphone WebSocket output failed; result=%s; errno=%d; duration=%ums; socket=%d",
               esp_err_to_name(result), static_cast<int>(component->last_socket_errno_.load()),
               static_cast<unsigned>(send_duration_ms), socket);
      httpd_sess_trigger_close(component->server_, socket);
    }
  } else {
    component->uplink_dropped_bytes_.fetch_add(static_cast<uint32_t>(UPLINK_FRAME_BYTES));
  }
  component->uplink_send_in_flight_.store(false);
}

bool JoydexLanAudio::send_frame_(httpd_ws_type_t type, const uint8_t *data, size_t length) {
  const int socket = this->client_socket_.load();
  if (this->websocket_send_mutex_ == nullptr || !this->client_is_websocket_(socket)) {
    return false;
  }

  if (xSemaphoreTake(this->websocket_send_mutex_, pdMS_TO_TICKS(2500)) != pdTRUE) {
    this->websocket_send_failures_.fetch_add(1);
    ESP_LOGW(TAG, "Timed out waiting to serialize WebSocket output; socket=%d; bytes=%u", socket,
             static_cast<unsigned>(length));
    if (this->fail_socket_if_active_(socket, CloseReason::SEND_MUTEX_TIMEOUT)) {
      this->websocket_forced_closes_.fetch_add(1);
      httpd_sess_trigger_close(this->server_, socket);
    }
    return false;
  }

  esp_err_t result = ESP_FAIL;
  const uint32_t send_started_ms = millis();
  if (socket == this->client_socket_.load() && this->client_is_websocket_(socket)) {
    httpd_ws_frame_t frame{};
    frame.type = type;
    frame.payload = const_cast<uint8_t *>(data);
    frame.len = length;
    result = httpd_ws_send_data(this->server_, socket, &frame);
  }
  const uint32_t send_duration_ms = millis() - send_started_ms;
  update_maximum_(this->websocket_send_max_duration_ms_, send_duration_ms);
  if (send_duration_ms >= WEBSOCKET_SEND_STALL_MS) {
    this->websocket_send_stalls_.fetch_add(1);
    ESP_LOGW(TAG, "WebSocket output stalled; duration=%ums; socket=%d; bytes=%u; type=%u",
             static_cast<unsigned>(send_duration_ms), socket, static_cast<unsigned>(length),
             static_cast<unsigned>(type));
  }
  xSemaphoreGive(this->websocket_send_mutex_);

  if (result != ESP_OK) {
    this->websocket_send_failures_.fetch_add(1);
    // A failed send may have written part of a WebSocket frame. No later frame
    // can safely reuse that byte stream, so stop audio and close it immediately.
    if (this->fail_socket_if_active_(socket, CloseReason::SEND_FAILED)) {
      this->websocket_forced_closes_.fetch_add(1);
      ESP_LOGE(TAG, "WebSocket output failed; result=%s; errno=%d; duration=%ums; socket=%d; bytes=%u; type=%u",
               esp_err_to_name(result), static_cast<int>(this->last_socket_errno_.load()),
               static_cast<unsigned>(send_duration_ms), socket, static_cast<unsigned>(length),
               static_cast<unsigned>(type));
      httpd_sess_trigger_close(this->server_, socket);
    }
    return false;
  }
  return true;
}

bool JoydexLanAudio::send_text_(const char *text) {
  if (text == nullptr) {
    return false;
  }
  return this->send_frame_(
      HTTPD_WS_TYPE_TEXT, reinterpret_cast<const uint8_t *>(text), std::strlen(text));
}

bool JoydexLanAudio::client_is_websocket_(int socket) const {
  return this->server_ != nullptr && socket >= 0 &&
         httpd_ws_get_fd_info(this->server_, socket) == HTTPD_WS_CLIENT_WEBSOCKET;
}

bool JoydexLanAudio::microphone_allowed_() const {
  if (!this->session_active_.load() || this->client_socket_.load() < 0) {
    return false;
  }
  if (this->announcement_active_.load() ||
      (millis() - this->announcement_ended_ms_.load()) < UPLINK_ANNOUNCEMENT_TAIL_MS) {
    return false;
  }
  return this->barge_in_enabled_.load() || !this->playback_busy_.load();
}

bool JoydexLanAudio::fail_socket_if_active_(int socket, CloseReason reason) {
  bool active = false;
  portENTER_CRITICAL(&this->client_mux_);
  if (this->client_socket_.load() == socket) {
    active = true;
    this->active_close_reason_.store(reason);
    this->last_close_reason_.store(reason);
    this->last_socket_errno_.store(this->active_socket_errno_.load());
    this->session_active_.store(false);
    this->connection_lost_pending_.store(true);
  }
  portEXIT_CRITICAL(&this->client_mux_);
  return active;
}

void JoydexLanAudio::record_socket_errno_if_active_(int socket, int32_t socket_errno) {
  portENTER_CRITICAL(&this->client_mux_);
  if (this->client_socket_.load() == socket) {
    this->active_socket_errno_.store(socket_errno);
    this->last_socket_errno_.store(socket_errno);
  }
  portEXIT_CRITICAL(&this->client_mux_);
}

void JoydexLanAudio::set_close_reason_(CloseReason reason) {
  this->active_close_reason_.store(reason);
  this->last_close_reason_.store(reason);
  this->last_socket_errno_.store(this->active_socket_errno_.load());
}

const char *JoydexLanAudio::close_reason_name_(CloseReason reason) {
  switch (reason) {
    case CloseReason::NONE:
      return "none";
    case CloseReason::PEER_CLOSE:
      return "peer_close";
    case CloseReason::PEER_DISCONNECT:
      return "peer_disconnect";
    case CloseReason::RECEIVE_HEADER_FAILED:
      return "receive_header_failed";
    case CloseReason::RECEIVE_PAYLOAD_FAILED:
      return "receive_payload_failed";
    case CloseReason::RECEIVE_OVERSIZED:
      return "receive_oversized";
    case CloseReason::SEND_MUTEX_TIMEOUT:
      return "send_mutex_timeout";
    case CloseReason::SEND_FAILED:
      return "send_failed";
    case CloseReason::REPLACED_BY_NEW_CLIENT:
      return "replaced_by_new_client";
    case CloseReason::SHUTDOWN:
      return "shutdown";
  }
  return "unknown";
}

void JoydexLanAudio::uplink_task_(void *argument) {
  auto *component = static_cast<JoydexLanAudio *>(argument);
  uint8_t frame[UPLINK_FRAME_BYTES];
  while (component->task_running_.load()) {
    if (!component->microphone_allowed_()) {
      vTaskDelay(pdMS_TO_TICKS(10));
      continue;
    }
    if (component->uplink_send_in_flight_.load()) {
      vTaskDelay(pdMS_TO_TICKS(UPLINK_IDLE_POLL_MS));
      continue;
    }
    if (component->ring_fill_(component->uplink_ring_, component->uplink_mux_) < UPLINK_FRAME_BYTES) {
      vTaskDelay(pdMS_TO_TICKS(UPLINK_IDLE_POLL_MS));
      continue;
    }
    const bool drop_stale_backlog = component->uplink_drop_backlog_pending_.exchange(false);
    const size_t length = drop_stale_backlog
                              ? component->pop_latest_frame_(component->uplink_ring_, component->uplink_mux_, frame,
                                                             sizeof(frame), component->uplink_dropped_bytes_)
                              : component->pop_(component->uplink_ring_, component->uplink_mux_, frame, sizeof(frame));
    if (length != sizeof(frame) || !component->queue_uplink_frame_(frame, length)) {
      component->uplink_dropped_bytes_.fetch_add(static_cast<uint32_t>(length));
      if (!component->is_connected()) {
        component->connection_lost_pending_.store(true);
      }
    }
  }
  component->uplink_task_handle_.store(nullptr);
  vTaskDelete(nullptr);
}

}  // namespace joydex_lan_audio
}  // namespace esphome
