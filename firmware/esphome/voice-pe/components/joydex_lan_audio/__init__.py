"""Trusted-LAN PCM bridge for the Joydex-dedicated Voice PE."""

import esphome.codegen as cg
import esphome.config_validation as cv
from esphome.components import esp32, media_player, microphone, network, socket, speaker, switch
from esphome.const import CONF_ID, CONF_MICROPHONE, CONF_PORT, CONF_SPEAKER, PLATFORM_ESP32
from esphome.core import CORE


CODEOWNERS = []
DEPENDENCIES = ["network", "microphone", "speaker", "media_player", "switch"]

CONF_ANNOUNCEMENT_MEDIA_PLAYER = "announcement_media_player"
CONF_BARGE_IN_SWITCH = "barge_in_switch"
CONF_UPLINK_ONLY = "uplink_only"


joydex_lan_audio_ns = cg.esphome_ns.namespace("joydex_lan_audio")
JoydexLanAudio = joydex_lan_audio_ns.class_("JoydexLanAudio", cg.Component)


def _reserve_sockets(config):
    """Reserve one listener and one accepted WebSocket connection."""
    network.require_high_performance_networking()
    socket.consume_sockets(2, "joydex_lan_audio_websocket_server")(config)
    return config


def _validate_audio_mode(config):
    if not config[CONF_UPLINK_ONLY] and CONF_SPEAKER not in config:
        raise cv.Invalid("speaker is required unless uplink_only is true")
    return config


CONFIG_SCHEMA = cv.All(
    cv.COMPONENT_SCHEMA.extend(
        {
            cv.GenerateID(): cv.declare_id(JoydexLanAudio),
            cv.Required(CONF_MICROPHONE): cv.use_id(microphone.MicrophoneSource),
            cv.Optional(CONF_SPEAKER): cv.use_id(speaker.Speaker),
            cv.Required(CONF_ANNOUNCEMENT_MEDIA_PLAYER): cv.use_id(media_player.MediaPlayer),
            cv.Required(CONF_BARGE_IN_SWITCH): cv.use_id(switch.Switch),
            cv.Optional(CONF_PORT, default=8765): cv.port,
            cv.Optional(CONF_UPLINK_ONLY, default=False): cv.boolean,
        }
    ),
    _validate_audio_mode,
    cv.only_on([PLATFORM_ESP32]),
    cv.only_with_esp_idf,
    _reserve_sockets,
)


async def to_code(config):
    if CORE.using_esp_idf:
        esp32.add_idf_sdkconfig_option("CONFIG_HTTPD_WS_SUPPORT", True)
        esp32.add_idf_sdkconfig_option("CONFIG_SPIRAM_ALLOW_STACK_EXTERNAL_MEMORY", True)

    var = cg.new_Pvariable(config[CONF_ID])
    await cg.register_component(var, config)

    mic = await cg.get_variable(config[CONF_MICROPHONE])
    announcement = await cg.get_variable(config[CONF_ANNOUNCEMENT_MEDIA_PLAYER])
    barge_in = await cg.get_variable(config[CONF_BARGE_IN_SWITCH])

    cg.add(var.set_microphone_source(mic))
    if CONF_SPEAKER in config:
        spk = await cg.get_variable(config[CONF_SPEAKER])
        cg.add(var.set_speaker(spk))
    cg.add(var.set_announcement_media_player(announcement))
    cg.add(var.set_barge_in_switch(barge_in))
    cg.add(var.set_port(config[CONF_PORT]))
    cg.add(var.set_uplink_only(config[CONF_UPLINK_ONLY]))
