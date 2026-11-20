# Xbox One / Series pad: the probe

Branch `xbox-one`, EXPERIMENTAL (planned for 0.2). Follows `xbox-one-impulse-triggers.md`.

The probe answers three questions with one hardware run:

1. Does Windows bind its in-box Xbox driver (`dc1-controller.inf` with the `xboxgip` filter) to our virtual GIP device on the usbip-win2 bus?
2. Does the pad reach XInput and Windows.Gaming.Input (WGI)?
3. Do trigger-rumble commands (Direct Motor Command, type `0x09`, trigger bits) reach us?

Everything is built from Microsoft's open specification **[MS-GIPUSB] 1.0** only (learn.microsoft.com/openspecs/windows_protocols/ms-gipusb). No other project's code was looked at.

## What is in the build

| Part | Where |
|---|---|
| USB descriptors, Microsoft OS descriptors, endpoints | `src/Kopru.Core/Virtual/VirtualXboxOne.cs` |
| GIP state machine (Hello, metadata, START, status, input, guide) | `src/Kopru.Core/Virtual/GipDevice.cs` |
| Framing, metadata blob, input mapping, motor command parsing and timing | `src/Kopru.Core/Virtual/GipProtocol.cs` |
| Trigger rumble on the DualSense's adaptive triggers | `src/Kopru.Core/Bridge/ImpulseTriggers.cs` (mapping), `src/Kopru.Core/Bridge/ControllerBridge/ControllerBridgeImpulse.cs` (layering, writes) |
| Developer switch | `src/Kopru.Core/Virtual/ExperimentalPads.cs`, used in `src/Kopru.Core/Bridge/BridgeHost/BridgeHostPads.cs` |
| WGI / XInput test tool | `tools/Kopru.WgiProbe` (not in `Kopru.sln`) |
| Tests | `tests/Kopru.Core.Tests/Virtual/XboxOneProbeTests.cs`, `tests/Kopru.Core.Tests/Bridge/BridgeTriggerRumbleTests.cs` |

Without the switch nothing changes: the bridge makes the selected pad type exactly as before.

### The device

- USB 2.0 full speed, device class `FF/47/D0`, 64-byte EP0, one configuration, one interface `FF/47/D0`, interrupt OUT `0x01` and IN `0x81`, 64 bytes, `bInterval` 4 ms. No audio interface.
- String `0xEE` answers `MSFT100` with vendor code `0x90`. Vendor request `0x90`, `wIndex` 4, answers the 40-byte extended compatible ID descriptor with compatible ID `XGIP10` for interface 0. That is what `dc1-controller.inf` matches as `USB\MS_COMP_XGIP10`. The extended properties request (`wIndex` 5), the device qualifier and anything else not in the spec's request table are stalled, as the spec asks.
- Strings: 1 manufacturer, 2 product, 3 serial (32 hex digits containing the 64-bit device ID). The device ID is `0000FFFB` plus 32 random bits, new for every pad (each app start, each pad swap). The spec asks for this (2.2.1.3: "Random numbers, determined on bootup"; two devices with the same ID both fail). It also means a quick re-attach never shows the host the ID of an instance it may still hold. The downside: Windows makes a new device entry per session (Device Manager → View → Show hidden devices lists the old ones).
- Before the bridge unplugs the pad (app exit, pad swap), the pad sends the "powering off" status and waits for the host to read it, then 500 ms more, as a real pad does before it powers off (3.1.1: a disconnect before the host processed that status counts as unexpected). Log: `GIP powering off (unplug)` and `GIP powering-off status picked up by the host after .. ms`.
- **OFF before START.** In hardware run 2 the host answered a quick re-attach (about 10 s after closing, same device ID, no powering-off status sent) with Set Device State OFF right after the metadata. The spec gives no reason the host sends OFF. A real pad in Off waits for its Guide button (3.1.5.5.6). The pad now does that too (the PS button wakes it: Hello again), and if the OFF came before any START in that session, it announces itself again after 3 s, at most 3 times. Each decision is logged. The random device ID and the powering-off status should keep this from happening at all.
- Metadata: the spec's example gamepad (input `0x20` with 14 bytes, output `0x09` with 9 bytes, type `Windows.Xbox.Input.Gamepad`, interfaces IController, IGamepad, INavigationController) **plus the security opt-out GUID** `7a34ce77-7de2-45c6-8ca4-0042c08bd94a` from spec section 5.1. That makes 198 bytes, sent in 4 fragments. A test checks that our builder reproduces the spec's 182-byte example blob exactly when the opt-out is left out.
- Firmware version in Hello and metadata: 1.0 (build 1).
- To the bridge it reports itself as an Xbox 360 pad: rumble only, no audio, no light bar, no speaker. The main motors go to the DualSense the same way as on the 360 pad. The trigger motors play on the DualSense's adaptive triggers (next section).

