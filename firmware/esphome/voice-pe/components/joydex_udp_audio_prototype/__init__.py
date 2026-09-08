"""Throwaway UDPPCM transport canary for the Joydex Dedicated Voice Endpoint."""

import esphome.codegen as cg
import esphome.config_validation as cv
from esphome.components import media_player, microphone, network, socket, speaker, switch
from esphome.const import CONF_ID, CONF_MICROPHONE, CONF_PORT, CONF_SPEAKER, PLATFORM_ESP32


CODEOWNERS = []
DEPENDENCIES = ["network", "microphone", "speaker", "media_player", "switch"]

CONF_ANNOUNCEMENT_MEDIA_PLAYER = "announcement_media_player"
CONF_BARGE_IN_SWITCH = "barge_in_switch"


joydex_udp_audio_ns = cg.esphome_ns.namespace("joydex_udp_audio_prototype")
JoydexUdpAudioPrototype = joydex_udp_audio_ns.class_("JoydexUdpAudioPrototype", cg.Component)


def _reserve_socket(config):
    """Reserve the prototype's single IPv4 UDP socket."""
    network.require_high_performance_networking()
    socket.consume_sockets(1, "joydex_udp_audio_prototype")(config)
    return config


CONFIG_SCHEMA = cv.All(
    cv.COMPONENT_SCHEMA.extend(
        {
            cv.GenerateID(): cv.declare_id(JoydexUdpAudioPrototype),
            cv.Required(CONF_MICROPHONE): cv.use_id(microphone.MicrophoneSource),
            cv.Required(CONF_SPEAKER): cv.use_id(speaker.Speaker),
            cv.Required(CONF_ANNOUNCEMENT_MEDIA_PLAYER): cv.use_id(media_player.MediaPlayer),
            cv.Required(CONF_BARGE_IN_SWITCH): cv.use_id(switch.Switch),
            cv.Optional(CONF_PORT, default=8766): cv.port,
        }
    ),
    cv.only_on([PLATFORM_ESP32]),
    cv.only_with_esp_idf,
    _reserve_socket,
)


async def to_code(config):
    """Bind ESPHome audio components to the source-only UDPPCM prototype."""
    var = cg.new_Pvariable(config[CONF_ID])
    await cg.register_component(var, config)

    mic = await cg.get_variable(config[CONF_MICROPHONE])
    spk = await cg.get_variable(config[CONF_SPEAKER])
    announcement = await cg.get_variable(config[CONF_ANNOUNCEMENT_MEDIA_PLAYER])
    barge_in = await cg.get_variable(config[CONF_BARGE_IN_SWITCH])

    cg.add(var.set_microphone_source(mic))
    cg.add(var.set_speaker(spk))
    cg.add(var.set_announcement_media_player(announcement))
    cg.add(var.set_barge_in_switch(barge_in))
    cg.add(var.set_port(config[CONF_PORT]))
