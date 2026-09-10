# Voice PE cue assets

The Room Voice firmware distributes three cue files:

- `WakeBlip.flac`: 48 kHz, stereo, signed 16-bit FLAC; the wake-word acknowledgement.
- `ReadyBlip.flac`: 48 kHz, stereo, signed 16-bit FLAC; the connected-and-ready cue.
- `EndBlip.flac`: 48 kHz, stereo, signed 16-bit FLAC; the completed-session cue.

Copyright (c) 2026 Mattie Casper. These recordings are distributed under the repository's [MIT License](../../../../LICENSE).

The guarded firmware scripts pin the reviewed files by SHA-256 and copy them into the prepared build without modification. Their containers contain no embedded metadata. Obsolete source and working files such as `WakeBlip.mp3` and `WakeBlip_old.flac` are not firmware inputs and remain excluded from the repository.
