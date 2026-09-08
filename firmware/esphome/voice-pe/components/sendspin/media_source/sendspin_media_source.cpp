#include "sendspin_media_source.h"

#if defined(USE_ESP32) && defined(USE_SENDSPIN_CONTROLLER) && defined(USE_SENDSPIN_PLAYER)

#include "esphome/components/audio/audio.h"
#include "esphome/core/hal.h"
#include "esphome/core/log.h"

#include <cinttypes>
#include <cmath>

namespace esphome::sendspin_ {

static const char *const TAG = "sendspin.media_source";
static constexpr char URI_PREFIX[] = "sendspin://";

void SendspinMediaSource::setup() {
  this->player_role_ = this->parent_->get_player_role();
  if (!this->player_role_) {
    ESP_LOGE(TAG, "Modern Sendspin player role is unavailable");
    this->mark_failed();
    return;
  }

  this->player_role_->update_volume(std::roundf(this->cached_volume_ * 100.0f));
  this->player_role_->update_muted(this->cached_muted_);
}

void SendspinMediaSource::loop() {
  if (!this->playback_ready_.load(std::memory_order_acquire)) {
    return;
  }
  const uint32_t now = millis();
  if (now - this->last_diagnostics_ms_ < 10000) {
    return;
  }
  this->last_diagnostics_ms_ = now;
  ESP_LOGD(TAG,
           "Playback diagnostics: starts=%" PRIu32 ", ends=%" PRIu32 ", writes=%" PRIu32
           ", zero_writes=%" PRIu32 ", accepted_bytes=%" PRIu32 ", played_frames=%" PRIu32,
           this->stream_starts_.load(std::memory_order_relaxed),
           this->stream_ends_.load(std::memory_order_relaxed), this->write_calls_.load(std::memory_order_relaxed),
           this->zero_writes_.load(std::memory_order_relaxed),
           this->accepted_bytes_.load(std::memory_order_relaxed),
           this->played_frames_.load(std::memory_order_relaxed));
}

void SendspinMediaSource::dump_config() {
  ESP_LOGCONFIG(TAG,
                "Modern Sendspin Media Source:\n"
                "  Output pipeline: 0\n"
                "  Format: 48 kHz mono PCM16 after Opus decode\n"
                "  Active playback diagnostics: every 10 seconds");
}

void SendspinMediaSource::init_pipelines(size_t pipeline_count) {
  media_source::MediaSource::init_pipelines(pipeline_count);
  if (pipeline_count == 0) {
    ESP_LOGE(TAG, "The speaker orchestrator did not provide pipeline 0");
    this->mark_failed();
  }
}

bool SendspinMediaSource::play_uri(const std::string &uri, size_t pipeline) {
  if (pipeline != PLAYER_PIPELINE || this->is_failed() || !this->output_callback_) {
    return false;
  }
  if (this->get_state(PLAYER_PIPELINE) != media_source::MediaSourceState::IDLE) {
    return false;
  }
  if (uri.compare(0, sizeof(URI_PREFIX) - 1, URI_PREFIX) != 0 ||
      uri.substr(sizeof(URI_PREFIX) - 1) != "current") {
    ESP_LOGE(TAG, "Unsupported Sendspin URI: '%s'", uri.c_str());
    return false;
  }

  this->pending_start_.store(false, std::memory_order_relaxed);
  this->playback_ready_.store(false, std::memory_order_relaxed);
  this->set_state_(media_source::MediaSourceState::PLAYING, PLAYER_PIPELINE);
  // set_state_ synchronously tells the pinned speaker orchestrator to install
  // this source. Publish Sendspin readiness only after that handoff completes,
  // otherwise the server can send the beginning of the stream into a closed
  // output callback.
  this->playback_ready_.store(true, std::memory_order_release);
  this->parent_->update_state(sendspin::SendspinClientState::SYNCHRONIZED);
  return true;
}

void SendspinMediaSource::handle_command(media_source::MediaSourceCommand command, size_t pipeline) {
  if (pipeline != PLAYER_PIPELINE) {
    return;
  }

  switch (command) {
    case media_source::MEDIA_SOURCE_COMMAND_END:
      this->pending_start_.store(false, std::memory_order_relaxed);
      this->playback_ready_.store(false, std::memory_order_release);
      this->set_state_(media_source::MediaSourceState::IDLE, PLAYER_PIPELINE);
      break;
    case media_source::MEDIA_SOURCE_COMMAND_STOP:
      this->pending_start_.store(false, std::memory_order_relaxed);
      this->playback_ready_.store(false, std::memory_order_release);
      this->set_state_(media_source::MediaSourceState::IDLE, PLAYER_PIPELINE);
      this->parent_->update_state(sendspin::SendspinClientState::EXTERNAL_SOURCE);
      break;
    case media_source::MEDIA_SOURCE_COMMAND_PLAY:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::PLAY);
      break;
    case media_source::MEDIA_SOURCE_COMMAND_PAUSE:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::PAUSE);
      break;
    case media_source::MEDIA_SOURCE_COMMAND_NEXT:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::NEXT);
      break;
    case media_source::MEDIA_SOURCE_COMMAND_PREVIOUS:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::PREVIOUS);
      break;
    case media_source::MEDIA_SOURCE_COMMAND_REPEAT_ALL:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::REPEAT_ALL);
      break;
    case media_source::MEDIA_SOURCE_COMMAND_REPEAT_ONE:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::REPEAT_ONE);
      break;
    case media_source::MEDIA_SOURCE_COMMAND_REPEAT_OFF:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::REPEAT_OFF);
      break;
    case media_source::MEDIA_SOURCE_COMMAND_SHUFFLE:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::SHUFFLE);
      break;
    case media_source::MEDIA_SOURCE_COMMAND_UNSHUFFLE:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::UNSHUFFLE);
      break;
    default:
      break;
  }
}

