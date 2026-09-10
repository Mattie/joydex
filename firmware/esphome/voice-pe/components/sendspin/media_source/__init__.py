"""Pipeline-zero adapter from sendspin-cpp into the pinned media source ABI."""

import esphome.codegen as cg
from esphome.components import media_source
import esphome.config_validation as cv
from esphome.const import CONF_BUFFER_SIZE, CONF_ID, CONF_TASK_STACK_IN_PSRAM

from .. import (
    AudioSupportedFormatObject,
    CODEC_FORMAT_OPUS,
    CODEC_FORMAT_PCM,
    CONF_SENDSPIN_ID,
    PlayerRoleConfig,
    SendspinHub,
    sendspin_ns,
)

AUTO_LOAD = ["audio"]
CODEOWNERS = ["@kahrendt"]
DEPENDENCIES = ["media_source", "audio"]

SendspinMediaSource = sendspin_ns.class_(
    "SendspinMediaSource",
    media_source.MediaSource,
    cg.Component,
)

CONFIG_SCHEMA = cv.Schema(
    {
        cv.GenerateID(): cv.declare_id(SendspinMediaSource),
        cv.GenerateID(CONF_SENDSPIN_ID): cv.use_id(SendspinHub),
        # sendspin-cpp 0.7.2 passes MALLOC_CAP_SPIRAM without the
        # MALLOC_CAP_8BIT bit required by ESP-IDF 5.5's pthread API. The
        # rejected configuration silently leaves the sync/decode worker on
        # the 3072-byte default stack, which overflows as playback starts.
        # Keep this 6192-byte worker in internal RAM until the upstream
        # allocation-capability bug is fixed.
        cv.Optional(CONF_TASK_STACK_IN_PSRAM, default=False): cv.boolean,
        cv.Optional(CONF_BUFFER_SIZE, default=1000000): cv.int_range(min=25000),
    }
).extend(cv.COMPONENT_SCHEMA)


async def to_code(config):
    """Advertise Joydex's exact formats and connect the legacy media pipeline."""
    var = cg.new_Pvariable(config[CONF_ID])
    await cg.register_component(var, config)

    hub = await cg.get_variable(config[CONF_SENDSPIN_ID])
    await cg.register_parented(var, hub)

    formats = [
        cg.StructInitializer(
            AudioSupportedFormatObject,
            ("codec", codec),
            ("channels", 1),
            ("sample_rate", 48000),
            ("bit_depth", 16),
        )
        for codec in (CODEC_FORMAT_OPUS, CODEC_FORMAT_PCM)
    ]
    player_config = cg.StructInitializer(
        PlayerRoleConfig,
        ("audio_formats", formats),
        ("audio_buffer_capacity", config[CONF_BUFFER_SIZE]),
        ("fixed_delay_us", 0),
        ("initial_static_delay_ms", 0),
        ("extra_startup_silence_ms", 50),
        ("psram_stack", config[CONF_TASK_STACK_IN_PSRAM]),
    )

    cg.add(hub.set_player_listener(var))
    cg.add(hub.set_player_config(player_config))
    cg.add(var.set_uri_prefix("sendspin"))