### Trigger rumble on the DualSense

The two trigger motor levels (0-100 %) go to the bridge (`ITriggerRumbleSource`), which plays each as the trigger's **vibration effect** (mode `0x26`, `TriggerEffects.Vibration`).

- **Mapping** (`ImpulseTriggers`, `ImpulseTuning`; why these numbers: `xbox-one-impulse-triggers.md`, section 7): the level drives a model of the Xbox trigger's small ERM motor. **Inertia** (`ImpulseMotor`): the motor level follows the game's level with a 25 ms spin-up and 35 ms spin-down time constant, and stalls below 10 % on its way to a stop. The motor level sets **amplitude and frequency together**, as on an ERM: amplitude = round(8 × x^0.8), at least 1; frequency = 30 + 60 × x Hz, in eighths of the range (x = level / 100). Steady state: 10 % → 1 at 38 Hz, 25 % → 3 at 45 Hz, 50 % → 5 at 60 Hz, 75 % → 6 at 75 Hz, 100 % → 8 at 90 Hz. The vibration covers the whole travel from zone 0 (the released trigger), so it is felt wherever the finger rests. The profile's trigger strength (Light/Normal/Strong) scales it like any game effect. All of these numbers can be tuned live (see "Trigger feel tuning" below).
- **Timing** (`GipMotors`): each motor keeps its own command, so a main-motor command neither cuts a trigger rumble short nor stretches it. A level-0 command for a motor stops it at once; duration 0 stops all four. Otherwise a command plays repeat + 1 cycles of "wait the delay, then play the duration" (10 ms steps; the specification does not say how the three fields combine, this is our reading). With delay 0, which is what Windows sends for a held WGI vibration (duration 255, delay 0, repeat 235), that is one steady stretch of about 10 minutes, unless a new command comes first. With a delay the motor pulses, at the pad timer's 20 ms resolution. When the time runs out with no new command, the trigger stops.
- **Layering, per trigger**: a DSX mod's effect > the trigger rumble > the fallback effect (Feedback / Weapon / Vibration). While the rumble plays it replaces the fallback on that trigger; when it ends the fallback comes back. It does not count as a native DualSense trigger effect (no `TriggersSeen`, no native-support signal). This pad never sends DualSense trigger effects of its own.
- **Writes**: one output report only when a trigger's mapped (amplitude, Hz) steps, and at most one every 10 ms (`ImpulseTriggerPlayer.MinIntervalMs`; both triggers share the report). A change held back by the cap goes out on the next 10 ms tick. While a motor spins up or down a timer pumps the model every 10 ms; it stops once both motors have settled. A game moving the level by a few percent each frame writes nothing new. The main motors' report goes out only when the main levels change.
- **When the bridge ends** (disconnect, pad swap) with a rumble still on, the triggers get their effect without it.

### Identities

