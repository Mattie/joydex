"""Controller-only media player for the modern Sendspin adapter."""

import esphome.codegen as cg
from esphome.components import media_player
import esphome.config_validation as cv
from esphome.const import CONF_ID

from .. import CONF_SENDSPIN_ID, SendspinHub, sendspin_ns

AUTO_LOAD = ["audio", "media_player"]
CODEOWNERS = ["@kahrendt"]
DEPENDENCIES = ["sendspin"]

SendspinMediaPlayer = sendspin_ns.class_(
    "SendspinMediaPlayer",
    media_player.MediaPlayer,
    cg.Component,
)

CONFIG_SCHEMA = media_player.media_player_schema(SendspinMediaPlayer).extend(
    {
        cv.GenerateID(): cv.declare_id(SendspinMediaPlayer),
        cv.GenerateID(CONF_SENDSPIN_ID): cv.use_id(SendspinHub),
    }
)


async def to_code(config):
    """Register the retained Voice PE group controller entity."""
    var = cg.new_Pvariable(config[CONF_ID])
    await cg.register_component(var, config)
    await cg.register_parented(var, config[CONF_SENDSPIN_ID])
    await media_player.register_media_player(var, config)
