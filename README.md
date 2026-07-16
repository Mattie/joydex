# Virpil Codex Pad

Virpil Codex Pad is a Windows tray companion that maps a VIRPIL throttle to a small, deliberate set of Codex desktop commands. It reads the throttle through background, non-exclusive DirectInput and leaves the VIRPIL firmware and VPC profile untouched.

The current build targets the VPC Throttle MT-50CM3 and compatible DirectInput device profiles. It includes the factory Codex Micro layout as a starter profile, device discovery, hot-plug recovery, dry-run logging, foreground-process guards, and a tray menu for configuration and status.

When available, ready-to-run packages are distributed through the repository's GitHub Releases page. Building from source requires the .NET 8 SDK.

## Use a packaged release

1. Download and extract the Windows x64 package from GitHub Releases.
2. Double-click `VirpilCodexPad.App.exe`.
3. The app appears in the Windows system tray. Its configuration window opens automatically the first time.
4. Choose the CM3 throttle in the device list.
5. Choose **Load Codex Micro defaults**.
6. Choose **Save and close**. The app installs the matching Codex keyboard shortcuts and keeps **Dry run** enabled.
7. Restart Codex once so it reads the new shortcuts. The tray companion can stay open.

The starter profile follows the Codex Micro controls:

| CM3 control | Dial position | Codex Micro role |
| --- | --- | --- |
| Base buttons B1-B6 | M2 | Fast, Approve, Reject, Fork, Push-to-talk, Submit |
| Base buttons B1-B6 | M3 | Plan, Back, Sidebar, Forward, New task, Skills |
| Base buttons B1-B6 | M4 | Agent slots 1-6 |
| Base encoder E1 | Any | Reasoning down/up; push is unused |
| Five-way hat | Any | Plan, Forward, Sidebar, Back |

VIRPIL's 5-way shift profile makes the dial a modifier. The dial produces no button event of its own; B1-B6 emit a different logical range in each position. The starter profile handles those ranges directly, so dial capture is unnecessary. The app also reads DirectInput's event buffer so short encoder pulses survive between polling frames.

Double-click the tray icon, or right-click it and choose **Configure...**, whenever you want to change mappings. The tray menu also has a checkable **Dry run** item for switching modes immediately, plus configuration reload, the activity log, and the underlying JSON for advanced use.

T3 is the quick-reference control: hold it up to show the floating CM3 button map and release it to hide the map. The map is rendered from an original schematic, reads its labels from the current configuration, stays above other windows, and remembers its last position, size, and maximized state. You can also show or hide it with **Button map...** in the tray menu; Escape and the window close button hide it without quitting the companion.

## Safety model

- Dry-run mode is enabled in every newly created configuration.
- The first report after connect becomes a baseline, so controls already held during startup cannot trigger actions.
- A 250 ms connection warm-up ignores settling axis values.
- A binding fires on its configured press or release edge. Standard-mode banks require one selector; the CM3 5-way profile uses VIRPIL's shifted button ranges directly.
- A per-binding cooldown rejects accidental repeats.
- Reasoning encoder pulses bypass that cooldown so each detent reaches Codex.
- Keyboard shortcuts require ChatGPT/Codex to be the foreground process; this guard cannot be disabled in the MVP.
- Processes in `simulatorProcessNames` block every action, including deep links.
- The action catalog contains fixed Codex commands. It cannot run shell commands or send free-form text.

