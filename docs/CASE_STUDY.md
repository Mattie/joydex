# “Can We Put This on My Throttle?”

## Building a Codex companion for a Joystick/Throttle

Why pay for [a purpose-built Codex Micro keyboard](https://openai.com/supply/co-lab/work-louder/) when you already have a joystick, pad, or flight-sim throttle?

Just use Codex to set it up in a couple of hours and save yourself that extra $230.

How Easy It Is
---

**User**

> I would like to port this functionality for the Codex app into my Virpil throttle I have installed on Windows.
>
> https://www.youtube.com/watch?v=m8uUUUsMD3Y<br>
> https://openai.com/supply/co-lab/work-louder/
>
> Put together a plan on how I might do this.

**Codex (summarized)**

The first step was figuring out what “port this” could mean. The Work Louder device has a purpose-built Codex integration. A VIRPIL throttle arrives as a general joystick. Codex inspected the local Windows setup, identified the installed `LEFT VPC Throttle MT-50CM3`, checked the available Codex command surface, and separated the project into a few possible scopes:

- Map the throttle to existing keyboard shortcuts.
- Build a Windows companion that understands the throttle.
- Pursue deeper parity such as agent status and RGB lighting.
- Emulate the purpose-built keyboard at the USB level.

The recommendation was the companion. It could handle all of the awkward parts that a normal remapper would leave exposed: more than 32 logical buttons, hot-plug recovery, a safe action catalog, foreground checks, logs, and later a proper configuration UI. It also meant leaving the VIRPIL firmware and Codex installation untouched.

Reply COMPANION to proceed with the input tracer and reversible-navigation MVP, or SHORTCUTS for a quicker Joystick Gremlin setup.

**User**

> COMPANION

That one-word answer was enough to start the implementation. Surely Joystick Gremlin would have been faster, but then it wouldn't be quite as specific to our workflow.

The initial plan explored more speculative branches than the finished product, including task-status lighting and deeper Codex integration. The conversation quickly narrowed toward something useful on the installed hardware.

---

## The first version ran, but in the wrong way

**Codex (summarized)**

Codex scaffolded a .NET 8 solution with three parts:

- A core for input edges, mappings, configuration, and cooldowns.
- A Windows layer for DirectInput, key injection, deep links, and foreground safety.
- A small application layer to run the companion.

The first useful build could identify the throttle, trace logical button events, and translate bindings into Codex actions. It was still too developer-shaped.

**User**

> I don't want to run it from the terminal, can you either get it in the tooltray with a config pane, or you ask me what settings and I'll get you to set things up.

**Codex (summarized)**

The project became a tray application. It gained a Windows configuration form, device selection, a starter profile button, Save and Close, a status item, config reload, and access to the log. The companion could be launched by double-clicking an executable and left running in the notification area.

This was the first important product correction. A terminal tracer was useful while building the input layer. It was the wrong daily interface for a physical control surface.

---

## Then the setup instructions exposed a bad assumption

The early configuration design treated the throttle's mode dial like a normal bank-selector button. The instructions were essentially:

**Codex (early instructions)**

> Add a bank and select its row.<br>
> Click **Capture selector**, then move the mode dial from an adjacent position into that bank.<br>
> Add a binding, choose its bank and Codex action.<br>
> Click **Capture action button**, then press the desired throttle button.<br>
> Choose **Save and close**.

**Codex (summarized)**

The dial was not producing a standalone DirectInput button event. In the active VPC profile it acted as a shift modifier. Turning the dial changed the logical numbers emitted by the six illuminated B1 through B6 buttons.

That meant there was nothing to capture for the dial itself. The companion's bank-selector model did not match this device profile.

**User**

> Not exactly sure why I'm setting up the buttons when you can look up the spec for the throttle and can give me some recommended defaults to start with?

> We're trying to get close to that keyboard we talked about, and its defaults. Not sure if you can see from the video what they are? Or a spec?

This moved the project from a generic mapping editor toward a CM3-specific product.

**Codex (summarized)**

Eureka! Codex found something handy:

```
I found something even better than the video: your installed Codex build contains the official Codex
Micro integration and its factory layout. The six command keys default to Fast Mode, [....], and
Forward. I’m now matching those internal commands to the hotkeys available in your app.
```

Codex compared the controls shown for the Codex keyboard with the commands exposed by the installed desktop app. It built a starter layout for the known throttle instead of asking the user to discover and enter every binding manually.

The mode dial would select logical button ranges behind the scenes. The user could press B1 through B6 in M2, M3, or M4 and get the intended Codex commands without ever configuring a selector row.

**User**

> I think I see most of the options in the hotkeys list at least in the app hotkeys, FWIW, including cycle reasoning and stuff.

That observation helped close the gap. Several controls that had looked like possible UI-automation work were available through the app's own command and hotkey system. The companion could use fixed, named Codex actions instead of screen coordinates or image matching.

---

## The log needed to be part of the app

Codex next asked for a small set of button presses and planned to inspect the activity log.

**User**

> I don't want to manually tail the log myself, why is it not in the app during dry run? What buttons do you need me to press to confirm when you read the log?

**Codex (summarized)**

Fair point. The tray application gained a **Test controls** window. It showed connection status, raw button presses and releases, the selected binding, and whether the action was executed, blocked, or suppressed by dry run. Unmapped buttons appeared too.

The input reader also began consuming DirectInput's buffered events. Encoder pulses can be shorter than a normal polling interval, so reading only snapshots risked dropping a detent. The test window made those misses visible.

Now the user could move one physical control at a time and see the evidence in the same application.

**User**

> While in there, I don't like using M1 because it is lightless on the button panel, so move agent slots 1-6 to M4. I do like M2 being as it is, though.

**Codex (summarized)**

The layout changed. M2 stayed the command bank. Agent slots 1 through 6 moved to M4, where the illuminated buttons made more physical sense. M3 became navigation and task controls.


---

## The first button map was clearly wrong

The new test view immediately paid for itself.

**User (condensed)**

> Incorrect: M2-B5 showed up as M2-B1, M2-B6 showed up as M2-B2, M3-B1 showed up as M2-B3, M3-B2 showed up as M2-B4...
>
> M4-B5 was M4-B1, M4-B6 was M4-B2 and it continued into M5 to be off.
>
> Pressing E1 did reasoning-up when it should be turning it right, turning it left DID reduce reasoning down. Joystick hat thing might have been right, though.


**Codex (summarized)**

Very good details. The nominal throttle documentation did not describe the exact logical numbering produced by the saved VPC profile on this machine. The earlier defaults had been inferred too aggressively.

Codex stopped trying to repair the mapping from a paper specification and used the throttle as the source of truth. The dry-run window was adjusted to make an ordered calibration pass easier. The request became smaller: clear the prior noise, press a few named controls in sequence, then mark the run complete. I gave 5 things to do so I could confirm.

**User**

> I did some testing but then cleared results and did the #1, #2, #3, #4. DONE

**Codex (summarized)**

Codex read the resulting events and rebuilt the defaults around the measured ranges:

- M2 B1 through B6: logical buttons 56 through 61.
- M3 B1 through B6: logical buttons 62 through 67.
- M4 B1 through B6: logical buttons 68 through 73.

The E1 directions and push were separated correctly as well. Tests were added around the starter profile so a future edit could not casually reintroduce the old ranges.

This was the turning point. From here on, the companion described the installed throttle rather than a guessed version of it.

---

## “Depresses” turned out to matter

**User**

> change 53 to the hold to talk dictate button and then I think we're good.

**Codex (summarized)**

Button 53, the E2 press, became push-to-talk. A held input needs two edges: press the keyboard shortcut when the throttle control engages, then release it when the physical control releases.

**User**

> Should 53 be showing me depresses in the log?

**Codex (summarized)**

Yes. The activity window was fixed so both press and release were visible and both could be bound.

**User**

> Looks good!

For a moment, it did.

---

## Then came the “while we're here” features

The next requests were small enough to state in one sentence and concrete enough to implement without another planning phase.

**User**

> Can I have E2 right-scroll scroll down the active chat window like the mouse scroll wheel does, and left-scroll scroll up the active chat window like the mouse scroll wheel does?

**Codex (summarized)**

E2 rotation began sending mouse-wheel input to the pane under the pointer, matching ordinary wheel behavior.

**User**

> Can you make the # of mouse wheel notches configurable in the UI when you use scroll wheel?

**Codex (summarized)**

Scroll bindings gained a `wheelNotches` setting from 1 through 100. The field appears only for scroll actions. The live E2 mappings currently use two notches per detent.

**User**

> Make T7 up 48 home and 49 end

**Codex (summarized)**

Buttons 48 and 49 became Home and End.

**User**

> Add dry run to the context menu on the tool tray. I'm also curious if for button 37, I think it is, if you are getting press, depresses on that one, because I'm curious, it's kind of a weird switch, it's number T4.

**Codex (summarized)**

Dry run became directly toggleable from the tray. The T4 switch did report both edges, which made it a good second push-to-talk control.

**User**

> Put button 37 also to push-to-talk (releasing when it releases) so I can use that as well.
>
> Any other buttons Codex uses that match a long-press model?

**Codex (summarized)**

Button 37 gained press-and-release push-to-talk. The companion did not invent a general long-press layer without a concrete action that needed one. Maintained switches already had a useful model: bind one action to the press edge and a corresponding action to release.

That same model would soon be reused for something unrelated to dictation.

---

## The last feature began as a request for a picture

**User (condensed)**

> I now need another button, another internal option, that will bring up a floating/positional/resizable button-map graphic, a picture of the throttle type I have, with lines off of each button showing what they do and what they're mapped to.
>
> I'll probably tie it to T3 eventually so that when I press, long press T3, essentially, it'll leave it up, but I can just depress T3 and it'll disappear. And the window remembers where it is on my screen, that way I can have it up as a quick reference.
>
> I put a PDF (which is really just an image) which shows the throttle, but you might have more luck filling out `vpc-mongoost-50cm3-throttle-template-v0.webp`.

**Codex (summarized)**

Codex inspected both supplied assets. The PDF had clean leader lines but no logical button numbers. The WebP already had the CM3 control numbers and blank label cells. That made it the better runtime template.

There was a small detour when the expected PDF inspection utility was missing. A bundled Python PDF renderer produced a page image for visual comparison. After that check, Codex converted the WebP into the PNG shipped with the app.

The labels were not baked into the image. The new window reads the current configuration and draws each action into the matching control cell. If M4-B3 changes later, the quick-reference map changes with it.

The map window was built to behave like a temporary physical cheat sheet:

- T3 up, logical button 36 press: show it.
- T3 returned, button 36 release: hide it.
- Do not steal keyboard focus from Codex when it appears.
- Stay above other windows.
- Remember position, size, and maximized state.
- Hide on Escape or the window close button.
- Provide **Button map...** in the tray menu as a manual fallback.

The `button-map` action was kept inside the companion. It bypasses the Codex foreground guard and dry-run suppression because it does not send anything to Codex.

**User**

> Continue.

**Codex (summarized)**

Codex rendered a preview using the live configuration, inspected the label placement, added the T3 press and release bindings, ran all 61 tests, published the Windows package, restarted the tray app, and confirmed that it reconnected to the CM3.

The finished graphic looked like this:

![Current CM3 button map](images/joydex-button-map.png)

---

## Where the conversation landed

Joydex grew from a fixed CM3 shortcut table into a multi-controller .NET 8 Windows tray companion. The current live profile uses the CM3 as its main command surface and an Alpha/WarBRD stick as a second prompt and navigation surface. Each controller connects and recovers independently.

| Physical control | Current result |
| --- | --- |
| CM3 hat up/right/down/left | Plan / Forward / Sidebar / Back |
| CM3 EN3/EN2/EN1 | Move through Quick prompts / insert the selected prompt |
| CM3 E1 counter-clockwise/clockwise/press | Reasoning down / Reasoning up / Fast mode |
| CM3 T3 | Hold the CM3 button map open |
| CM3 T1 | Hold the Alpha/WarBRD button map open |
| CM3 M2 B1–B6 | Reject / Fork / Plan / Approve / Open / Submit |
| CM3 M3 B1–B6 | Plan / Back / Sidebar / Forward / New task / Skills |
| CM3 M4 B1–B6 | Agent slots 1–6 |
| Alpha wheel buttons 24/23/21 | Move through Review and debug prompts / insert and submit |
| Occupied task-alert controls | Open the assigned Codex task instead of running the ordinary binding |

The two prompt pickers each include an **[Exit / Nevermind]** item. Quick prompts submit only selected entries that request it; the Review and debug picker currently submits all three entries automatically. Joydex supports as many as three independently named pickers, and each picker can be rebound to an encoder or any Up, Down, and Insert button group on any configured controller.

The result now includes:

- Background, non-exclusive DirectInput for multiple independently reconnecting controllers.
- Device-qualified bindings, so identical logical button numbers remain separate across controllers.
- A Codex command catalog that resolves the current keyboard shortcuts immediately before sending input.
- Safe chords, sequences, held push-to-talk controls, mouse-wheel actions, and Unicode prompt insertion.
- Per-prompt optional submission and multiline insertion that leaves ordinary prompts as unsent drafts.
- Separate floating CM3 and Alpha/WarBRD maps whose labels follow the active configuration.
- Dry-run inspection, graphical configuration, hot-plug recovery, and foreground and simulator safety checks.
- Ten optional task-alert slots driven by Codex lifecycle hooks. Assigned controls open their tasks, while VIRPIL LinkTool displays running, approval, completed, and fault states through the controller LEDs.
- A Task Alerts status window for inspecting assignments, hook events, bank detection, and outgoing LED telemetry.

The source, documentation, map assets, and machine-neutral example configuration are published under the MIT License. Users build Joydex locally with .NET 8; machine-specific controller identifiers and the live configuration remain outside the repository.

The original safety boundary still holds: Joydex leaves the VIRPIL firmware and controller profiles intact. It does not emulate the Codex Micro hardware, patch the Codex application, or flash either controller.

---

## Just ask your coding agent

If you have an idea for something, just ask your coding agent to build upon your inspiration. It can often get you going and create something out of thin air to improve your workflow.