media_source::MediaSourceCapabilities SendspinMediaSource::get_capabilities() {
  return {
      .supports_pause = true,
      .supports_next_track = true,
      .supports_previous_track = true,
      .supports_volume_control = true,
      .has_internal_playlist = true,
  };
}

void SendspinMediaSource::notify_volume_changed(float volume) {
  this->cached_volume_ = volume;
  if (this->player_role_) {
    this->player_role_->update_volume(std::roundf(volume * 100.0f));
  }
}

void SendspinMediaSource::notify_mute_changed(bool is_muted) {
  this->cached_muted_ = is_muted;
  if (this->player_role_) {
    this->player_role_->update_muted(is_muted);
  }
}

void SendspinMediaSource::notify_audio_played(uint32_t frames, int64_t timestamp, size_t pipeline) {
  if (pipeline == PLAYER_PIPELINE && this->player_role_) {
    this->played_frames_.fetch_add(frames, std::memory_order_relaxed);
    this->player_role_->notify_audio_played(frames, timestamp);
  }
}

size_t SendspinMediaSource::on_audio_write(uint8_t *data, size_t length, uint32_t timeout_ms) {
  this->write_calls_.fetch_add(1, std::memory_order_relaxed);
  if (!this->output_callback_ || !this->playback_ready_.load(std::memory_order_acquire)) {
    this->zero_writes_.fetch_add(1, std::memory_order_relaxed);
    vTaskDelay(pdMS_TO_TICKS(timeout_ms));
    return 0;
  }

  const auto &params = this->player_role_->get_current_stream_params();
  if (!params.bit_depth.has_value() || !params.channels.has_value() || !params.sample_rate.has_value()) {
    this->zero_writes_.fetch_add(1, std::memory_order_relaxed);
    vTaskDelay(pdMS_TO_TICKS(timeout_ms));
    return 0;
  }

  audio::AudioStreamInfo stream_info(*params.bit_depth, *params.channels, *params.sample_rate);
  const size_t written =
      this->output_callback_(data, length, pdMS_TO_TICKS(timeout_ms), stream_info, PLAYER_PIPELINE);
  if (written == 0) {
    this->zero_writes_.fetch_add(1, std::memory_order_relaxed);
  } else {
    this->accepted_bytes_.fetch_add(static_cast<uint32_t>(written), std::memory_order_relaxed);
  }
  return written;
}

void SendspinMediaSource::on_stream_start() {
  this->stream_starts_.fetch_add(1, std::memory_order_relaxed);
  bool expected = false;
  if (this->play_uri_request_callback_ &&
      this->pending_start_.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) {
    this->defer("sendspin-stream-start", [this]() {
      if (this->pending_start_.load(std::memory_order_acquire) && this->play_uri_request_callback_) {
        this->play_uri_request_callback_("sendspin://current", PLAYER_PIPELINE);
      }
    });
  }
}

void SendspinMediaSource::on_stream_end() {
  this->stream_ends_.fetch_add(1, std::memory_order_relaxed);
  this->pending_start_.store(false, std::memory_order_relaxed);
  this->playback_ready_.store(false, std::memory_order_release);
  this->defer("sendspin-stream-end", [this]() {
    this->set_state_(media_source::MediaSourceState::IDLE, PLAYER_PIPELINE);
  });
}

void SendspinMediaSource::on_volume_changed(uint8_t volume) {
  if (this->volume_request_callback_) {
    this->volume_request_callback_(volume / 100.0f);
  }
}

void SendspinMediaSource::on_mute_changed(bool muted) {
  if (this->mute_request_callback_) {
    this->mute_request_callback_(muted);
  }
}

}  // namespace esphome::sendspin_

#endif
