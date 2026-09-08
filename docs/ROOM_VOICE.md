# Room Voice setup

Room Voice is an experimental Joydex feature that turns one Home Assistant
Voice Preview Edition into a hands-free Codex endpoint. The device recognizes
the wake word locally, Joydex owns a dedicated Codex task, and live microphone
and speaker audio travel over the local network.

## Status and boundaries

Room Voice is a reusable example rather than a turnkey consumer feature.
[`AGENTS.md`](../AGENTS.md) records the Codex Desktop build used to verify
Joydex's ordinary command bindings. Room Voice has a separate compatibility
boundary: the runtime verifies the exact Codex App Server executable and
companion code-mode host pinned in
[`CodexAppServerBinaryPolicy.cs`](../src/Joydex.Windows/Voice/CodexAppServerBinaryPolicy.cs).
Joydex does not distribute those Codex binaries.

The dedicated task runs with `approvalPolicy: never` and a full-access sandbox.
Its configured workspace is the intended place for files and session records;
it does not contain the task's filesystem access. Give the voice task only work
you are comfortable allowing it to perform without an approval prompt.

Voice media, wake signaling, and session control are plaintext on the local
network. Use the feature only on a trusted LAN. No router port-forwarding or
public exposure is required.

## Components

```text
Voice PE microphone ──LAN──> Joydex ──WebRTC──> Codex App Server
Voice PE speaker    <──LAN── Joydex <───────── Codex realtime voice
                                  │
                                  ├── dedicated owned task and transcript
                                  └── optional Desktop Task Bridge
                                      └── selected Desktop-owned task
```

Joydex remains useful as a controller and task-status app when Room Voice is
disabled or unavailable. Room Voice has no dependency on a connected joystick
or throttle.

## 1. Build the device firmware

Follow the supported source build in the
[`firmware/esphome/voice-pe` guide](../firmware/esphome/voice-pe/README.md).
Compiled images and credentials are deliberately absent from the repository.
The build wrapper never flashes a device.

After installing the firmware, open its web page and confirm that it is healthy.
The device endpoint is normally an HTTP address such as
`http://device-name.local/`. This trusted-LAN web page and its web updater are
intentionally unauthenticated; only the fallback recovery access point uses the
configured recovery password. Keep USB available for genuine recovery, but
normal updates may use ESPHome OTA after the installed image and credentials
have been verified.

## 2. Build and start Joydex

Joydex targets Windows and .NET 8. Restore and test it as described in the root
[`README`](../README.md), then create the combined Room Voice package from the
repository root:

```powershell
.\scripts\Publish-Joydex.ps1
```

Start `artifacts\Joydex\win-x64\Joydex.App.exe`. The published directory
includes `Joydex.DesktopBridgeHost.exe`, the WebView2 native loader, and the
required attribution files; keep those files together. An IDE or `dotnet run`
session does not assemble that flat package for the optional Desktop bridge.

Room Voice needs the exact compatible Codex App Server runtime named in the
current source policy. In Joydex, browse to that `codex.exe` under **Pinned App
Server executable**. Joydex rejects a different version or hash instead of
silently running against an untested protocol.

## 3. Provision the dedicated task

Open **Configure → Room Voice** and:

1. Select **Joydex owns a dedicated task**.
2. Enter the Voice PE **Device endpoint**.
3. Choose an existing local Codex project, a new dedicated project, or a custom
   absolute **Working folder**.
4. Choose **Create fresh owned task**. Joydex verifies the task's returned
   working directory before it saves the selection.
5. Select a realtime voice and speaker gain.
6. Enable Room Voice and save the configuration.

The default dedicated location is a sibling `joydex_voice` folder when Joydex
is running from a Git checkout, or `Documents\joydex_voice` otherwise. Each
voice session is recorded below
`.joydex\voice-sessions\YYYY-MM-DD\HHmmss-<session-id>`. The folder contains a
readable transcript and session metadata; diagnostic WAV files are added when
**Preserve assistant audio as WAV files** is enabled.

The Joydex-owned task cannot also be opened as a writer in Codex Desktop while
Room Voice owns it. Use the Room Voice window for its live transcript.

## 4. Use the conversation window

Choose **Room Voice** from the tray. The window shows connection state, the
conversation, mute and hangup controls, the selected Voice Target, and pending
outbound messages. Transcript bubbles support selection and copying. The view
follows new messages until you scroll upward; **Latest ↓** returns to the live
end.

Say `Okay Computer` to start. `Goodbye`, `bye`, `shut up`, `end`, and `die` are
recognized as explicit session-ending phrases. The center button also supports
short-press hangup and one-second-hold mute/unmute.

Wake tuning is available under **Device and wake**. Load the current values
before changing them. A lower wake probability cutoff is more sensitive and
can also increase false activations.

## Optional Desktop task messaging

The dedicated voice task and the task receiving a follow-up are separate.
Joydex starts and supervises the packaged bridge host itself; the disabled
marker-managed MCP block from earlier prototypes is retained only as migration
metadata and is not the runtime launch path. New setups do not need to install
that block. To send prompts to an existing local Codex Desktop task:

1. Enable experimental Desktop task messaging and save.
2. In the Room Voice window, choose a **Voice Target**.
3. Ask Room Voice to send a message to "this task," or name another visible
   task explicitly.

The **Install/Repair Desktop Bridge** and **Remove Desktop Bridge** controls
manage only that disabled compatibility block. If the block is present,
`enabled = false` is expected and prevents Codex Desktop from launching a
competing broker for each task.

The bridge uses Desktop's own task tool and never resumes the target as a second
writer. Desktop must be running. A failed delivery is written to the workspace
outbox and waits for manual Retry, Retarget, Copy, or Discard; reconnecting does
not send it automatically. Revalidate this compatibility after Codex Desktop
updates.

## Troubleshooting

- A red ring after wake usually means Joydex rejected startup, lost the LAN
  session, or could not start the pinned App Server. Check the Room Voice status
  and Joydex log before changing firmware.
- If wake detection feels weak, use **Load from device** before changing gain,
  wake cutoff, sliding window, or VAD cutoff. Those settings persist remotely
  and do not require an OTA update.
- If speech is incomplete, enable assistant WAV preservation for a short test.
  Compare the saved WAV with what the device played to separate upstream audio
  from LAN or speaker playback trouble.
- If the Desktop bridge is unavailable, Room Voice conversation still works.
  Review the held outbound message after Desktop reconnects.
- Existing captures under LocalAppData are legacy diagnostics. New session
  records live under the configured Voice Agent Workspace.

The concepts and compatibility boundaries are recorded in
[`CONCEPTS.md`](../CONCEPTS.md) and the accepted decisions under
[`docs/adr`](adr/).