`new-task` and `open-skills` use documented `codex://` deep links and may bring Codex forward. Other actions use the documented Windows shortcuts and remain foreground-gated. See [ChatGPT desktop app commands](https://learn.chatgpt.com/docs/reference/commands.md).

## Advanced: build from source

This repository pins .NET SDK 8.0.423 in `global.json`.

```powershell
dotnet build .\VirpilCodexPad.sln --configuration Release
dotnet test .\VirpilCodexPad.sln --configuration Release
```

## Advanced: command-line tracer

List attached DirectInput devices:

```powershell
dotnet run --project .\tools\VirpilCodexPad.Trace -- list
```

Trace a CM3 throttle for 60 seconds. Use the `list` command first and add `--instance-guid` when more than one matching device is connected:

```powershell
dotnet run --project .\tools\VirpilCodexPad.Trace -- trace `
  --name "VPC Throttle MT-50CM3" `
  --seconds 60
```

Move one control at a time. Trace output uses one-based button numbers, matching `config.json`. In VIRPIL's 5-way shift profile, test B1-B6 in each dial position because the dial itself has no standalone event.

## Advanced: configuration file

The tray application creates `%LOCALAPPDATA%\VirpilCodexPad\config.json` and `%LOCALAPPDATA%\VirpilCodexPad\virpil-codex-pad.log`. The graphical configuration pane manages the file for normal use.

The checked-in [example configuration](config/virpil-codex-pad.example.json) selects the throttle by product name and intentionally omits machine-specific DirectInput GUIDs. The graphical pane writes the normal configuration. A direct binding that works without a bank uses the reserved `always` bank:

```json
{
  "bankSelectors": {},
  "bindings": [
    {
      "name": "M2 B1 - fast-mode",
      "bank": "always",
      "button": 56,
      "trigger": "press",
      "action": "fast-mode"
    }
  ]
}
```

For `scroll-up` and `scroll-down` bindings, set `wheelNotches` from 1 through 100. The configuration pane enables this field only on scroll-action rows; omitted values default to 1.

The CM3 starter profile uses logical ranges measured from a five-way-shift VPC profile. The dry-run test window shows raw press and release events for every logical button, including unmapped controls, so different VPC profiles are visible immediately.

Keep `dryRun` set to `true` during mapping. Exercise each bank and button, open the activity log from the tray menu, add simulator executables in the configuration pane, and save. Disable **Dry run** after the log matches the intended actions; the pane asks for confirmation before enabling live actions.

After live mappings are stable, you can create a shortcut to `VirpilCodexPad.App.exe` in `shell:startup` if the companion should start when you sign in. Startup registration remains an explicit user choice.

You can use an alternate config with either:

```powershell
$env:VIRPIL_CODEX_PAD_CONFIG = 'D:\path\to\config.json'
dotnet run --project .\src\VirpilCodexPad.App
```

or the app's `--config D:\path\to\config.json` argument.

## Supported actions

| Action ID | Codex operation | Delivery |
| --- | --- | --- |
| `agent-1` through `agent-6` | Select agent slot 1-6 | `Ctrl+Alt+Shift+F1` through `F6` |
| `fast-mode` | Toggle fast mode | `Ctrl+Alt+Shift+F7` |
| `approve` / `reject` | Answer an approval request | `Ctrl+Alt+Shift+F8` / `F9` |
| `fork-task` | Fork the current task | `Ctrl+Alt+Shift+F10` |
| `push-to-talk` | Hold global dictation | `Ctrl+CapsLock` held |
| `submit` | Submit the composer | `Ctrl+Alt+Shift+F11` |
| `plan-mode` | Toggle plan mode | `Ctrl+Alt+Shift+F12` |
| `reasoning-up` / `reasoning-down` | Change reasoning effort | `Ctrl+Alt+PageUp` / `Ctrl+Alt+PageDown` |
| `scroll-up` / `scroll-down` | Scroll the pane under the pointer | Configurable 1-100 mouse-wheel notches per encoder detent |
| `home` / `end` | Send Home or End | Unmodified `Home` / `End` key |
| `button-map` | Show the floating CM3 quick-reference map | Internal press-to-show / release-to-hide window |
| `new-task` | Open a new local task | `codex://threads/new` |
| `open-skills` | Open Skills | `codex://skills` |
| `previous-task` | Select previous task | `Ctrl+Shift+[` |
| `next-task` | Select next task | `Ctrl+Shift+]` |
| `navigate-back` | Navigate back | `Ctrl+[` |
| `navigate-forward` | Navigate forward | `Ctrl+]` |
| `toggle-sidebar` | Toggle sidebar | `Ctrl+B` |
| `dictation` | Start dictation | `Ctrl+Shift+D` |

The one-click profile merges these keys into `%USERPROFILE%\.codex\keybindings.json` and preserves unrelated bindings such as global dictation. Older F13-F24 bindings installed by this companion are migrated automatically.

## Scope

This companion does not write a VPC profile, flash firmware, control RGB, or click desktop UI. Approve and Submit remain fixed, foreground-gated Codex commands.

## License and trademarks

The source code is available under the [MIT License](LICENSE).

Virpil Codex Pad is an independent project and is not affiliated with or endorsed by VIRPIL Controls, OpenAI, or Work Louder. VIRPIL, VPC, OpenAI, ChatGPT, Codex, Work Louder, and associated marks are the property of their respective owners.