| `KOPRU_EXPERIMENTAL_XBOXONE` | USB identity | Recognised by |
|---|---|---|
| `1` (or anything not listed below) | generic GIP device, `1209:0001` (the pid.codes test PID) | the compatible ID `XGIP10` only |
| `one` | Xbox One S, `045E:02EA` | hardware ID in `dc1-controller.inf` |
| `series` | Xbox Series, `045E:0B12` | hardware ID in `dc1-controller.inf` |
| unset, empty, `0`, `off` | switch off | — |

Try `1` first (the cleanest test of the protocol), then `one`.

## How to run it

**Use a virtual machine if you can.** A malformed GIP stream reaches a kernel driver (`xboxgip.sys`), and a bug check is possible. The PC needs usbip-win2 and Windows 10 21H2 or later (older Windows still demands authentication).

1. **Quit any running DS5 Untethered** (tray icon → Exit). The app is single-instance: a second start only shows the first one, and that one does not have the switch.
2. Build the test build, for example `dotnet build src\Kopru.App -c Debug`.
3. Start it from a PowerShell window that has the switch set:

   ```powershell
   $env:KOPRU_EXPERIMENTAL_XBOXONE = '1'      # or 'one' / 'series'
   & .\src\Kopru.App\bin\Debug\net8.0\Kopru.App.exe
   ```

   The variable only lives in that PowerShell window and the app started from it.
4. Turn the DualSense on (Bluetooth) so the bridge connects. Whatever pad type is selected in the app, the virtual pad is now the experimental Xbox One pad.
5. Look at the four places below while it runs. Press buttons and move the sticks a little.
6. Start the WGI probe window:

   ```powershell
   dotnet run --project tools\Kopru.WgiProbe                     # level 75 %, automatic sequence after 3 s
   dotnet run --project tools\Kopru.WgiProbe -- --level 1.0 --no-auto
   # or, once built: .\tools\Kopru.WgiProbe\bin\Debug\net8.0-windows10.0.19041.0\Kopru.WgiProbe.exe
   ```

   It is a window of its own because Windows.Gaming.Input only gives readings and vibration to the app in the foreground. A console probe failed in hardware run 1 for this reason: the console window belongs to conhost / Windows Terminal, not to the probe. The window brings itself to the front and shows in green or red whether it is in front. **Don't click away while it vibrates.** Losing focus makes Windows send QUIESCE, which clears the motors.

   It shows the XInput slots and the first WGI gamepad's readings live (move things on the DualSense; stick up should be positive Y). It has buttons for Left Trigger, Right Trigger, Left Motor, Right Motor, All, Sequence and Stop, a **level slider** (0-100 %, default 75 %) and a **Hold** box. Without Hold a button plays one step: 2 s on at the slider's level, then off. With Hold on, a button keeps its motors running at the slider's level until Stop (or Hold off); moving the slider changes the level live, so steady levels can be felt one by one (and 0 % shows the stop). A held level is sent again when the window comes back to the front. 3 s after start it plays the sequence: LeftTrigger, RightTrigger, LeftMotor, RightMotor, all four, each 2 s on and 0.5 s off. Every line also goes to `wgi-probe.log` next to the exe, with times, to match against the `GIP motor command` and `trigger rumble` lines in `app.log`.

   Rest a finger on the trigger during the trigger steps: the DualSense's trigger vibration is felt through the trigger blade, and rattles a little when nothing touches it.

   **Trigger feel tuning (experimental).** Below the buttons the probe has a "Trigger feel" row: presets **Xbox-like** (the default), **Soft** and **Sharp**, and sliders for Min Hz, Max Hz, Hz curve, Amp curve, Spin-up ms and Spin-down ms. Every change is written to `%LOCALAPPDATA%\Untethered\impulse-tuning.json` (at most ten times a second); the app, only while `KOPRU_EXPERIMENTAL_XBOXONE` is set, checks that file every 0.1 s and re-maps a rumble that is playing (`ImpulseTuningFile`). So: tick Hold, press Left Trigger or Right Trigger, rest a finger on it, and move the sliders. The line under the sliders shows what the app plays for the level slider (steady state, same code as the app) and the amplitude@Hz at 10/25/50/75/100 %; `wgi-probe.log` gets a "tuning written: ..." line with the numbers, and `app.log` an "impulse trigger tuning: ..." line when the app picked them up. Delete the file to go back to the default.
