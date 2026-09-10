# Voice PE firmware licensing

The Joydex Voice PE example is a source-only firmware recipe assembled from
Joydex code and several upstream projects. This notice defines the license
boundary within `firmware/esphome/voice-pe`.

## File boundary

| Material | License |
| --- | --- |
| C/C++ runtime files under `components`, including the Joydex LAN audio and ESPHome Sendspin adapter code | [GNU GPL version 3 only](../../../LICENSES/GPL-3.0-only.txt) |
| Patch files that modify ESPHome `.c`, `.cpp`, `.h`, `.hpp`, `.tcc`, or `.ino` runtime files | [GNU GPL version 3 only](../../../LICENSES/GPL-3.0-only.txt) |
| Joydex-authored YAML, Python, PowerShell, Markdown, protocol descriptions, and patch files that modify only upstream YAML, Python, or other non-runtime files | [MIT](../../../LICENSE) |
| `sounds/ReadyBlip.flac`, `sounds/WakeBlip.flac`, and `sounds/EndBlip.flac`, copyright (c) 2026 Mattie Casper | [MIT](../../../LICENSE) |
| `sendspin-cpp` 0.7.2 fetched by ESP-IDF | [Apache License 2.0](../../../LICENSES/Apache-2.0.txt) |
| Models referenced from `esphome/micro-wake-word-models` | [Apache License 2.0](../../../LICENSES/Apache-2.0.txt) |
| Release-model assets fetched from `kahrendt/microWakeWord` | The terms supplied with those upstream assets; they are referenced rather than redistributed here |
| Source fetched by preparation or build tools | The license supplied by that upstream project, with the specific direct dependencies recorded below and in the repository's third-party notices |

When distributed as a combined firmware image, the GPL-covered runtime makes
the combined firmware subject to GPLv3. Compatible Apache-2.0 components keep
their copyright, patent, and attribution notices. The root MIT license
continues to apply to the separately distributed Joydex desktop application
and the MIT files identified above.

## Upstream source and modifications

Joydex modified the following upstream material during August 2026:

- Home Assistant Voice PE commit
  `a163e7b980c572df9d3811ff98094350f5aae541`: dedicated wake routing,
  session state and controls, LED/cue behavior, and pinned source references.
- ESPHome commit `99f7e9aeb74447fa489fefb80b8129623e916816`:
  runtime microWakeWord cutoff and sliding-window controls.
- ESPHome mixer commit `0fe230114c80b6d3378f04c777baadee178e40ad`
  and speaker-source commit `f5c1a8111df78e32d07d7a7bb800018808cdc0e7`:
  playback accounting used by the Sendspin adapter.
- ESPHome's Sendspin component design: adapted to the pinned Voice PE media
  source ABI and `sendspin-cpp` 0.7.2 for the Joydex speaker path.

The Joydex-specific C/C++ components, overlays, patches, and preparation
scripts record the complete source changes needed for this source checkpoint.
The build also references:

- `sendspin-cpp` tag `v0.7.2`.
- `esphome/micro-wake-word-models` commit
  `05b65922cc433c9df13e98e32a7fe520758c837e`.
- `kahrendt/microWakeWord` release-model assets retained by the pinned retail
  base and fetched directly from upstream.

## Distribution boundary

This repository does not distribute compiled Voice PE firmware or credentials.
The three Joydex cue recordings named above are first-party MIT assets. Anyone
distributing a compiled image must perform a dependency review for that exact build, preserve
all applicable notices, and provide the corresponding source and build or
installation information required by GPLv3.

The upstream projects and their authors do not endorse Joydex. All software is
provided without warranty under its applicable license.
