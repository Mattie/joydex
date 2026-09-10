#include "sendspin_media_player.h"

#if defined(USE_ESP32) && defined(USE_MEDIA_PLAYER) && defined(USE_SENDSPIN_CONTROLLER)

#include "esphome/core/log.h"

#include <algorithm>
#include <cmath>

namespace esphome::sendspin_ {

static const char *const TAG = "sendspin.media_player";

void SendspinMediaPlayer::setup() {
  this->parent_->add_group_update_callback([this](const sendspin::GroupUpdateObject &group) {
    if (!group.playback_state.has_value()) {
      return;
    }
    auto new_state = group.playback_state.value() == sendspin::SendspinPlaybackState::PLAYING
                         ? media_player::MEDIA_PLAYER_STATE_PLAYING
                         : media_player::MEDIA_PLAYER_STATE_IDLE;
    if (this->state != new_state) {
      this->state = new_state;
      this->force_publish_state_ = true;
    }
  });

  this->parent_->add_controller_state_callback([this](const sendspin::ServerStateControllerObject &state) {
    this->volume = state.volume / 100.0f;
    this->muted_ = state.muted;
    this->force_publish_state_ = true;
  });
  this->parent_->add_controller_state_clear_callback([this]() {
    this->state = media_player::MEDIA_PLAYER_STATE_IDLE;
    this->force_publish_state_ = true;
  });

  this->state = media_player::MEDIA_PLAYER_STATE_IDLE;
  this->publish_state();
}

void SendspinMediaPlayer::loop() {
  if (!this->force_publish_state_) {
    return;
  }
  this->force_publish_state_ = false;
  this->publish_state();
  ESP_LOGD(TAG, "State changed to %s", media_player::media_player_state_to_string(this->state));
}

media_player::MediaPlayerTraits SendspinMediaPlayer::get_traits() {
  media_player::MediaPlayerTraits traits;
  traits.set_supports_pause(true);
  return traits;
}

void SendspinMediaPlayer::control(const media_player::MediaPlayerCall &call) {
  if (!this->is_ready() || this->is_failed()) {
    return;
  }

  if (call.get_volume().has_value()) {
    auto volume = static_cast<uint8_t>(std::roundf(call.get_volume().value() * 100.0f));
    this->parent_->send_client_command(sendspin::SendspinControllerCommand::VOLUME, volume);
  }

  if (!call.get_command().has_value()) {
    return;
  }

  switch (call.get_command().value()) {
    case media_player::MEDIA_PLAYER_COMMAND_TOGGLE:
      this->parent_->send_client_command(this->state == media_player::MEDIA_PLAYER_STATE_PLAYING
                                             ? sendspin::SendspinControllerCommand::PAUSE
                                             : sendspin::SendspinControllerCommand::PLAY);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_PLAY:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::PLAY);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_PAUSE:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::PAUSE);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_STOP:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::STOP);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_REPEAT_OFF:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::REPEAT_OFF);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_REPEAT_ONE:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::REPEAT_ONE);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_REPEAT_ALL:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::REPEAT_ALL);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_SHUFFLE:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::SHUFFLE);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_UNSHUFFLE:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::UNSHUFFLE);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_NEXT:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::NEXT);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_PREVIOUS:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::PREVIOUS);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_VOLUME_UP:
      this->parent_->send_client_command(
          sendspin::SendspinControllerCommand::VOLUME,
          static_cast<uint8_t>(std::roundf(std::min(1.0f, this->volume + 0.05f) * 100.0f)));
      break;
    case media_player::MEDIA_PLAYER_COMMAND_VOLUME_DOWN:
      this->parent_->send_client_command(
          sendspin::SendspinControllerCommand::VOLUME,
          static_cast<uint8_t>(std::roundf(std::max(0.0f, this->volume - 0.05f) * 100.0f)));
      break;
    case media_player::MEDIA_PLAYER_COMMAND_MUTE:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::MUTE, std::nullopt, true);
      break;
    case media_player::MEDIA_PLAYER_COMMAND_UNMUTE:
      this->parent_->send_client_command(sendspin::SendspinControllerCommand::MUTE, std::nullopt, false);
      break;
    default:
      break;
  }
}

}  // namespace esphome::sendspin_

#endif
