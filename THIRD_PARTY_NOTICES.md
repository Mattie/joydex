# Third-party notices

## VPC MongoosT-50CM3 visual template

The visual template used for Joydex's CM3 button map is adapted from the [VPC MongoosT-50CM3 Throttle Template](https://www.reddit.com/r/hotas/comments/o3pqnb/vpc_mongoost50cm3_throttle_template/) created by Reddit user u/axefan1.

The creator shared the source files and wrote:

> "Feel free to copy this template and swap out the device images and logical button mappings for your own devices!"

This attribution applies to the button-map asset in `src/Joydex.App/Assets/cm3-button-map.png` and the derived documentation image in `docs/images/joydex-button-map.png`.

## Alpha/WarBRD visual template

The Alpha/WarBRD button-map artwork in `docs/images/joydex-button-map_vpc-constellation-alpha-warbrd.png` was created for Joydex by Mattie Casper and is distributed under the project's MIT License. The rendered documentation image in `docs/images/joydex-alpha-button-map.png` is derived from that original artwork.

## HID device access

Joydex uses [HidSharp](https://github.com/SeekHisKingdom/HIDSharp), copyright 2010-2025 James F. Bellinger, under the Apache License 2.0, to communicate with supported HID devices.

## Opus audio encoding

Joydex uses [Concentus 2.2.2](https://www.nuget.org/packages/Concentus/2.2.2), a managed implementation of the Opus codec, for Voice PE speaker downlink.

Copyright (c) by various holding parties, including (but not limited to):
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the names of
  specific contributors, may be used to endorse or promote products derived
  from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Microsoft WebView2

Joydex uses the [Microsoft Edge WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4129.50)
for Room Voice's low-latency browser audio boundary. Copyright (C) Microsoft
Corporation. The SDK is redistributed under its BSD-style license. The complete
license and bundled third-party notice are included as
[`LICENSES/Microsoft-WebView2.txt`](LICENSES/Microsoft-WebView2.txt) and
[`LICENSES/Microsoft-WebView2-NOTICE.txt`](LICENSES/Microsoft-WebView2-NOTICE.txt),
and both files are copied into Joydex publish output.

## Home Assistant Voice PE and ESPHome firmware

The source-only Voice PE example under `firmware/esphome/voice-pe` is based on
[Home Assistant Voice PE](https://github.com/esphome/home-assistant-voice-pe) at
commit `a163e7b980c572df9d3811ff98094350f5aae541` and uses
[ESPHome](https://github.com/esphome/esphome) `2025.12.2`, including runtime
source pinned at commits `99f7e9aeb74447fa489fefb80b8129623e916816`,
`0fe230114c80b6d3378f04c777baadee178e40ad`, and
`f5c1a8111df78e32d07d7a7bb800018808cdc0e7`.

Those projects license C/C++ runtime code under GNU GPL version 3 and Python
and other material under MIT. Joydex modified the upstream configuration and
runtime components during August 2026 for dedicated wake handling, runtime
wake tuning, session controls, LAN audio transport, and sustained Sendspin
playback. The exact license boundary and modification notice are recorded in
`firmware/esphome/voice-pe/LICENSE.md`.

## Sendspin

The Voice PE build uses
[sendspin-cpp 0.7.2](https://github.com/Sendspin/sendspin-cpp/tree/v0.7.2)
under the Apache License 2.0. Joydex's ESPHome adapter is distributed under the
firmware terms described in `firmware/esphome/voice-pe/LICENSE.md`; the upstream
Sendspin library retains its Apache-2.0 terms and notices.

## microWakeWord models

The Voice PE build references wake and VAD models from
[esphome/micro-wake-word-models](https://github.com/esphome/micro-wake-word-models/tree/05b65922cc433c9df13e98e32a7fe520758c837e)
at commit `05b65922cc433c9df13e98e32a7fe520758c837e`, and release
models from [kahrendt/microWakeWord](https://github.com/kahrendt/microWakeWord).
Both project repositories publish their source under the Apache License 2.0.
The release-model assets are fetched directly from upstream and remain subject
to the terms supplied with those assets. The separately distributed Joydex cue
recordings are first-party assets covered by the repository's MIT License.

## Wireless-panel scale photograph

The photograph in
`docs/images/joydex-esp32-4848s040c-in-action.jpg` was created for Joydex by
Mattie Casper. The depicted Magic: The Gathering card is included only as a
familiar size reference. Its artwork, text, product name, and trademarks remain
the property of their respective rights holders and are not licensed under the
Joydex MIT License. Those rights holders are not affiliated with or endorsing
Joydex.
