#pragma once

#include "esphome/core/defines.h"

#if defined(USE_ESP32) && defined(USE_MEDIA_PLAYER) && defined(USE_SENDSPIN_CONTROLLER)

#include "esphome/components/media_player/media_player.h"
#include "esphome/components/sendspin/sendspin_hub.h"

namespace esphome::sendspin_ {

class SendspinMediaPlayer final : public SendspinChild, public media_player::MediaPlayer {
 public:
  void setup() override;
  void loop() override;
  media_player::MediaPlayerTraits get_traits() override;
  bool is_muted() const override { return this->muted_; }

 protected:
  void control(const media_player::MediaPlayerCall &call) override;

  bool force_publish_state_{false};
  bool muted_{false};
};

}  // namespace esphome::sendspin_

#endif