7. To undo: close the app, close that PowerShell window (or `Remove-Item Env:KOPRU_EXPERIMENTAL_XBOXONE`), start the app normally. In Device Manager (View → Show hidden devices) you can uninstall the leftover "Xbox Gaming Device" / "Xbox One Controller" entries if you want.

### Where to look

| Place | What to look for |
|---|---|
| `%LOCALAPPDATA%\Untethered\logs\app.log` | the lines in the next section |
| Device Manager | a device under **Xbox Peripherals** ("Xbox Gaming Device" for `1`, "Xbox One Controller" / "Xbox Controller" for `one` / `series`) with no warning sign. Properties → Details → Hardware IDs / Compatible IDs should show `USB\MS_COMP_XGIP10`. |
| `joy.cpl` (Win+R) | a game controller entry (an Xbox One pad usually shows as "Controller (Xbox One For Windows)" or similar). Properties → buttons and axes should move with the DualSense. |
| `Kopru.WgiProbe` | an XInput slot "connected", one RawGameController and one Gamepad, changing readings, and the vibration steps showing up in `app.log` |

## Expected log lines

All lines start with `virtual Xbox One (experimental) 1-1:`. A good run looks like this (abbreviated):

```
EXPERIMENTAL KOPRU_EXPERIMENTAL_XBOXONE is set: presenting the Xbox One (GIP) probe pad as generic (1209:0001) instead of the selected type (DualSense)
... GET_DESCRIPTOR device [80 06 0100 0000 len 64] -> answered 18
... GET_DESCRIPTOR string 238 [80 06 03EE 0000 len 18] -> answered 18          <- Windows asks for the MS OS descriptor
... vendor request 0x90 [C0 90 0000 0004 len 16] -> answered 16                <- compatible ID header
... vendor request 0x90 [C0 90 0000 0004 len 40] -> answered 40                <- compatible ID XGIP10
... GET_DESCRIPTOR configuration [...] -> answered 32
... SET_CONFIGURATION 1 [...] -> accepted
... first IN poll on endpoint 1                                               <- a driver is running and polls
... GIP arrival: Hello every 500 ms (device id 0000FFFB..., generic (1209:0001), firmware 1.0.1.0, metadata 198 bytes)
... GIP Hello queued
... first output report 0x04, 4 bytes (interrupt OUT)                          <- the host's first GIP message
... GIP Metadata Request #1 from the host -> sending metadata (198 bytes in 4 fragments, seq 2)
... GIP Protocol Control from the host (flags 0x20, seq ..): .. .. ..         <- raw ACK bytes (layout not published)
... GIP last metadata fragment sent, waiting for the host's ACK
... GIP metadata ACKed by the host -> Metadata Complete sent, waiting for Set Device State START
... GIP Set Device State START (in state Idle)
... GIP Active: status sent, input reports follow
... GIP Guide button LED command from the host (...), ignored
... GIP first input report sent (...)
... GIP motor command #1: bitmap 0x03 (LM RM), levels LT 0 RT 0 LM 50 RM 50, duration ..., delay ..., repeat ...
... GIP FIRST TRIGGER RUMBLE (command #5): bitmap 0x08 (LT), levels LT 50 RT 0 ...
trigger rumble: first trigger rumble played on the DualSense: LT 75 -> amplitude 7, RT 0 -> off (trigger strength 1, 40 Hz from zone 0)
... GIP summary (10 s after START): state Active, Hello 1, metadata requests 1 (completed 1, ...), ... motor commands 12 (with trigger motors 4) ...; motors max levels ..., stopped by their own time 0 (trigger motors 0)
... summary after 10 s: 10.0 s, IN polls ..., input reports ..., output reports ...
```

