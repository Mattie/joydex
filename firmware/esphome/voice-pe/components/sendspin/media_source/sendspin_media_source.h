#pragma once

#include "esphome/core/defines.h"

#if defined(USE_ESP32) && defined(USE_SENDSPIN_CONTROLLER) && defined(USE_SENDSPIN_PLAYER)

#include "esphome/components/media_source/media_source.h"
#include "esphome/components/sendspin/sendspin_hub.h"

#include <sendspin/player_role.h>

#include <atomic>

namespace esphome::sendspin_ {

/// Bridges the modern player into pipeline 0 of the Voice PE's pinned
/// pipeline-indexed MediaSource interface.
class SendspinMediaSource final : public SendspinChild,
                                  public media_source::MediaSource,
                                  public sendspin::PlayerRoleListener {
 public:
  void setup() override;
  void loop() override;
  void dump_config() override;

  void init_pipelines(size_t pipeline_count) override;
  bool play_uri(const std::string &uri, size_t pipeline) override;
  void handle_command(media_source::MediaSourceCommand command, size_t pipeline) override;
  media_source::MediaSourceCapabilities get_capabilities() override;

  void notify_volume_changed(float volume) override;
  void notify_mute_changed(bool is_muted) override;
  void notify_audio_played(uint32_t frames, int64_t timestamp, size_t pipeline) override;

 protected:
  size_t on_audio_write(uint8_t *data, size_t length, uint32_t timeout_ms) override;
  void on_stream_start() override;
  void on_stream_end() override;
  void on_volume_changed(uint8_t volume) override;
  void on_mute_changed(bool muted) override;

  static constexpr size_t PLAYER_PIPELINE = 0;
  sendspin::PlayerRole *player_role_{nullptr};
  float cached_volume_{0.0f};
  bool cached_muted_{false};
  std::atomic<bool> pending_start_{false};
  std::atomic<bool> playback_ready_{false};
  std::atomic<uint32_t> stream_starts_{0};
  std::atomic<uint32_t> stream_ends_{0};
  std::atomic<uint32_t> write_calls_{0};
  std::atomic<uint32_t> zero_writes_{0};
  std::atomic<uint32_t> accepted_bytes_{0};
  std::atomic<uint32_t> played_frames_{0};
  uint32_t last_diagnostics_ms_{0};
};

}  // namespace esphome::sendspin_

#endif
