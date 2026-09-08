#pragma once

#include "esphome/components/media_player/media_player.h"
#include "esphome/components/microphone/microphone_source.h"
#include "esphome/components/speaker/speaker.h"
#include "esphome/components/switch/switch.h"
#include "esphome/core/component.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <vector>

#include <freertos/FreeRTOS.h>
#include <freertos/portmacro.h>
#include <freertos/task.h>
#include <lwip/sockets.h>

namespace esphome {
namespace joydex_udp_audio_prototype {

/// Throwaway source-only UDPPCM bridge for one Joydex Voice Session.
///
/// This component exists to test whether bounded, independent datagrams can
/// sustain Voice PE microphone and speaker PCM. It is not the adopted product
/// transport. Audio stays in bounded PSRAM queues and is never persisted.
class JoydexUdpAudioPrototype : public Component {
 public:
  void set_microphone_source(microphone::MicrophoneSource *source) { this->microphone_source_ = source; }
  void set_speaker(speaker::Speaker *speaker) { this->speaker_ = speaker; }
  void set_announcement_media_player(media_player::MediaPlayer *player) {
    this->announcement_media_player_ = player;
  }
  void set_barge_in_switch(switch_::Switch *barge_in_switch) { this->barge_in_switch_ = barge_in_switch; }
  void set_port(uint16_t port) { this->port_ = port; }

  void setup() override;
  void loop() override;
  void on_shutdown() override;
  float get_setup_priority() const override { return setup_priority::AFTER_WIFI; }
  void dump_config() override;

  bool is_connected() const { return this->peer_present_.load(); }
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
  uint32_t get_udp_uplink_send_drops() const { return this->udp_uplink_send_drops_.load(); }
  uint32_t get_udp_uplink_send_max_us() const { return this->udp_uplink_send_max_us_.load(); }
  uint32_t get_udp_invalid_packets() const { return this->udp_invalid_packets_.load(); }
  uint32_t get_udp_foreign_packets() const { return this->udp_foreign_packets_.load(); }
  uint32_t get_udp_downlink_missing_packets() const { return this->udp_downlink_missing_packets_.load(); }
  uint32_t get_udp_downlink_late_packets() const { return this->udp_downlink_late_packets_.load(); }
  uint32_t get_udp_sessions() const { return this->udp_sessions_.load(); }
  uint32_t get_udp_timeouts() const { return this->udp_timeouts_.load(); }
  int32_t get_last_socket_errno() const { return this->last_socket_errno_.load(); }
  const char *get_last_close_reason() const;

 protected:
  static constexpr uint32_t MAGIC = 0x4A445855;  // JDXU
  static constexpr uint8_t PROTOCOL_VERSION = 1;
  static constexpr size_t HEADER_BYTES = 16;
  static constexpr uint32_t UPLINK_SAMPLE_RATE = 16000;
  static constexpr uint32_t DOWNLINK_SAMPLE_RATE = 48000;
  static constexpr uint8_t BITS_PER_SAMPLE = 16;
  static constexpr uint8_t CHANNELS = 1;
  static constexpr uint32_t UPLINK_FRAME_MS = 20;
  static constexpr uint32_t DOWNLINK_FRAME_MS = 10;
  static constexpr size_t UPLINK_FRAME_BYTES =
      UPLINK_SAMPLE_RATE * UPLINK_FRAME_MS / 1000 * sizeof(int16_t);
  static constexpr size_t DOWNLINK_FRAME_BYTES =
      DOWNLINK_SAMPLE_RATE * DOWNLINK_FRAME_MS / 1000 * sizeof(int16_t);
  static constexpr size_t UPLINK_QUEUE_BYTES = UPLINK_SAMPLE_RATE * 400 / 1000 * sizeof(int16_t);
  static constexpr size_t DOWNLINK_QUEUE_BYTES = DOWNLINK_SAMPLE_RATE * 400 / 1000 * sizeof(int16_t);
  static constexpr size_t MAX_PACKET_BYTES = HEADER_BYTES + DOWNLINK_FRAME_BYTES;
  static constexpr size_t RECEIVE_BUFFER_BYTES = MAX_PACKET_BYTES + 1;
  static constexpr size_t MAX_DOWNLINK_DRAIN_BYTES_PER_LOOP = DOWNLINK_FRAME_BYTES * 8;
  static constexpr UBaseType_t NETWORK_TASK_PRIORITY = 1;
  static constexpr uint32_t NETWORK_IDLE_POLL_MS = 2;
  static constexpr uint32_t SESSION_OPEN_ANNOUNCEMENT_TAIL_MS = 300;
  static constexpr uint32_t UPLINK_ANNOUNCEMENT_TAIL_MS = 80;
  static constexpr uint32_t PLAYBACK_GAP_HOLD_MS = 250;
  static constexpr uint32_t PLAYBACK_TAIL_MS = 250;
  static constexpr uint32_t PLAYBACK_END_REORDER_HOLD_MS = 30;
  static constexpr uint32_t HOST_TIMEOUT_MS = 3500;
  static constexpr uint8_t MAX_RECEIVE_BURST = 16;
  static constexpr uint8_t CLOSE_ACK_BURST = 3;

  static_assert(UPLINK_FRAME_BYTES == 640, "UDPPCM uplink must remain 20 ms of 16 kHz PCM16 mono");
  static_assert(DOWNLINK_FRAME_BYTES == 960, "UDPPCM downlink must remain 10 ms of 48 kHz PCM16 mono");
  static_assert(MAX_PACKET_BYTES < 1200, "UDPPCM packets must remain comfortably below the LAN MTU");

