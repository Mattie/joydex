#pragma once

#include "esphome/components/media_player/media_player.h"
#include "esphome/components/microphone/microphone_source.h"
#include "esphome/components/speaker/speaker.h"
#include "esphome/components/switch/switch.h"
#include "esphome/core/component.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include <esp_http_server.h>
#include <freertos/FreeRTOS.h>
#include <freertos/portmacro.h>
#include <freertos/semphr.h>
#include <freertos/task.h>

namespace esphome {
namespace joydex_lan_audio {

/// Device-hosted microphone/control bridge for one Joydex Voice Session.
///
/// A single trusted-LAN WebSocket client connects to /joydex/audio. Binary
/// microphone frames travel toward Joydex as 16 kHz PCM16 mono. Text frames
/// control the bounded session lifecycle. Optional diagnostic duplex mode also
/// accepts 24 kHz PCM16 mono toward a configured speaker; production
/// uplink-only mode rejects those frames and never owns a speaker component.
/// Audio is never retained beyond the in-memory queues.
class JoydexLanAudio : public Component {
 public:
  void set_microphone_source(microphone::MicrophoneSource *source) { this->microphone_source_ = source; }
  void set_speaker(speaker::Speaker *speaker) { this->speaker_ = speaker; }
  void set_announcement_media_player(media_player::MediaPlayer *player) {
    this->announcement_media_player_ = player;
  }
  void set_barge_in_switch(switch_::Switch *barge_in_switch) { this->barge_in_switch_ = barge_in_switch; }
  void set_port(uint16_t port) { this->port_ = port; }
  void set_uplink_only(bool uplink_only) { this->uplink_only_ = uplink_only; }

  void setup() override;
  void loop() override;
  void on_shutdown() override;
  float get_setup_priority() const override { return setup_priority::AFTER_WIFI; }
  void dump_config() override;

  bool is_connected() const;
  bool is_session_active() const { return this->session_active_.load(); }
  uint32_t get_uplink_frames() const { return this->uplink_frames_.load(); }
  uint32_t get_uplink_dropped_bytes() const { return this->uplink_dropped_bytes_.load(); }
  uint32_t get_uplink_queue_high_water_bytes() const { return this->uplink_queue_high_water_bytes_.load(); }
  uint32_t get_downlink_frames() const { return this->downlink_frames_.load(); }
  uint32_t get_downlink_dropped_bytes() const { return this->downlink_dropped_bytes_.load(); }
  uint32_t get_downlink_underruns() const { return this->downlink_underruns_.load(); }
  uint32_t get_downlink_queue_high_water_bytes() const { return this->downlink_queue_high_water_bytes_.load(); }
  uint32_t get_downlink_speaker_accepted_bytes() const { return this->downlink_speaker_accepted_bytes_.load(); }
  uint32_t get_downlink_speaker_backpressure_events() const {
    return this->downlink_speaker_backpressure_events_.load();
  }
  uint32_t get_downlink_speaker_partial_writes() const { return this->downlink_speaker_partial_writes_.load(); }
  uint32_t get_downlink_max_loop_gap_ms() const { return this->downlink_max_loop_gap_ms_.load(); }
  uint32_t get_websocket_send_failures() const { return this->websocket_send_failures_.load(); }
  uint32_t get_websocket_send_stalls() const { return this->websocket_send_stalls_.load(); }
  uint32_t get_websocket_send_max_duration_ms() const { return this->websocket_send_max_duration_ms_.load(); }
  uint32_t get_websocket_receive_failures() const { return this->websocket_receive_failures_.load(); }
  uint32_t get_websocket_forced_closes() const { return this->websocket_forced_closes_.load(); }
  uint32_t get_websocket_connections() const { return this->websocket_connections_.load(); }
  int32_t get_last_socket_errno() const { return this->last_socket_errno_.load(); }
  const char *get_last_close_reason() const;

