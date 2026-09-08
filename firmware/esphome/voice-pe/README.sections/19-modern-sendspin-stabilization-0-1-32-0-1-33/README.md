### Corrected modern Sendspin line (`0.1.32`–`0.1.33`)

USB serial converted the `0.1.31` red-ring failure into a precise fault: ESP-IDF reported
`A stack overflow in task pthread has been detected` when modern Sendspin crossed from decode into
speaker playback. `sendspin-cpp` `0.7.2` requests a PSRAM pthread stack with
`MALLOC_CAP_SPIRAM` alone. ESP-IDF `5.5.1` requires `MALLOC_CAP_8BIT` in that capability mask;
the rejected configuration was ignored, and the unchecked call silently left the default
3,072-byte pthread stack in place.

Firmware `0.1.32` keeps only the modern Sendspin player worker's configured 6,192-byte stack in
internal RAM by setting its media-source `task_stack_in_psram` option to `false`. Large decoder and
audio buffers remain in PSRAM. The Sendspin HTTP server retains its independent PSRAM-stack setting;
startup logging now names that setting explicitly so it cannot be mistaken for the player worker.
Two direct 40-second full-duplex canaries completed without a panic or reboot. A later natural call
also stayed booted and carried complete user and assistant events, moving its observed failure to a
host startup-timeout race rather than the corrected device stack.

The same serial session exposed an independent lifecycle problem. With Joydex owning this endpoint,
no Home Assistant native-API client remains connected. ESPHome's default 15-minute native-API
watchdog therefore logged `No clients; rebooting` and deliberately restarted a healthy endpoint.
Firmware `0.1.33` sets `api.reboot_timeout: 0s`. The Wi-Fi reconnect watchdog remains enabled for
actual network-loss recovery.

Joydex now opens and synchronizes Sendspin with one silent 20 ms priming frame before reporting the
speaker lane ready. The recovery wrapper allows eight seconds for an individual speaker send and
reports an attempt timeout explicitly. This covers the observed 2.86-second first-stream
synchronization delay that raced the former three-second deadline, canceled the first real frame,
and terminated a session with 99 already-queued frames. These host changes do not drop or replay
assistant audio; a failed scheduled send remains terminal.

The guarded two-pass `0.1.33` build passed station and recovery credential parity, generated-code
checks for both the internal-RAM player stack and zero native-API reboot timeout, pinned dependency
and ABI checks, image validation, and resource ceilings. The application image is 3,149,203 bytes,
with 46,736 bytes of static RAM and 134,799 bytes of DIRAM. The immutable artifacts are:

| Built `0.1.33` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.ota.bin` | 3,149,344 | `17DA5649C5C0C16318EBD66A38D55ECD5387575B0D6B87B155B45230E4214061` | `1B9317997A069937F05355A3DCDA7A5C802E6F6D8A7748218EDDCC526E7793BE` |
| `firmware.factory.bin` | 3,214,880 | `9168B1E2AA5807AB2066C9BCCD2DCEA6B78606CF96CBD8970097A36B57EBA89F` | — |

On 2026-08-29, the exact OTA payload above was written over USB to the known Voice PE;
esptool verified every written block. The endpoint rejoined, served HTTP and ports
`6053`, `8765`, and `8927`, reported Armed with wake inference and microphone capture active, and
was reclaimed by the Joydex-owned Dedicated Voice Task. It remained Armed at 922 seconds with the
USB reset reason unchanged, passing the former 15-minute no-client reboot boundary.

The primed host build then completed the longest mostly functional natural call in this program:
about 321 seconds, 24 assistant playback boundaries, 8,173 assistant frames, and 15,982 Sendspin
frames. The host queue high-water was two frames with zero overflow, maximum pacing lateness was
11.8 ms, and the session ended by spoken hangup and rearmed without a reboot. This is the working
playback checkpoint.

A following short-answer trial exposed a narrower remaining defect. One physically inaudible reply
contained 105 scheduled frames but only 30 non-silent frames, with no host overflow, burst, or
material arrival gap. Its onset coincided with restoration of the 1,000 ms Sendspin timeline lead
after a host delivery stall. A longer following response was audible. The next diagnostic should
isolate playback onset and resynchronization for sub-second speech without changing the checkpointed
scheduler first.