Also possible, and useful:

- `GIP still no answer to Hello (20 sent)`: the host does not talk GIP to us.
- `GIP no Protocol Control ACK within 1000 ms after the last metadata fragment -> back to Arrival (Hello)`: our metadata framing is not accepted (or the ACK is sent some other way).
- `GIP no Set Device State START within 500 ms after the metadata -> assuming START`: allowed by the spec (3.1.1). Not a failure by itself.
- `GIP Set Device State START ... - no Metadata Request before it: the host had our metadata cached`: normal on a second attach before a reboot.
- `GIP SECURITY message from the host ...`: the host wants the security exchange despite the opt-out GUID. The pad does not answer it.
- `GIP host asked for an ACK of ...`: the host requested an ACK. We cannot answer it because the Protocol Control layout is not in the spec. Worth noting which message it was.
- `GIP Set Device State QUIESCE (focus change) -> motors cleared`: normal when the WGI probe window loses focus.
- `GIP summary (host released)`: when the pad is unplugged or the app closes.

## What each outcome means

| What you see | Meaning | Next step |
|---|---|---|
| No `string 238` request at all | Windows did not ask for the MS OS descriptor, so it cannot know `XGIP10`. Windows asks only once per VID/PID/bcdDevice and caches the answer under `HKLM\SYSTEM\CurrentControlSet\Control\usbflags\120900010100` (for `1209:0001` v1.00). | Delete that key as admin and re-attach, or try `one` (matched by hardware ID, no OS descriptor needed). |
| Device under "Other devices" / code 28 / no Xbox Peripherals entry | No driver bound. | Check the Compatible IDs in Device Manager. Try `one`. |
| Xbox Peripherals entry, but no `first IN poll` | The driver loaded but does not use the endpoints, or failed to start (look at the device status code). | Report the status code and the control request lines. |
| `first IN poll`, Hellos, never a host message | xboxgip runs but ignores our Hello. | Check the Hello fields (device ID, firmware version) against the spec. Try `one`. |
| Metadata Request, then ACK timeouts in a loop | Our fragments or Metadata Complete are not accepted. | Send the logged Protocol Control bytes. They show what the host received. |
| START, Active, input reports, but no XInput slot / WGI gamepad / joy.cpl entry | The driver accepts the device but the upper layers (XboxGipSvc, WGI) do not publish it. | Check the Xbox Accessories app. Try `one` / `series`. |
| Security messages after START, pad stops working | Authentication is still required on the virtual bus. | No-go for this approach unless the opt-out is placed differently. |
| XInput slot, WGI gamepad, readings move | **Questions 1 and 2 are answered: yes.** | Check stick directions (up should be positive Y in WGI) and buttons. |
| `GIP FIRST TRIGGER RUMBLE` during the WGI LeftTrigger / RightTrigger steps | **Question 3 is answered: yes.** Impulse trigger commands reach the device. | Note duration/delay/repeat and how often the commands repeat while a step is held. That is what the trigger-effect mapping must handle. |
| Main motor commands only, no trigger bits during the trigger steps | The trigger values are dropped above the device (WGI / driver decided the pad has no trigger motors). | Try `one` / `series`. The metadata may need another interface. |
| `Set Device State OFF ... OFF came before any START this session` | The host turned the pad off right after the metadata (hardware run 2). | The pad re-announces after 3 s (3 times), or press PS. Send the lines around it, including whether the previous session ended with `powering-off status picked up`. |
| Blue screen | The kernel driver choked on our stream. | Stop; send the last `app.log` lines and the bug check code. |

## Hardware runs