 protected:
  static constexpr uint32_t UPLINK_SAMPLE_RATE = 16000;
  static constexpr uint32_t DOWNLINK_SAMPLE_RATE = 24000;
  static constexpr uint8_t BITS_PER_SAMPLE = 16;
  static constexpr uint8_t CHANNELS = 1;
  static constexpr uint32_t FRAME_DURATION_MS = 20;
  static constexpr size_t UPLINK_FRAME_BYTES = UPLINK_SAMPLE_RATE * FRAME_DURATION_MS / 1000 * sizeof(int16_t);
  static constexpr size_t DOWNLINK_FRAME_BYTES = DOWNLINK_SAMPLE_RATE * FRAME_DURATION_MS / 1000 * sizeof(int16_t);
  static constexpr size_t UPLINK_QUEUE_BYTES = UPLINK_SAMPLE_RATE * 400 / 1000 * sizeof(int16_t);
  static constexpr size_t DOWNLINK_QUEUE_BYTES = DOWNLINK_SAMPLE_RATE * 400 / 1000 * sizeof(int16_t);
  static constexpr size_t MAX_DOWNLINK_FRAME_BYTES = DOWNLINK_FRAME_BYTES * 4;
  // The ESPHome application loop can run below the 50 Hz PCM frame cadence.
  // Drain a bounded burst into the resampler's nonblocking ring each pass so
  // queued audio catches up without monopolizing the main task.
  static constexpr size_t MAX_DOWNLINK_DRAIN_BYTES_PER_LOOP = DOWNLINK_FRAME_BYTES * 8;
  static constexpr UBaseType_t UPLINK_TASK_PRIORITY = 1;
  static constexpr uint32_t UPLINK_IDLE_POLL_MS = 10;
  static constexpr uint32_t ANNOUNCEMENT_TAIL_MS = 300;
  static constexpr uint32_t UPLINK_ANNOUNCEMENT_TAIL_MS = 80;
  static constexpr uint32_t PLAYBACK_GAP_HOLD_MS = 250;
  static constexpr uint32_t PLAYBACK_TAIL_MS = 250;
  // A resampler reports STOPPED before its mixer source and the shared mixer
  // have completed their asynchronous stop cascade. Starting immediately can
  // enqueue the mixer's START before its pending STOP clears all event bits,
  // leaving every speaker lane stranded until reboot.
  static constexpr uint32_t SPEAKER_RESTART_SETTLE_MS = 250;
  static constexpr uint32_t WEBSOCKET_SEND_STALL_MS = 100;

  static_assert(UPLINK_FRAME_BYTES == 640, "Joydex uplink must remain 20 ms of 16 kHz PCM16 mono");
  static_assert(DOWNLINK_FRAME_BYTES == 960, "Joydex downlink must remain 20 ms of 24 kHz PCM16 mono");
  static_assert((UPLINK_QUEUE_BYTES % sizeof(int16_t)) == 0, "Uplink queue must preserve PCM16 alignment");
  static_assert((DOWNLINK_QUEUE_BYTES % sizeof(int16_t)) == 0, "Downlink queue must preserve PCM16 alignment");
  struct ByteRing {
    uint8_t *data{nullptr};
    size_t capacity{0};
    size_t head{0};
    size_t tail{0};
    size_t fill{0};
  };

  enum class CloseReason : uint8_t {
    NONE = 0,
    PEER_CLOSE = 1,
    PEER_DISCONNECT = 2,
    RECEIVE_HEADER_FAILED = 3,
    RECEIVE_PAYLOAD_FAILED = 4,
    RECEIVE_OVERSIZED = 5,
    SEND_MUTEX_TIMEOUT = 6,
    SEND_FAILED = 7,
    REPLACED_BY_NEW_CLIENT = 8,
    SHUTDOWN = 9,
  };

  static esp_err_t websocket_handler_(httpd_req_t *request);
  static esp_err_t open_callback_(httpd_handle_t server, int socket);
  static void close_callback_(httpd_handle_t server, int socket);
  static int send_all_(httpd_handle_t server, int socket, const char *data, size_t length, int flags);
  static void release_global_context_(void *context);
  static void uplink_send_work_(void *argument);
  static void uplink_task_(void *argument);

  esp_err_t handle_websocket_(httpd_req_t *request);
  void handle_text_(const char *text, size_t length);
  void handle_downlink_(const uint8_t *data, size_t length);
  void handle_microphone_(const std::vector<uint8_t> &data);
  void update_announcement_state_();
  void update_session_state_();
  bool ensure_speaker_ready_();
  void begin_playback_pause_();
  void stop_session_(bool notify_host);
  void flush_audio_();
  void flush_ring_(ByteRing &ring, portMUX_TYPE &mux);
  size_t push_latest_(ByteRing &ring, portMUX_TYPE &mux, const uint8_t *data, size_t length,
                      std::atomic<uint32_t> &dropped_bytes);
  size_t pop_(ByteRing &ring, portMUX_TYPE &mux, uint8_t *destination, size_t maximum);
  size_t pop_latest_frame_(ByteRing &ring, portMUX_TYPE &mux, uint8_t *destination, size_t frame_size,
                           std::atomic<uint32_t> &dropped_bytes);
  size_t ring_fill_(ByteRing &ring, portMUX_TYPE &mux);
  static void update_maximum_(std::atomic<uint32_t> &target, uint32_t value);
  bool queue_uplink_frame_(const uint8_t *data, size_t length);
  bool send_frame_(httpd_ws_type_t type, const uint8_t *data, size_t length);
  bool send_text_(const char *text);
  bool client_is_websocket_(int socket) const;
  bool microphone_allowed_() const;
  bool fail_socket_if_active_(int socket, CloseReason reason);
  void record_socket_errno_if_active_(int socket, int32_t socket_errno);
  void set_close_reason_(CloseReason reason);
  static const char *close_reason_name_(CloseReason reason);

