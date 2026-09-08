# Joydex modern Sendspin compatibility canary

This host-only harness makes `sendspin-cpp` 0.7.2 behave like the player in a
future Voice PE firmware build. It advertises 48 kHz mono Opus and PCM, runs the
current decoder and synchronization task, and reports played frames through a
bounded realtime fake speaker.

`Joydex.SendspinProductionCanary` connects the production session to this
harness and exercises three responses in one call-scoped stream. A passing run
proves that Joydex's plaintext `player@v1` server dialect is wire compatible
with the current player engine. It does not validate physical Voice PE audio.

Pin `sendspin-cpp` tag `v0.7.2`, commit
`30514d5102c269a0c7fa6a13932d6bf7f2ae1abc`. Later encryption work changes the
handshake and audio frame layout and is outside this canary's contract.

Configure CMake with `SENDSPIN_CPP_SOURCE_DIR` set to that checkout. The
upstream host build supports Linux and macOS. Its FetchContent dependencies can
be supplied with `FETCHCONTENT_SOURCE_DIR_ARDUINOJSON`,
`FETCHCONTENT_SOURCE_DIR_MICRO_FLAC`, `FETCHCONTENT_SOURCE_DIR_MICRO_OPUS`, and
`FETCHCONTENT_SOURCE_DIR_IXWEBSOCKET` for a network-isolated build.

An offline configure looks like this after the pinned checkouts are available:

```text
cmake -S <this-directory> -B <build-directory> \
  -DSENDSPIN_CPP_SOURCE_DIR=<sendspin-cpp-directory> \
  -DFETCHCONTENT_FULLY_DISCONNECTED=ON \
  -DFETCHCONTENT_SOURCE_DIR_ARDUINOJSON=<arduinojson-directory> \
  -DFETCHCONTENT_SOURCE_DIR_MICRO_FLAC=<micro-flac-directory> \
  -DFETCHCONTENT_SOURCE_DIR_MICRO_OPUS=<micro-opus-directory> \
  -DFETCHCONTENT_SOURCE_DIR_IXWEBSOCKET=<ixwebsocket-directory>
cmake --build <build-directory> --parallel
```

Run the harness on port `8927`, then run the production canary against
loopback:

```text
joydex-sendspin-modern-canary --port 8927 --required-streams 1 --required-content-segments 3 --minimum-audible-frames-per-segment 90000 --require-rising-segment-energy 1
.\.tools\dotnet\dotnet.exe run --project .\tools\Joydex.SendspinProductionCanary\Joydex.SendspinProductionCanary.csproj --no-restore
```

Acceptance requires the configured number of stream starts and ends, at least
one decoded write, every accepted PCM frame reported played, and zero
sink-write timeouts. The fake
speaker has a bounded two-second PCM queue: one second matches Joydex's
scheduling lead and the second absorbs ordinary host-thread jitter.
The production-session gate requires one call-scoped stream, three tone
segments separated by clocked silence, at least 90,000 non-silent decoded
frames in each two-second response, and increasing energy across the three
amplitude-coded tones. This prevents generated synchronization silence from
masquerading as a successful response and verifies response order. CMake enforces the exact
source commit and rejects tracked semantic changes while tolerating line-ending
normalization.

The verified spike used these source revisions:

- `sendspin-cpp` `v0.7.2`: `30514d5102c269a0c7fa6a13932d6bf7f2ae1abc`
- ArduinoJson `v7.4.1`: `32520135092970120a5ac165cf45f48e658c421d`
- micro-flac `v0.1.1`: `9f8bfe5c9ee46cea175084b49ae8ac95545705b5`
- micro-opus `v0.3.5`: `3e9ce44c56ab8007261d68c511f0162599542aa2`
- IXWebSocket `v11.4.5`: `c5a02f1066fb0fde48f80f51178429a27f689a39`