- **Run 1 (2026-11-17, generic identity `1209:0001`).** Everything up to Active worked: OS descriptor and compatible ID, Hello, metadata (198 bytes, 4 fragments), host ACK, START. Input reports flowed. Device Manager showed "Xbox Oyun Cihazı" (XboxComposite) working. XInput slot 0 connected (type 1, subtype 1) with stick values. WGI reported GamepadAdded and a RawGameController "Xbox One Game Controller" VID 1209 PID 0001 with 14 buttons and 6 axes. The host also sent a Set Device State `0x06` with a 15-byte payload (reserved in table 40, ignored), the Guide LED command, a security message `01 00` (we don't take part; nothing broke), and motor command #1: bitmap `0x0F`, all levels 0, duration 255, repeat 235. That looks like a "clear all" on start. The console WGI probe was not in front, so it read zeros and its vibration produced no motor commands. **Questions 1 and 2: yes. Question 3: still open.**
- **Run 2 (app restarted ~10 s later):** OFF right after the metadata ACK. Handled now as described above.
- **Run 3 (a few seconds later):** START → Active again. The console probe only produced a QUIESCE (focus change). Hence the windowed probe.
- **Run 4 (windowed probe, identities `1` and `series`).** The in-box driver bound both; XInput and WGI saw the pad; the focused probe window's trigger steps arrived with the right levels, e.g. `GIP FIRST TRIGGER RUMBLE (command #7): bitmap 0x0F (LT RT LM RM), levels LT 50 RT 0 LM 0 RM 0, duration 255 (2550 ms), delay 0, repeat 235`. **Question 3: yes.** Windows sends all four motors in every command and a held vibration as duration 255 / repeat 235. Next: the trigger rumble mapping above, to be felt and tuned with the probe.

## Known gaps and open questions

- **Protocol Control layout.** Checked again in the full PDF of the spec. It names the Protocol Control message (type `0x01`, flags `0x20`, 9-byte payload, first byte "Control Code", "ACKs transfers and indicates receive buffer status") and refers to a "Protocol Control section" that is not in the published document. So the pad treats any Protocol Control message after the last fragment as the ACK. It cannot answer ACK requests from the host; it logs them in full (`GIP host asked for an ACK of ...`, 4 per type) and counts them in the summary. Run 1 reached Active without our ACKs. If a later step needs them, the host's own ACKs in the log (`GIP Protocol Control from the host ...`) show the layout by example.
- **Set Device State `0x06`** (seen in run 1 with a 15-byte payload) is "Reserved" in table 40, and the 15-byte form is only listed in the message summary. Logged with its payload and ignored.
- **Vendor request `0x90`, `wIndex` 5** (extended properties descriptor) is stalled. Table 4 of the spec says STALL in every device state, so that is correct.
- **Guide button message.** The spec gives the type (`0x07`, 2-byte payload) and says the Xbox button is reported as the virtual key `0x5B`, but the table itself is missing from the published document. The pad sends `01 5B` / `00 5B` (pressed / released). This is an inference. If the PS button does nothing (no Game Bar), that is the reason.
- **Keep-alive.** Input bit `0x02` is named "Keep Alive" in the input report, and the section "Preventing Host from Entering Power Save Mode" is empty in the published document. The pad does not use that bit. Its liveness comes from the periodic status messages (every 1 s for 10 s, then every 20 s).
- **Delay/repeat timing.** The spec gives the fields but not how they combine. We play repeat + 1 cycles of delay (off) then duration (on), per motor, which ends after (delay + duration) × (repeat + 1) × 10 ms. Windows sends delay 0 for a held WGI vibration, where every reading gives the same steady result; a game sending a delay would show whether the pulse order is right.
- **Feel.** Frequency (40 Hz) and the curve (exponent 0.6) are first guesses, to be tuned by hand with the probe's Hold and slider, then in a racing game and a shooter.
- **Share button** (32-byte report, Series pads) is not in this probe.
- **Generic VID/PID.** `1209:0001` is the pid.codes test PID, fine for a private probe but not for a release. A release would use a Microsoft identity, as the Xbox 360 pad does, or a PID of our own.
- **Stick Y sign.** Y is sent up-positive, as on the Xbox 360 pad. The spec only gives the range. Check it in the WGI probe.
