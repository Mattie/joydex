"""Modern Sendspin player integration for the pinned Voice PE ESPHome tree."""

import esphome.codegen as cg
from esphome.components import esp32, network, socket, wifi
import esphome.config_validation as cv
from esphome.const import CONF_ID, CONF_TASK_STACK_IN_PSRAM, PLATFORM_ESP32

AUTO_LOAD = ["mdns"]
CODEOWNERS = ["@kahrendt"]
DEPENDENCIES = ["network"]

CONF_KALMAN_PROCESS_ERROR = "kalman_process_error"
CONF_KALMAN_FORGET_FACTOR = "kalman_forget_factor"
CONF_SENDSPIN_ID = "sendspin_id"

sendspin_library_ns = cg.global_ns.namespace("sendspin")
SendspinCodecFormat = sendspin_library_ns.enum("SendspinCodecFormat", is_class=True)
CODEC_FORMAT_OPUS = SendspinCodecFormat.enum("OPUS")
CODEC_FORMAT_PCM = SendspinCodecFormat.enum("PCM")
AudioSupportedFormatObject = sendspin_library_ns.struct("AudioSupportedFormatObject")
PlayerRoleConfig = sendspin_library_ns.struct("PlayerRoleConfig")

# The trailing underscore keeps ESPHome's adapter namespace distinct from the
# global namespace owned by sendspin-cpp.
sendspin_ns = cg.esphome_ns.namespace("sendspin_")
SendspinHub = sendspin_ns.class_("SendspinHub", cg.Component)


def _request_network_resources(config):
    """Reserve the sockets and Wi-Fi controls used by the embedded listener."""
    network.require_high_performance_networking()
    socket.require_wake_loop_threadsafe()
    socket.consume_sockets(5, "joydex_sendspin_websocket_server")(config)
    wifi.enable_runtime_power_save_control()
    return config


CONFIG_SCHEMA = cv.All(
    cv.COMPONENT_SCHEMA.extend(
        {
            cv.GenerateID(): cv.declare_id(SendspinHub),
            cv.SplitDefault(CONF_TASK_STACK_IN_PSRAM, esp32_idf=False): cv.All(
                cv.boolean, cv.only_with_esp_idf
            ),
            # The retail Voice PE package still supplies these legacy filter
            # settings. The modern library owns its clock model, so accept the
            # fields without carrying the obsolete values into C++.
            cv.Optional(CONF_KALMAN_PROCESS_ERROR): cv.float_,
            cv.Optional(CONF_KALMAN_FORGET_FACTOR): cv.float_,
        }
    ),
    cv.only_on([PLATFORM_ESP32]),
    cv.only_with_esp_idf,
    _request_network_resources,
)


async def to_code(config):
    """Configure the exact modern library and its narrow Voice PE role set."""
    esp32.add_idf_component(name="sendspin/sendspin-cpp", ref="0.7.2")
    esp32.add_idf_sdkconfig_option("CONFIG_HTTPD_WS_SUPPORT", True)
    esp32.add_idf_sdkconfig_option("CONFIG_SENDSPIN_ENABLE_PLAYER", True)
    esp32.add_idf_sdkconfig_option("CONFIG_SENDSPIN_ENABLE_CONTROLLER", True)
    esp32.add_idf_sdkconfig_option("CONFIG_SENDSPIN_ENABLE_METADATA", False)
    esp32.add_idf_sdkconfig_option("CONFIG_SENDSPIN_ENABLE_COLOR", False)
    esp32.add_idf_sdkconfig_option("CONFIG_SENDSPIN_ENABLE_ARTWORK", False)
    esp32.add_idf_sdkconfig_option("CONFIG_SENDSPIN_ENABLE_VISUALIZER", False)

    cg.add_define("USE_SENDSPIN", True)
    cg.add_define("USE_SENDSPIN_PLAYER", True)
    cg.add_define("USE_SENDSPIN_CONTROLLER", True)

    var = cg.new_Pvariable(config[CONF_ID])
    await cg.register_component(var, config)

    if config.get(CONF_TASK_STACK_IN_PSRAM):
        cg.add(var.set_task_stack_in_psram(True))
        esp32.add_idf_sdkconfig_option("CONFIG_SPIRAM_ALLOW_STACK_EXTERNAL_MEMORY", True)