  enum class PacketType : uint8_t {
    OPEN = 1,
    READY = 2,
    CLOSE = 3,
    CLOSED = 4,
    PING = 5,
    PONG = 6,
    PLAYBACK_END = 7,
    SPEAKER = 16,
    MICROPHONE = 17,
  };

  enum class CloseReason : uint8_t {
    NONE = 0,
    HOST_CLOSE = 1,
    HOST_TIMEOUT = 2,
    REPLACED_BY_OPEN = 3,
    SHUTDOWN = 4,
  };

  struct ByteRing {
    uint8_t *data{nullptr};
    size_t capacity{0};
    size_t head{0};
    size_t tail{0};
    size_t fill{0};
  };

  static void network_task_(void *argument);
  static uint16_t read_u16_(const uint8_t *data);
  static uint32_t read_u32_(const uint8_t *data);
  static void write_u16_(uint8_t *data, uint16_t value);
  static void write_u32_(uint8_t *data, uint32_t value);

  void run_network_task_();
  void handle_datagram_(const uint8_t *packet, size_t length, const sockaddr_in &sender);
  void handle_open_(uint32_t session_id, const sockaddr_in &sender);
  void handle_speaker_(uint32_t sequence, const uint8_t *payload, size_t length);
  bool sender_matches_(const sockaddr_in &sender) const;
  bool send_packet_(PacketType type, uint32_t sequence, const uint8_t *payload = nullptr, size_t length = 0);
  void expire_peer_(CloseReason reason);

  void handle_microphone_(const std::vector<uint8_t> &data);
  void update_announcement_state_();
  void update_session_state_();
  bool ensure_speaker_ready_();
  void begin_playback_pause_();
  void stop_session_();
  void reset_session_maxima_();
  void flush_audio_();
  void flush_ring_(ByteRing &ring, portMUX_TYPE &mux);
  size_t push_latest_(ByteRing &ring, portMUX_TYPE &mux, const uint8_t *data, size_t length,
                      std::atomic<uint32_t> &dropped_bytes, std::atomic<uint32_t> &high_water_bytes);
  size_t pop_(ByteRing &ring, portMUX_TYPE &mux, uint8_t *destination, size_t maximum);
  size_t ring_fill_(ByteRing &ring, portMUX_TYPE &mux);
  static void update_maximum_(std::atomic<uint32_t> &target, uint32_t value);
  static const char *close_reason_name_(CloseReason reason);
  bool microphone_allowed_() const;

  microphone::MicrophoneSource *microphone_source_{nullptr};
  speaker::Speaker *speaker_{nullptr};
  media_player::MediaPlayer *announcement_media_player_{nullptr};
  switch_::Switch *barge_in_switch_{nullptr};

  uint16_t port_{8766};
  int socket_{-1};
  sockaddr_in peer_address_{};
  std::atomic<TaskHandle_t> network_task_handle_{nullptr};

  ByteRing uplink_ring_;
  ByteRing downlink_ring_;
  portMUX_TYPE uplink_mux_ = portMUX_INITIALIZER_UNLOCKED;
  portMUX_TYPE downlink_mux_ = portMUX_INITIALIZER_UNLOCKED;
  uint8_t *receive_packet_{nullptr};
  uint8_t *send_packet_buffer_{nullptr};
  uint8_t *downlink_scratch_{nullptr};

  std::atomic<bool> task_running_{false};
  std::atomic<bool> peer_present_{false};
  std::atomic<bool> open_command_pending_{false};
  std::atomic<bool> open_requested_{false};
  std::atomic<bool> session_active_{false};
  std::atomic<bool> connection_lost_pending_{false};
  std::atomic<bool> close_requested_{false};
  std::atomic<bool> ready_notification_pending_{false};
  std::atomic<uint32_t> microphone_callbacks_active_{0};
  std::atomic<bool> announcement_active_{false};
  std::atomic<bool> barge_in_enabled_{false};
  std::atomic<bool> playback_busy_{false};
  std::atomic<bool> playback_end_received_{true};
  std::atomic<bool> underrun_reported_{false};
  std::atomic<uint32_t> session_id_{0};
  std::atomic<uint32_t> last_host_packet_ms_{0};
  std::atomic<uint32_t> announcement_ended_ms_{0};
  std::atomic<uint32_t> last_downlink_ms_{0};

  bool speaker_sequence_started_{false};
  uint32_t expected_speaker_sequence_{0};
  std::atomic<bool> playback_end_pending_{false};
  uint32_t pending_playback_end_sequence_{0};
  uint32_t playback_end_pending_since_ms_{0};
  uint32_t microphone_sequence_{0};
  bool speaker_stop_pending_{false};
  bool speaker_start_requested_{false};
  bool session_maxima_reset_pending_{false};
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
  std::atomic<uint32_t> udp_uplink_send_drops_{0};
  std::atomic<uint32_t> udp_uplink_send_max_us_{0};
  std::atomic<uint32_t> udp_invalid_packets_{0};
  std::atomic<uint32_t> udp_foreign_packets_{0};
  std::atomic<uint32_t> udp_downlink_missing_packets_{0};
  std::atomic<uint32_t> udp_downlink_late_packets_{0};
  std::atomic<uint32_t> udp_sessions_{0};
  std::atomic<uint32_t> udp_timeouts_{0};
  std::atomic<int32_t> last_socket_errno_{0};
  std::atomic<CloseReason> last_close_reason_{CloseReason::NONE};
};

}  // namespace joydex_udp_audio_prototype
}  // namespace esphome