  microphone::MicrophoneSource *microphone_source_{nullptr};
  speaker::Speaker *speaker_{nullptr};
  media_player::MediaPlayer *announcement_media_player_{nullptr};
  switch_::Switch *barge_in_switch_{nullptr};

  uint16_t port_{8765};
  bool uplink_only_{false};
  httpd_handle_t server_{nullptr};
  std::atomic<int> client_socket_{-1};
  std::atomic<TaskHandle_t> uplink_task_handle_{nullptr};
  StaticSemaphore_t websocket_send_mutex_storage_;
  SemaphoreHandle_t websocket_send_mutex_{nullptr};

  ByteRing uplink_ring_;
  ByteRing downlink_ring_;
  portMUX_TYPE client_mux_ = portMUX_INITIALIZER_UNLOCKED;
  portMUX_TYPE uplink_mux_ = portMUX_INITIALIZER_UNLOCKED;
  portMUX_TYPE downlink_mux_ = portMUX_INITIALIZER_UNLOCKED;
  uint8_t *receive_buffer_{nullptr};
  uint8_t *downlink_scratch_{nullptr};
  uint8_t uplink_send_buffer_[UPLINK_FRAME_BYTES]{};

  std::atomic<bool> task_running_{false};
  std::atomic<bool> uplink_send_in_flight_{false};
  std::atomic<bool> uplink_drop_backlog_pending_{false};
  std::atomic<int> uplink_send_socket_{-1};
  std::atomic<uint32_t> client_generation_{0};
  std::atomic<uint32_t> uplink_send_generation_{0};
  std::atomic<uint32_t> uplink_session_generation_{0};
  std::atomic<uint32_t> uplink_send_session_generation_{0};
  std::atomic<uint32_t> uplink_send_queued_ms_{0};
  std::atomic<bool> hello_pending_{false};
  std::atomic<bool> connection_opened_pending_{false};
  std::atomic<bool> connection_lost_pending_{false};
  std::atomic<bool> close_requested_{false};
  std::atomic<bool> flush_requested_{false};
  std::atomic<bool> open_command_pending_{false};
  std::atomic<bool> pong_pending_{false};
  std::atomic<bool> open_requested_{false};
  std::atomic<bool> session_active_{false};
  std::atomic<bool> announcement_active_{false};
  std::atomic<bool> barge_in_enabled_{false};
  std::atomic<bool> playback_busy_{false};
  std::atomic<bool> playback_end_received_{true};
  std::atomic<bool> opened_notification_pending_{false};
  std::atomic<bool> closed_notification_pending_{false};
  std::atomic<uint32_t> announcement_ended_ms_{0};
  std::atomic<uint32_t> last_downlink_ms_{0};
  std::atomic<bool> underrun_reported_{false};

  bool speaker_stop_pending_{false};
  bool speaker_stop_observed_{false};
  uint32_t speaker_stopped_ms_{0};
  bool speaker_start_requested_{false};
  bool playback_source_busy_{false};
  bool playback_tail_active_{false};
  uint32_t playback_drained_ms_{0};
  uint32_t last_active_loop_ms_{0};
  size_t downlink_pending_length_{0};
  size_t downlink_pending_offset_{0};

  std::atomic<uint32_t> uplink_frames_{0};
  std::atomic<uint32_t> uplink_dropped_bytes_{0};
  std::atomic<uint32_t> uplink_queue_high_water_bytes_{0};
  std::atomic<uint32_t> downlink_frames_{0};
  std::atomic<uint32_t> downlink_dropped_bytes_{0};
  std::atomic<uint32_t> downlink_underruns_{0};
  std::atomic<uint32_t> downlink_queue_high_water_bytes_{0};
  std::atomic<uint32_t> downlink_speaker_accepted_bytes_{0};
  std::atomic<uint32_t> downlink_speaker_backpressure_events_{0};
  std::atomic<uint32_t> downlink_speaker_partial_writes_{0};
  std::atomic<uint32_t> downlink_max_loop_gap_ms_{0};
  std::atomic<uint32_t> websocket_send_failures_{0};
  std::atomic<uint32_t> websocket_send_stalls_{0};
  std::atomic<uint32_t> websocket_send_max_duration_ms_{0};
  std::atomic<uint32_t> websocket_receive_failures_{0};
  std::atomic<uint32_t> websocket_forced_closes_{0};
  std::atomic<uint32_t> websocket_connections_{0};
  std::atomic<int32_t> active_socket_errno_{0};
  std::atomic<CloseReason> active_close_reason_{CloseReason::NONE};
  std::atomic<int32_t> last_socket_errno_{0};
  std::atomic<CloseReason> last_close_reason_{CloseReason::NONE};
};

}  // namespace joydex_lan_audio
}  // namespace esphome
