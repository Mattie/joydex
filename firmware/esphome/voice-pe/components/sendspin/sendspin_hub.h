#pragma once

#include "esphome/core/defines.h"

#if defined(USE_ESP32) && defined(USE_SENDSPIN_CONTROLLER) && defined(USE_SENDSPIN_PLAYER)

#include "esphome/core/component.h"
#include "esphome/core/helpers.h"
#include "esphome/core/preferences.h"

#include <sendspin/client.h>
#include <sendspin/config.h>
#include <sendspin/controller_role.h>
#include <sendspin/player_role.h>
#include <sendspin/types.h>

#include <memory>
#include <optional>
#include <utility>

namespace esphome::sendspin_ {

struct LastPlayedServerPref {
  uint32_t server_id_hash;
};

struct StaticDelayPref {
  uint16_t delay_ms;
};

/// Owns sendspin-cpp and exposes only the player/controller surface used by the
/// dedicated Joydex Voice PE endpoint.
class SendspinHub final : public Component,
                          public sendspin::ControllerRoleListener,
                          public sendspin::SendspinClientListener,
                          public sendspin::SendspinNetworkProvider,
                          public sendspin::SendspinPersistenceProvider {
 public:
  float get_setup_priority() const override { return setup_priority::AFTER_WIFI; }
  void setup() override;
  void loop() override;
  void dump_config() override;

  void update_state(sendspin::SendspinClientState state);
  void send_client_command(sendspin::SendspinControllerCommand command,
                           std::optional<uint8_t> volume = std::nullopt,
                           std::optional<bool> mute = std::nullopt);

  template<typename F> void add_group_update_callback(F &&callback) {
    this->group_update_callbacks_.add(std::forward<F>(callback));
  }

  template<typename F> void add_controller_state_callback(F &&callback) {
    this->controller_state_callbacks_.add(std::forward<F>(callback));
  }

  template<typename F> void add_controller_state_clear_callback(F &&callback) {
    this->controller_state_clear_callbacks_.add(std::forward<F>(callback));
  }

  void set_task_stack_in_psram(bool enabled) { this->task_stack_in_psram_ = enabled; }
  void set_player_listener(sendspin::PlayerRoleListener *listener) { this->player_listener_ = listener; }
  void set_player_config(const sendspin::PlayerRoleConfig &config) { this->player_config_ = config; }
  sendspin::PlayerRole *get_player_role();

 protected:
  sendspin::SendspinClientConfig build_client_config_();
  static const char *get_client_id_into_buffer(std::span<char, MAC_ADDRESS_PRETTY_BUFFER_SIZE> buffer);

  void on_group_update(const sendspin::GroupUpdateObject &group) override;
  void on_request_high_performance() override;
  void on_release_high_performance() override;
  bool is_network_ready() override;

  bool save_last_server_hash(uint32_t hash) override;
  std::optional<uint32_t> load_last_server_hash() override;
  bool save_static_delay(uint16_t delay_ms) override;
  std::optional<uint16_t> load_static_delay() override;

  void on_controller_state(const sendspin::ServerStateControllerObject &state) override;
  void on_controller_state_clear() override;

  ESPPreferenceObject last_played_server_pref_;
  ESPPreferenceObject static_delay_pref_;
  std::unique_ptr<sendspin::SendspinClient> client_;
  sendspin::ControllerRole *controller_role_{nullptr};
  sendspin::PlayerRoleListener *player_listener_{nullptr};
  sendspin::PlayerRoleConfig player_config_{};

  CallbackManager<void(const sendspin::GroupUpdateObject &)> group_update_callbacks_{};
  CallbackManager<void(const sendspin::ServerStateControllerObject &)> controller_state_callbacks_{};
  CallbackManager<void()> controller_state_clear_callbacks_{};

  bool task_stack_in_psram_{false};
};

class SendspinChild : public Component, public Parented<SendspinHub> {
 public:
  float get_setup_priority() const override { return setup_priority::AFTER_WIFI - 1.0f; }
};

}  // namespace esphome::sendspin_

#endif
