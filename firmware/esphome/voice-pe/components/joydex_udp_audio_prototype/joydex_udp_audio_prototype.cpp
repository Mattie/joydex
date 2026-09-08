#include "joydex_udp_audio_prototype.h"

#include "esphome/components/audio/audio.h"
#include "esphome/core/helpers.h"
#include "esphome/core/log.h"

#include <algorithm>
#include <cerrno>
#include <cstring>

#include <esp_heap_caps.h>
#include <fcntl.h>
#include <freertos/idf_additions.h>
#include <lwip/inet.h>

namespace esphome {
namespace joydex_udp_audio_prototype {

static const char *const TAG = "joydex.udp_audio";

void JoydexUdpAudioPrototype::setup() {
  if (this->microphone_source_ == nullptr || this->speaker_ == nullptr ||
      this->announcement_media_player_ == nullptr || this->barge_in_switch_ == nullptr) {
    ESP_LOGE(TAG, "Required audio or lifecycle component is missing");
    this->mark_failed();
    return;
  }

  const auto microphone_info = this->microphone_source_->get_audio_stream_info();
  if (microphone_info.get_sample_rate() != UPLINK_SAMPLE_RATE ||
      microphone_info.get_bits_per_sample() != BITS_PER_SAMPLE || microphone_info.get_channels() != CHANNELS) {
    ESP_LOGE(TAG, "wake_word_mic must be 16 kHz, mono, signed 16-bit PCM (got %u Hz, %u bit, %u channel)",
             microphone_info.get_sample_rate(), microphone_info.get_bits_per_sample(), microphone_info.get_channels());
    this->mark_failed();
    return;
  }

  this->uplink_ring_.capacity = UPLINK_QUEUE_BYTES;
  this->downlink_ring_.capacity = DOWNLINK_QUEUE_BYTES;
  this->uplink_ring_.data =
      static_cast<uint8_t *>(heap_caps_malloc(UPLINK_QUEUE_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  this->downlink_ring_.data =
      static_cast<uint8_t *>(heap_caps_malloc(DOWNLINK_QUEUE_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  this->receive_packet_ =
      static_cast<uint8_t *>(heap_caps_malloc(RECEIVE_BUFFER_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  this->send_packet_buffer_ =
      static_cast<uint8_t *>(heap_caps_malloc(MAX_PACKET_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  this->downlink_scratch_ =
      static_cast<uint8_t *>(heap_caps_malloc(DOWNLINK_FRAME_BYTES, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  if (this->uplink_ring_.data == nullptr || this->downlink_ring_.data == nullptr ||
      this->receive_packet_ == nullptr || this->send_packet_buffer_ == nullptr || this->downlink_scratch_ == nullptr) {
    ESP_LOGE(TAG, "Could not allocate bounded UDPPCM queues in PSRAM");
    this->mark_failed();
    return;
  }

  this->speaker_->set_audio_stream_info(audio::AudioStreamInfo(BITS_PER_SAMPLE, CHANNELS, DOWNLINK_SAMPLE_RATE));
  this->speaker_->start();
  this->speaker_start_requested_ = true;

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

  this->socket_ = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
  if (this->socket_ < 0) {
    this->last_socket_errno_.store(errno);
    ESP_LOGE(TAG, "Could not create UDPPCM socket; errno=%d", errno);
    this->mark_failed();
    return;
  }

  int enabled = 1;
  setsockopt(this->socket_, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
  int socket_buffer_bytes = 32 * 1024;
  setsockopt(this->socket_, SOL_SOCKET, SO_RCVBUF, &socket_buffer_bytes, sizeof(socket_buffer_bytes));
  setsockopt(this->socket_, SOL_SOCKET, SO_SNDBUF, &socket_buffer_bytes, sizeof(socket_buffer_bytes));

  const int existing_flags = fcntl(this->socket_, F_GETFL, 0);
  if (existing_flags < 0 || fcntl(this->socket_, F_SETFL, existing_flags | O_NONBLOCK) < 0) {
    this->last_socket_errno_.store(errno);
    ESP_LOGE(TAG, "Could not make UDPPCM socket nonblocking; errno=%d", errno);
    close(this->socket_);
    this->socket_ = -1;
    this->mark_failed();
    return;
  }

  sockaddr_in local{};
  local.sin_family = AF_INET;
  local.sin_addr.s_addr = htonl(INADDR_ANY);
  local.sin_port = htons(this->port_);
  if (bind(this->socket_, reinterpret_cast<const sockaddr *>(&local), sizeof(local)) < 0) {
    this->last_socket_errno_.store(errno);
    ESP_LOGE(TAG, "Could not bind UDPPCM socket on port %u; errno=%d", this->port_, errno);
    close(this->socket_);
    this->socket_ = -1;
    this->mark_failed();
    return;
  }

  this->task_running_.store(true);
  TaskHandle_t network_task_handle = nullptr;
  const BaseType_t task_result = xTaskCreatePinnedToCoreWithCaps(
      JoydexUdpAudioPrototype::network_task_, "joydex_udp_pcm", 4096, this, NETWORK_TASK_PRIORITY,
      &network_task_handle, tskNO_AFFINITY, MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT);
  if (task_result != pdPASS) {
    ESP_LOGE(TAG, "Could not start UDPPCM network task");
    this->task_running_.store(false);
    close(this->socket_);
    this->socket_ = -1;
    this->mark_failed();
    return;
  }
  this->network_task_handle_.store(network_task_handle);
  ESP_LOGI(TAG, "UDPPCM prototype listening on udp://<device>:%u", this->port_);
}

void JoydexUdpAudioPrototype::loop() {
  const bool connection_lost = this->connection_lost_pending_.exchange(false);
  const bool close_requested = this->close_requested_.exchange(false);
  if (connection_lost || close_requested) {
    this->stop_session_();
  }
  if (this->open_command_pending_.exchange(false)) {
    this->stop_session_();
    this->session_maxima_reset_pending_ = true;
  }
  if (this->session_maxima_reset_pending_) {
    if (!this->peer_present_.load()) {
      this->session_maxima_reset_pending_ = false;
    } else if (this->microphone_callbacks_active_.load() == 0) {
      this->reset_session_maxima_();
      this->session_maxima_reset_pending_ = false;
      this->open_requested_.store(true);
    }
  }
  this->update_session_state_();
  if (!this->session_active_.load()) {
    if (this->speaker_ != nullptr && (this->speaker_stop_pending_ || !this->speaker_->is_running())) {
      this->ensure_speaker_ready_();
    }
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

void JoydexUdpAudioPrototype::on_shutdown() {
  this->last_close_reason_.store(CloseReason::SHUTDOWN);
  this->session_active_.store(false);
  this->peer_present_.store(false);
  this->task_running_.store(false);
  for (uint16_t wait = 0; wait < 250 && this->network_task_handle_.load() != nullptr; wait++) {
    vTaskDelay(pdMS_TO_TICKS(10));
  }
  if (this->network_task_handle_.load() != nullptr) {
    ESP_LOGW(TAG, "UDPPCM network task did not stop before platform teardown");
  }
  this->stop_session_();
}

void JoydexUdpAudioPrototype::dump_config() {
  ESP_LOGCONFIG(TAG, "Joydex UDPPCM prototype:");
  ESP_LOGCONFIG(TAG, "  Endpoint: udp://<device>:%u", this->port_);
  ESP_LOGCONFIG(TAG, "  Uplink: 16 kHz PCM16 mono, 20 ms / 640-byte datagrams");
  ESP_LOGCONFIG(TAG, "  Downlink: 48 kHz PCM16 mono, 10 ms / 960-byte datagrams");
  ESP_LOGCONFIG(TAG, "  Queue bound: 400 ms per direction, oldest audio discarded");
  ESP_LOGCONFIG(TAG, "  Host timeout: %u ms", HOST_TIMEOUT_MS);
  ESP_LOGCONFIG(TAG, "  Barge-in default: %s", this->barge_in_switch_->state ? "ON" : "OFF");
}

uint16_t JoydexUdpAudioPrototype::read_u16_(const uint8_t *data) {
  return static_cast<uint16_t>((static_cast<uint16_t>(data[0]) << 8) | data[1]);
}

uint32_t JoydexUdpAudioPrototype::read_u32_(const uint8_t *data) {
  return (static_cast<uint32_t>(data[0]) << 24) | (static_cast<uint32_t>(data[1]) << 16) |
         (static_cast<uint32_t>(data[2]) << 8) | static_cast<uint32_t>(data[3]);
}

void JoydexUdpAudioPrototype::write_u16_(uint8_t *data, uint16_t value) {
  data[0] = static_cast<uint8_t>(value >> 8);
  data[1] = static_cast<uint8_t>(value);
}

void JoydexUdpAudioPrototype::write_u32_(uint8_t *data, uint32_t value) {
  data[0] = static_cast<uint8_t>(value >> 24);
  data[1] = static_cast<uint8_t>(value >> 16);
  data[2] = static_cast<uint8_t>(value >> 8);
  data[3] = static_cast<uint8_t>(value);
}

void JoydexUdpAudioPrototype::network_task_(void *argument) {
  auto *component = static_cast<JoydexUdpAudioPrototype *>(argument);
  component->run_network_task_();
  component->network_task_handle_.store(nullptr);
  vTaskDelete(nullptr);
}

void JoydexUdpAudioPrototype::run_network_task_() {
  uint8_t microphone_frame[UPLINK_FRAME_BYTES];
  while (this->task_running_.load()) {
    for (uint8_t attempt = 0; attempt < MAX_RECEIVE_BURST; attempt++) {
      sockaddr_in sender{};
      socklen_t sender_length = sizeof(sender);
      const int received = recvfrom(this->socket_, this->receive_packet_, RECEIVE_BUFFER_BYTES, 0,
                                    reinterpret_cast<sockaddr *>(&sender), &sender_length);
      if (received < 0) {
        if (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR) {
          this->last_socket_errno_.store(errno);
        }
        break;
      }
      this->handle_datagram_(this->receive_packet_, static_cast<size_t>(received), sender);
    }

    if (this->ready_notification_pending_.load() && this->session_active_.load() &&
        this->send_packet_(PacketType::READY, 0)) {
      this->ready_notification_pending_.store(false);
    }

    if (this->playback_end_pending_.load() &&
        (millis() - this->playback_end_pending_since_ms_) >= PLAYBACK_END_REORDER_HOLD_MS) {
      if (this->pending_playback_end_sequence_ > this->expected_speaker_sequence_) {
        this->udp_downlink_missing_packets_.fetch_add(this->pending_playback_end_sequence_ -
                                                       this->expected_speaker_sequence_);
        this->expected_speaker_sequence_ = this->pending_playback_end_sequence_;
      }
      this->playback_end_pending_.store(false);
      this->playback_end_received_.store(true);
    }

    if (this->microphone_allowed_() &&
        this->ring_fill_(this->uplink_ring_, this->uplink_mux_) >= UPLINK_FRAME_BYTES) {
      const size_t length = this->pop_(this->uplink_ring_, this->uplink_mux_, microphone_frame, sizeof(microphone_frame));
      if (length != sizeof(microphone_frame) ||
          !this->send_packet_(PacketType::MICROPHONE, this->microphone_sequence_++, microphone_frame, length)) {
        this->uplink_dropped_bytes_.fetch_add(static_cast<uint32_t>(length));
        this->udp_uplink_send_drops_.fetch_add(1);
      } else {
        this->uplink_frames_.fetch_add(1);
      }
    }

    if (this->peer_present_.load() &&
        (millis() - this->last_host_packet_ms_.load()) > HOST_TIMEOUT_MS) {
      this->udp_timeouts_.fetch_add(1);
      this->expire_peer_(CloseReason::HOST_TIMEOUT);
    }
    vTaskDelay(pdMS_TO_TICKS(NETWORK_IDLE_POLL_MS));
  }

  if (this->socket_ >= 0) {
    close(this->socket_);
    this->socket_ = -1;
  }
}

void JoydexUdpAudioPrototype::handle_datagram_(const uint8_t *packet, size_t length, const sockaddr_in &sender) {
  if (length < HEADER_BYTES || length > MAX_PACKET_BYTES || read_u32_(packet) != MAGIC ||
      packet[4] != PROTOCOL_VERSION) {
    this->udp_invalid_packets_.fetch_add(1);
    return;
  }

  const auto type = static_cast<PacketType>(packet[5]);
  const size_t payload_length = read_u16_(packet + 6);
  const uint32_t session_id = read_u32_(packet + 8);
  const uint32_t sequence = read_u32_(packet + 12);
  if (payload_length != length - HEADER_BYTES || session_id == 0) {
    this->udp_invalid_packets_.fetch_add(1);
    return;
  }

  if (type == PacketType::OPEN) {
    if (payload_length != 0) {
      this->udp_invalid_packets_.fetch_add(1);
      return;
    }
    this->handle_open_(session_id, sender);
    return;
  }

  if (!this->peer_present_.load() || session_id != this->session_id_.load() || !this->sender_matches_(sender)) {
    this->udp_foreign_packets_.fetch_add(1);
    return;
  }
  switch (type) {
    case PacketType::CLOSE:
      if (payload_length != 0) {
        this->udp_invalid_packets_.fetch_add(1);
        return;
      }
      this->last_host_packet_ms_.store(millis());
      // A final acknowledgement has no later packet available to reveal its
      // loss. Send a tiny burst so a single LAN datagram loss does not make
      // the host report a leaked session.
      for (uint8_t acknowledgement = 0; acknowledgement < CLOSE_ACK_BURST; acknowledgement++) {
        this->send_packet_(PacketType::CLOSED, acknowledgement);
      }
      this->last_close_reason_.store(CloseReason::HOST_CLOSE);
      this->peer_present_.store(false);
      this->session_id_.store(0);
      this->close_requested_.store(true);
      break;
    case PacketType::PING:
      if (payload_length == 0) {
        this->last_host_packet_ms_.store(millis());
        this->send_packet_(PacketType::PONG, sequence);
      } else {
        this->udp_invalid_packets_.fetch_add(1);
      }
      break;
    case PacketType::PLAYBACK_END:
      if (payload_length == 0) {
        this->last_host_packet_ms_.store(millis());
        if (sequence <= this->expected_speaker_sequence_) {
          this->playback_end_pending_.store(false);
          this->playback_end_received_.store(true);
        } else {
          this->playback_end_pending_.store(true);
          this->pending_playback_end_sequence_ = sequence;
          this->playback_end_pending_since_ms_ = millis();
        }
      } else {
        this->udp_invalid_packets_.fetch_add(1);
      }
      break;
    case PacketType::SPEAKER:
      if (payload_length == DOWNLINK_FRAME_BYTES) {
        this->last_host_packet_ms_.store(millis());
      }
      this->handle_speaker_(sequence, packet + HEADER_BYTES, payload_length);
      break;
    default:
      this->udp_invalid_packets_.fetch_add(1);
      break;
  }
}

void JoydexUdpAudioPrototype::handle_open_(uint32_t session_id, const sockaddr_in &sender) {
  if (this->peer_present_.load() && session_id == this->session_id_.load() && this->sender_matches_(sender)) {
    this->last_host_packet_ms_.store(millis());
    if (this->session_active_.load()) {
      this->send_packet_(PacketType::READY, 0);
    }
    return;
  }

  if (this->peer_present_.load()) {
    this->last_close_reason_.store(CloseReason::REPLACED_BY_OPEN);
  }
  this->peer_address_ = sender;
  this->session_id_.store(session_id);
  this->peer_present_.store(true);
  this->last_host_packet_ms_.store(millis());
  this->session_active_.store(false);
  this->speaker_sequence_started_ = false;
  this->expected_speaker_sequence_ = 0;
  this->playback_end_pending_.store(false);
  this->pending_playback_end_sequence_ = 0;
  this->playback_end_pending_since_ms_ = 0;
  this->microphone_sequence_ = 0;
  // Network-task timing is reset here because no previous-session send can
  // still be in flight on this same task.
  this->udp_uplink_send_max_us_.store(0);
  this->ready_notification_pending_.store(false);
  this->open_command_pending_.store(true);
  this->udp_sessions_.fetch_add(1);
  ESP_LOGI(TAG, "UDPPCM prototype accepted session %08x", static_cast<unsigned>(session_id));
}

void JoydexUdpAudioPrototype::handle_speaker_(uint32_t sequence, const uint8_t *payload, size_t length) {
  if (!this->session_active_.load() || length != DOWNLINK_FRAME_BYTES) {
    this->downlink_dropped_bytes_.fetch_add(static_cast<uint32_t>(length));
    if (length != DOWNLINK_FRAME_BYTES) {
      this->udp_invalid_packets_.fetch_add(1);
    }
    return;
  }

  if (!this->speaker_sequence_started_) {
    this->speaker_sequence_started_ = true;
    this->expected_speaker_sequence_ = 0;
  }
  if (sequence < this->expected_speaker_sequence_) {
    this->udp_downlink_late_packets_.fetch_add(1);
    return;
  }
  if (sequence > this->expected_speaker_sequence_) {
    this->udp_downlink_missing_packets_.fetch_add(sequence - this->expected_speaker_sequence_);
  }
  this->expected_speaker_sequence_ = sequence + 1;

  this->begin_playback_pause_();
  this->playback_end_received_.store(false);
  this->last_downlink_ms_.store(millis());
  this->underrun_reported_.store(false);
  this->push_latest_(this->downlink_ring_, this->downlink_mux_, payload, length, this->downlink_dropped_bytes_,
                     this->downlink_queue_high_water_bytes_);
  this->downlink_frames_.fetch_add(1);
  if (this->playback_end_pending_.load() &&
      this->expected_speaker_sequence_ >= this->pending_playback_end_sequence_) {
    this->playback_end_pending_.store(false);
    this->playback_end_received_.store(true);
  }
}

bool JoydexUdpAudioPrototype::sender_matches_(const sockaddr_in &sender) const {
  return sender.sin_family == this->peer_address_.sin_family &&
         sender.sin_port == this->peer_address_.sin_port &&
         sender.sin_addr.s_addr == this->peer_address_.sin_addr.s_addr;
}

bool JoydexUdpAudioPrototype::send_packet_(PacketType type, uint32_t sequence, const uint8_t *payload, size_t length) {
  if (this->socket_ < 0 || !this->peer_present_.load() || length > DOWNLINK_FRAME_BYTES ||
      (length > 0 && payload == nullptr)) {
    return false;
  }
  write_u32_(this->send_packet_buffer_, MAGIC);
  this->send_packet_buffer_[4] = PROTOCOL_VERSION;
  this->send_packet_buffer_[5] = static_cast<uint8_t>(type);
  write_u16_(this->send_packet_buffer_ + 6, static_cast<uint16_t>(length));
  write_u32_(this->send_packet_buffer_ + 8, this->session_id_.load());
  write_u32_(this->send_packet_buffer_ + 12, sequence);
  if (length > 0) {
    std::memcpy(this->send_packet_buffer_ + HEADER_BYTES, payload, length);
  }

  const uint32_t started_us = micros();
  const int sent = sendto(this->socket_, this->send_packet_buffer_, HEADER_BYTES + length, 0,
                          reinterpret_cast<const sockaddr *>(&this->peer_address_), sizeof(this->peer_address_));
  const uint32_t duration_us = micros() - started_us;
  if (type == PacketType::MICROPHONE) {
    update_maximum_(this->udp_uplink_send_max_us_, duration_us);
  }
  if (sent != static_cast<int>(HEADER_BYTES + length)) {
    this->last_socket_errno_.store(sent < 0 ? errno : 0);
    return false;
  }
  return true;
}

void JoydexUdpAudioPrototype::expire_peer_(CloseReason reason) {
  if (!this->peer_present_.exchange(false)) {
    return;
  }
  this->last_close_reason_.store(reason);
  this->session_id_.store(0);
  this->session_active_.store(false);
  this->connection_lost_pending_.store(true);
  ESP_LOGW(TAG, "UDPPCM prototype session expired; reason=%s", close_reason_name_(reason));
}

void JoydexUdpAudioPrototype::handle_microphone_(const std::vector<uint8_t> &data) {
  this->microphone_callbacks_active_.fetch_add(1);
  if (!this->microphone_allowed_() || data.empty()) {
    this->microphone_callbacks_active_.fetch_sub(1);
    return;
  }
  if ((data.size() % sizeof(int16_t)) != 0) {
    this->uplink_dropped_bytes_.fetch_add(static_cast<uint32_t>(data.size()));
    this->microphone_callbacks_active_.fetch_sub(1);
    return;
  }
  this->push_latest_(this->uplink_ring_, this->uplink_mux_, data.data(), data.size(), this->uplink_dropped_bytes_,
                     this->uplink_queue_high_water_bytes_);
  this->microphone_callbacks_active_.fetch_sub(1);
}

void JoydexUdpAudioPrototype::update_announcement_state_() {
  const bool announcing = this->announcement_media_player_->state == media_player::MEDIA_PLAYER_STATE_ANNOUNCING;
  const bool was_announcing = this->announcement_active_.exchange(announcing);
  if (!was_announcing && announcing) {
    this->flush_ring_(this->uplink_ring_, this->uplink_mux_);
  } else if (was_announcing && !announcing) {
    this->announcement_ended_ms_.store(millis());
  }
}

void JoydexUdpAudioPrototype::update_session_state_() {
  if (!this->open_requested_.load() || this->session_active_.load()) {
    return;
  }
  if (this->announcement_active_.load() ||
      (millis() - this->announcement_ended_ms_.load()) < SESSION_OPEN_ANNOUNCEMENT_TAIL_MS ||
      !this->peer_present_.load() || !this->ensure_speaker_ready_()) {
    return;
  }
  this->flush_audio_();
  this->open_requested_.store(false);
  this->session_active_.store(true);
  this->ready_notification_pending_.store(true);
  ESP_LOGI(TAG, "UDPPCM session ready after local acknowledgement drained");
}

bool JoydexUdpAudioPrototype::ensure_speaker_ready_() {
  if (this->speaker_stop_pending_) {
    if (!this->speaker_->is_stopped()) {
      return false;
    }
    this->speaker_stop_pending_ = false;
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

void JoydexUdpAudioPrototype::begin_playback_pause_() {
  const bool was_busy = this->playback_busy_.exchange(true);
  if (!was_busy && !this->barge_in_enabled_.load()) {
    this->flush_ring_(this->uplink_ring_, this->uplink_mux_);
  }
}

void JoydexUdpAudioPrototype::stop_session_() {
  this->session_active_.store(false);
  this->open_requested_.store(false);
  this->ready_notification_pending_.store(false);
  this->playback_busy_.store(false);
  this->playback_end_received_.store(true);
  this->playback_end_pending_.store(false);
  this->playback_source_busy_ = false;
  this->playback_tail_active_ = false;
  this->flush_audio_();
  // Reset the source on every lifecycle boundary, including an OPEN that
  // replaces an active peer and a timeout that already cleared active state.
  // That prevents buffered audio from one session leaking into the next.
  if (this->speaker_ != nullptr && !this->speaker_stop_pending_) {
    this->speaker_stop_pending_ = true;
    this->speaker_start_requested_ = false;
    this->speaker_->stop();
  }
}

void JoydexUdpAudioPrototype::reset_session_maxima_() {
  // The OPEN transition calls this only after session deactivation and after
  // every old microphone callback exits. Ring locks also serialize this reset
  // with network-task queue writes.
  portENTER_CRITICAL(&this->uplink_mux_);
  this->uplink_ring_.head = 0;
  this->uplink_ring_.tail = 0;
  this->uplink_ring_.fill = 0;
  this->uplink_queue_high_water_bytes_.store(0);
  portEXIT_CRITICAL(&this->uplink_mux_);
  portENTER_CRITICAL(&this->downlink_mux_);
  this->downlink_ring_.head = 0;
  this->downlink_ring_.tail = 0;
  this->downlink_ring_.fill = 0;
  this->downlink_queue_high_water_bytes_.store(0);
  portEXIT_CRITICAL(&this->downlink_mux_);
  this->downlink_max_loop_gap_ms_.store(0);
  this->last_active_loop_ms_ = 0;
}

void JoydexUdpAudioPrototype::flush_audio_() {
  this->flush_ring_(this->uplink_ring_, this->uplink_mux_);
  this->flush_ring_(this->downlink_ring_, this->downlink_mux_);
  this->downlink_pending_length_ = 0;
  this->downlink_pending_offset_ = 0;
}

void JoydexUdpAudioPrototype::flush_ring_(ByteRing &ring, portMUX_TYPE &mux) {
  portENTER_CRITICAL(&mux);
  ring.head = 0;
  ring.tail = 0;
  ring.fill = 0;
  portEXIT_CRITICAL(&mux);
}

size_t JoydexUdpAudioPrototype::push_latest_(ByteRing &ring, portMUX_TYPE &mux, const uint8_t *data, size_t length,
                                             std::atomic<uint32_t> &dropped_bytes,
                                             std::atomic<uint32_t> &high_water_bytes) {
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
  update_maximum_(high_water_bytes, static_cast<uint32_t>(fill));
  portEXIT_CRITICAL(&mux);
  return fill;
}

size_t JoydexUdpAudioPrototype::pop_(ByteRing &ring, portMUX_TYPE &mux, uint8_t *destination, size_t maximum) {
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

size_t JoydexUdpAudioPrototype::ring_fill_(ByteRing &ring, portMUX_TYPE &mux) {
  portENTER_CRITICAL(&mux);
  const size_t fill = ring.fill;
  portEXIT_CRITICAL(&mux);
  return fill;
}

void JoydexUdpAudioPrototype::update_maximum_(std::atomic<uint32_t> &target, uint32_t value) {
  uint32_t current = target.load();
  while (value > current && !target.compare_exchange_weak(current, value)) {
  }
}

bool JoydexUdpAudioPrototype::microphone_allowed_() const {
  if (!this->session_active_.load() || !this->peer_present_.load()) {
    return false;
  }
  if (this->announcement_active_.load() ||
      (millis() - this->announcement_ended_ms_.load()) < UPLINK_ANNOUNCEMENT_TAIL_MS) {
    return false;
  }
  return this->barge_in_enabled_.load() || !this->playback_busy_.load();
}

const char *JoydexUdpAudioPrototype::get_last_close_reason() const {
  return close_reason_name_(this->last_close_reason_.load());
}

const char *JoydexUdpAudioPrototype::close_reason_name_(CloseReason reason) {
  switch (reason) {
    case CloseReason::NONE:
      return "none";
    case CloseReason::HOST_CLOSE:
      return "host_close";
    case CloseReason::HOST_TIMEOUT:
      return "host_timeout";
    case CloseReason::REPLACED_BY_OPEN:
      return "replaced_by_open";
    case CloseReason::SHUTDOWN:
      return "shutdown";
  }
  return "unknown";
}

}  // namespace joydex_udp_audio_prototype
}  // namespace esphome
