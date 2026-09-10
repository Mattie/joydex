#include "sendspin_hub.h"

#if defined(USE_ESP32) && defined(USE_SENDSPIN_CONTROLLER) && defined(USE_SENDSPIN_PLAYER)

#include "esphome/components/network/util.h"
#include "esphome/components/wifi/wifi_component.h"
#include "esphome/core/application.h"
#include "esphome/core/log.h"
#include "esphome/core/version.h"

namespace esphome::sendspin_ {

static const char *const TAG = "sendspin.hub";

void SendspinHub::setup() {
  auto config = this->build_client_config_();
  this->client_ = std::make_unique<sendspin::SendspinClient>(std::move(config));

  this->last_played_server_pref_ =
      global_preferences->make_preference<LastPlayedServerPref>(fnv1_hash("sendspin_last_played"));
  this->static_delay_pref_ =
      global_preferences->make_preference<StaticDelayPref>(fnv1_hash("sendspin_static_delay"));

  this->client_->set_listener(this);
  this->client_->set_network_provider(this);
  this->client_->set_persistence_provider(this);

  this->controller_role_ = &this->client_->add_controller();
  this->controller_role_->set_listener(this);
  this->client_->add_player(this->player_config_).set_listener(this->player_listener_);

  if (!this->client_->start_server()) {
    ESP_LOGE(TAG, "Failed to start modern Sendspin server");
    this->mark_failed();
  }
}

void SendspinHub::loop() {
  if (this->client_) {
    this->client_->loop();
  }
}

void SendspinHub::dump_config() {
  char mac_buffer[MAC_ADDRESS_PRETTY_BUFFER_SIZE];
  ESP_LOGCONFIG(TAG,
                "Modern Sendspin Hub:\n"
                "  Client ID: %s\n"
                "  Listener: ws://<device>:8927/sendspin\n"
                "  Roles: player@v1, controller@v1\n"
                "  Library: sendspin-cpp 0.7.2\n"
                "  HTTP server task stack in PSRAM: %s",
                get_client_id_into_buffer(mac_buffer), YESNO(this->task_stack_in_psram_));
}

void SendspinHub::update_state(sendspin::SendspinClientState state) {
  if (this->client_ && this->is_ready()) {
    this->client_->update_state(state);
  }
}

void SendspinHub::send_client_command(sendspin::SendspinControllerCommand command,
                                      std::optional<uint8_t> volume, std::optional<bool> mute) {
  if (!this->controller_role_ || !this->is_ready()) {
    return;
  }
  this->controller_role_->send_command({
      .command = command,
      .volume = volume,
      .muted = mute,
  });
}

const char *SendspinHub::get_client_id_into_buffer(std::span<char, MAC_ADDRESS_PRETTY_BUFFER_SIZE> buffer) {
  return get_mac_address_pretty_into_buffer(buffer);
}

sendspin::SendspinClientConfig SendspinHub::build_client_config_() {
  sendspin::SendspinClientConfig config;
  char mac_buffer[MAC_ADDRESS_PRETTY_BUFFER_SIZE];
  config.client_id = get_client_id_into_buffer(mac_buffer);
  config.name = App.get_friendly_name();
  config.product_name = App.get_name();
  config.manufacturer = "ESPHome / Joydex";
  config.software_version = ESPHOME_VERSION;
  config.httpd_psram_stack = this->task_stack_in_psram_;
  config.server_port = 8927;
  return config;
}

void SendspinHub::on_group_update(const sendspin::GroupUpdateObject &group) {
  this->group_update_callbacks_.call(group);
}

void SendspinHub::on_request_high_performance() {
#ifdef USE_WIFI
  if (wifi::global_wifi_component != nullptr) {
    wifi::global_wifi_component->request_high_performance();
  }
#endif
}

void SendspinHub::on_release_high_performance() {
#ifdef USE_WIFI
  if (wifi::global_wifi_component != nullptr) {
    wifi::global_wifi_component->release_high_performance();
  }
#endif
}

bool SendspinHub::is_network_ready() { return network::is_connected(); }

bool SendspinHub::save_last_server_hash(uint32_t hash) {
  LastPlayedServerPref preference{.server_id_hash = hash};
  return this->last_played_server_pref_.save(&preference);
}

std::optional<uint32_t> SendspinHub::load_last_server_hash() {
  LastPlayedServerPref preference{};
  if (this->last_played_server_pref_.load(&preference)) {
    return preference.server_id_hash;
  }
  return std::nullopt;
}

bool SendspinHub::save_static_delay(uint16_t delay_ms) {
  StaticDelayPref preference{.delay_ms = delay_ms};
  return this->static_delay_pref_.save(&preference);
}

std::optional<uint16_t> SendspinHub::load_static_delay() {
  StaticDelayPref preference{};
  if (this->static_delay_pref_.load(&preference)) {
    return preference.delay_ms;
  }
  return std::nullopt;
}

void SendspinHub::on_controller_state(const sendspin::ServerStateControllerObject &state) {
  this->controller_state_callbacks_.call(state);
}

void SendspinHub::on_controller_state_clear() { this->controller_state_clear_callbacks_.call(); }

sendspin::PlayerRole *SendspinHub::get_player_role() {
  if (!this->client_ || !this->is_ready()) {
    return nullptr;
  }
  return this->client_->player();
}

}  // namespace esphome::sendspin_

#endif
