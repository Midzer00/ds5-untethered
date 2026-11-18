# Xbox One / Series emulation for impulse triggers

Researched 2026-11-17. Research only: nothing was built, attached or installed.

**Idea.** Xbox One and Series controllers have a small rumble motor in each trigger ("impulse triggers"). If the app offers a virtual Xbox One/Series pad, the trigger rumble a game sends could be played as DualSense adaptive-trigger vibration, so games with no DualSense support still give feedback in the triggers.

**Short answer.** Technically it looks feasible and cheaper than expected, because Microsoft published the whole wire protocol in 2024 and removed the authentication requirement for wired pads on Windows 10 21H2 and later. The open question is not the protocol but whether Windows' driver accepts the device on the usbip-win2 virtual bus; that needs one small probe. The benefit is real but narrow: about 130 PC games are listed as using impulse triggers.

**Clean-room note.** Sources are Microsoft documentation, the Microsoft open specification `[MS-GIPUSB]`, public wikis and forum prose, and the INF files Windows itself ships (read on this machine). No other project's source code was opened. Search results that pointed at source files (Linux xpad, SDL, xone, gists) were not opened.

How sure each part is:

| Mark | Meaning |
|---|---|
| **[spec]** | Stated in Microsoft's published specification or API docs |
| **[local]** | Read from this machine's `C:\Windows\INF` (Windows 11 26200) |
| **[community]** | Public wiki / forum prose, not checked by us |
| **[guess]** | Our inference; must be confirmed by the probe |

---

## 1. How games reach impulse triggers on PC

### The APIs

| API | Trigger motors? | Notes |
|---|---|---|
| **XInput** (`XInputSetState`) | **No** | `XINPUT_VIBRATION` has only `wLeftMotorSpeed` and `wRightMotorSpeed` **[spec]**. A four-motor `XINPUT_VIBRATION_EX` exists only in the console "XInput on GameInput" docs, listed for Xbox consoles, not Windows **[spec]**. |
| **Windows.Gaming.Input** (`Gamepad.Vibration`) | **Yes** | `GamepadVibration` has `LeftMotor`, `RightMotor`, `LeftTrigger`, `RightTrigger`, each 0.0 to 1.0. On pads with fewer motors "the driver" combines the four values **[spec]**. Windows 10+. Usable from normal desktop games, not only Store apps. |
| **GameInput** (`IGameInputDevice::SetRumbleState`) | **Yes** | `GameInputRumbleParams` has `lowFrequency`, `highFrequency`, `leftTrigger`, `rightTrigger`, each 0.0 to 1.0; supported on Windows and Xbox **[spec]**. This is the API Microsoft now points new games at. |
| **Steam Input** (`TriggerVibrationExtended`) | **Yes, with a catch** | Adds left/right trigger speeds for "Xbox Impulse Trigger motors". On Windows it needs the user to install Steam's "Xbox Extended Feature Support" driver **[spec: Steamworks docs]**. |
| **SDL** (`SDL_RumbleGamepadTriggers`) | **Yes** | Documented as "currently only supported on Xbox One gamepads" (SDL wiki). Any SDL-based game or engine can call it. |
| DirectInput | No | Sees the pad as a generic HID gamepad. |

So the owner's assumption holds: **XInput cannot drive trigger motors**, and a game must use WGI, GameInput, SDL or Steam Input to do it.

### Engines

- **Unreal Engine** reads pads through XInput on Windows by default; an Unreal support answer says impulse triggers are "still not supported by XInput on Windows" **[community]**. A UE game gets them only if the studio adds a GameInput/WGI path (Microsoft first-party titles do).
- **Unity**'s input system has an `IXboxOneRumble` interface with trigger motor speeds, but it is tied to the Xbox One platform back-end; on Windows the default back-end is XInput **[community]**.
- **Microsoft GDK titles** (Forza, Gears, Halo and other Xbox Game Studios ports) use GameInput and are the most reliable users.
- **Codemasters/EA racing** games are the best known third-party users (DiRT 3, DiRT Rally, GRID Autosport, F1 2012 are named in an old forum thread **[community]**).

### How common is it?

- PCGamingWiki keeps an auto-generated "List of games that support Xbox One impulse triggers". A search snippet of the page reports **"Total number of games: 130"**, counting entries marked true, limited or hackable **[community]**. Out of the PC catalogue that is small.
- The same wiki page notes that "many games implement only limited trigger vibration" and that the feature "isn't yet a PC standard" **[community]**.
- Players regularly ask publishers to add it: there are EA forum threads asking for impulse trigger support in WRC and other EA racing games on PC, and Steam threads calling the supported list "rather short" **[community]**.
- Steam version quirks: a Steam thread on Forza Horizon 5 reports impulse triggers working on one pad model and not another until the Steam extended driver is installed **[community]**. Steam Input sitting between the game and the pad is a known source of lost trigger rumble.
- Prior art: PCGamingWiki says the tool **Special K "can map Impulse Trigger vibration to DualSense adaptive triggers"** **[community]**. So the mapping idea is proven to be worth having; Special K does it by injecting into the game, we would do it at the device level with no injection.

**Reading.** Trigger rumble is common in racing games from Microsoft and Codemasters and in a number of Microsoft shooters, rare elsewhere. Most games without DualSense support only call XInput and will send nothing to the triggers whatever pad we emulate.

Sources:
- <https://learn.microsoft.com/en-us/windows/desktop/api/xinput/ns-xinput-xinput_vibration>
- <https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.gamepadvibration>
- <https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/structs/gameinputrumbleparams>
- <https://partner.steamgames.com/doc/api/ISteamInput>
- <https://wiki.libsdl.org/SDL3/SDL_RumbleGamepadTriggers>
- <https://www.pcgamingwiki.com/wiki/List_of_games_that_support_Xbox_One_impulse_triggers>
- <https://www.pcgamingwiki.com/wiki/Controller:Xbox_Wireless_Controller>
- <https://answers.unrealengine.com/questions/181472/view.html>
- <https://www.overclock.net/threads/how-many-games-take-advantage-of-impulse-triggers-xbox-one-controller-on-pc.1599586/>
- <https://forums.ea.com/discussions/wrc-general-discussion-en/impulse-trigger-support-for-pc-xbox-controllers/8380279>
- <https://steamcommunity.com/discussions/forum/1/3951406749567112243>

---

## 2. How a wired Xbox One / Series controller works on Windows

The protocol is **GIP (Gaming Input Protocol)**. Since 2024-09-16 it is a Microsoft Open Specification, **`[MS-GIPUSB]` revision 1.0**, with a notice that implementers may copy from it to build implementations. Everything marked **[spec]** below is from that document. Its companion download (aka.ms/gipdocs) has wire traces and a metadata compiler; we did not need it for this study.

Spec root: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/e7c90904-5e21-426e-b9ad-d82adeee0dbc>

### 2.1 USB identity and descriptors **[spec]**

- **Not HID.** Device class `FF`, subclass `47`, protocol `D0`. USB 2.0, `bMaxPacketSize0` 64, one configuration, `bmAttributes` `A0` (bus powered, remote wakeup), max power non-zero and at most 500 mA.
- Strings: 1 manufacturer, 2 product, 3 serial. The serial is 32 hex digits and carries the 64-bit device ID.
- **Interface 0, the GIP data interface:** class `FF` / subclass `47` / protocol `D0`, two endpoints: interrupt OUT `0x01` and interrupt IN `0x81`, both 64-byte packets, `bInterval` at least 4 ms (so at most 250 Hz).
- A pad with a headset jack has a second interface with isochronous audio endpoints. A pad without audio has **one interface only**. We can leave audio out.
- **Microsoft OS descriptor:** string index `0xEE` returns `MSFT100` with vendor code `0x90`; the extended compat ID descriptor (40 bytes, `wIndex` 4) names interface 0 with compatible ID **`XGIP10`**. That is how Windows recognises a GIP device from any vendor.

VID/PID of Microsoft's own pads **[local, from `dc1-controller.inf`]**: `045E:02D1`, `02DD`, `02E3`, `02EA`, `0B00`, `0B06`, `0B0A`, `0B12`. PCGamingWiki names `02EA` as the Xbox One S pad (model 1708) and `0B12` as the Series pad (model 1914) **[community]**.

### 2.2 Windows driver stack **[local]**

Read from the INF files on this machine (driver version 10.0.26100.9818):

- **`dc1-controller.inf`** (class "Xbox Peripherals") is the function driver. It matches the Microsoft VID/PID list above **and the generic compatible ID `USB\MS_COMP_XGIP10`** ("Xbox Gaming Device"). It installs `dc1-controller.sys` and registers **`xboxgip`** as an upper filter.
- **`xboxgip.inf`** installs `xboxgip.sys` ("Xbox Game Input Protocol Driver") and also ships **`devauthe.sys`**, the device authentication helper.
- `xboxgipsynthetic.inf` describes an "Xbox Virtual Game Controller" (`SWD\XBOXGIP_SYNTHETIC`). It is an internal software device with no documented way to create one; not usable by us.
- `xinputhid.inf` only matches Bluetooth hardware IDs of Xbox pads. It is not a route for a USB device.

Above the drivers, a user-mode service (`XboxGipSvc`) and the WGI/GameInput runtime expose the pad to games. Classic XInput and DirectInput games still see a GIP pad: the stack presents an XInput slot and a HID gamepad child for them, which is why an Xbox One pad "just works" in old games **[community]**. PCGamingWiki lists it in joy.cpl as "Controller (Xbox One for Windows)".

Matching by `MS_COMP_XGIP10` matters: Windows should bind the driver from the OS descriptor alone, so in principle we do not have to copy a Microsoft PID.

### 2.3 Message framing **[spec]**

Every packet in both directions starts with a 4-byte header:

| Byte | Field | Meaning |
|---|---|---|
| 0 | Message type | Which message |
| 1 | Flags | bit 7 fragment, bit 6 first fragment, bit 5 system message, bit 4 "acknowledge me", bits 2-0 expansion index (0 = the pad itself) |
| 2 | Sequence ID | Per-class wrapping counter, 0 is reserved |
| 3 | Payload length | 7 bits; bit 7 set means the length continues in the next byte |

Messages we would need:

| Type | Direction | Name | Use |
|---|---|---|---|
| `0x01` | both | Protocol Control | ACK for messages that asked for one |
| `0x02` | pad to host | Hello Device | Announce, every 500 ms until the host answers |
| `0x03` | pad to host | Status Device | Battery/state; on start, then every 1 s for 10 s, then every 20 s |
| `0x04` | both | Metadata Request / Response | Host asks for the pad's capability blob; pad answers, usually in fragments |
| `0x05` | host to pad | Set Device State | Start, Stop, Off, Quiesce, Reset |
| `0x06` | both | Security | Authentication exchange (see 2.5) |
| `0x07` | pad to host | Guide Button Status | The Xbox button, sent separately from the input report |
| `0x09` | host to pad | **Direct Motor Command** | **Rumble, all four motors** |
| `0x0A` | host to pad | Guide Button LED | Ignore |
| `0x1E` | both | Extended Command | Serial number, telemetry; only if metadata claims support |
| `0x20` | pad to host | **Gamepad Input Report** | Buttons, triggers, sticks |

### 2.4 Start-up sequence **[spec]**

1. Normal USB enumeration; Windows reads the `0xEE` string and the compat ID descriptor.
2. **Arrival state.** The pad sends **Hello** (type `0x02`, flags `0x20`, 28-byte payload) every 500 ms. Payload: 8-byte device ID (top four bytes `00 00 FF FB`, the other four random at each power-up; two pads with the same ID both fail), VID, PID, firmware version major/minor/build/revision (must not be all zero; major/minor must appear in the metadata), hardware version, then three fixed version pairs `01 00` (RF, security, GIP).
3. The host answers with **Metadata Request** (type `0x04`, empty payload), up to 4 times at 500 ms, or skips it when it already has metadata cached for that VID/PID/firmware version (the cache is cleared by a reboot).
4. The pad sends the **metadata** blob. It is the GIP counterpart of a HID report descriptor: supported system commands, the type name (`Windows.Xbox.Input.Gamepad`), interface GUIDs (IController, IGamepad, INavigationController) and the list of vendor messages: input `0x20` length 14 and output `0x09` length 9. The spec prints a complete example: the JSON and its **182-byte compiled blob** for a standard gamepad. The blob is longer than one 64-byte packet, so it goes out as fragments (first fragment flags `0xF0` with the total length, 58 data bytes), the host ACKs with Protocol Control, and the pad ends with an empty "Metadata Complete" (flags `0xA0`). The pad must ask for an ACK on the first and last fragment and at least every 100 ms.
5. The host sends **Set Device State: Start** (type `0x05`, payload `00`). The pad is now **Active**: it sends a Status message and may send input reports. If Start does not arrive within 500 ms after metadata the pad may assume it.
6. Optionally the security exchange (2.5), then normal traffic.

Other state values: `01` Stop (go idle), `04` Off, `05` Quiesce (app lost focus: clear all motors), `07` Reset (send a "powering off" status, wait 500 ms, start again from Hello).

### 2.5 Authentication: is it needed on PC?

**No, not on current Windows, for wired pads [spec].**

- Appendix B, note 1: "Removed device authentication requirement for USB endpoints in **Windows 10 v21H2**, Windows 11, and later."
- Section 5.1: "Controllers that talk to a Windows PC SHOULD use the following GUID to opt-out of the security exchange over USB", added to the metadata's `SupportedInterfaces`: **`7a34ce77-7de2-45c6-8ca4-0042c08bd94a`**. And: "The host succeeds the security exchange by default."

Consoles are different: an Xbox console requires the security exchange with a Microsoft-provisioned chip, which is why licensed third-party pads (PowerA, PDP, HORI and so on) carry one. That is irrelevant for a PC-only virtual pad. Third-party licensed pads on PC bind through the same `MS_COMP_XGIP10` compatible ID.

Windows 10 versions before 21H2 did require authentication and cannot be supported. The app would have to gate the pad type on the OS build (19044 or later).

### 2.6 Input report **[spec]**

Type `0x20`, flags `0x00`, 14-byte payload, sent only when something changed:

| Payload byte | Content |
|---|---|
| 0 | `0x04` Menu, `0x08` View, `0x10` A, `0x20` B, `0x40` X, `0x80` Y (`0x02` keep-alive, `0x01` reserved) |
| 1 | `0x01` D-pad up, `0x02` down, `0x04` left, `0x08` right, `0x10` LB, `0x20` RB, `0x40` left stick, `0x80` right stick |
| 2-3 | Left trigger, 0 to 1023, little-endian |
| 4-5 | Right trigger, 0 to 1023 |
| 6-13 | Left X, left Y, right X, right Y, signed 16-bit little-endian |

- The **Guide** button is not in this report; it is its own message (type `0x07`, 2-byte payload).
- The **Share** button (Series pads, 2020) uses the "Console Function Map" extension: the input report grows to a **32-byte** payload, byte 14 is `01` while Share is held, the rest zero; the metadata declares message length 32 and adds interface GUID `ECDDD2FE-D387-4294-BD96-1A712E3DC77D`.

Compared with our Xbox 360 report (`src/Kopru.Core/Virtual/XboxReports.cs`): same buttons in different bits, triggers 10-bit instead of 8-bit, sticks the same signed 16-bit.

### 2.7 Rumble: the Direct Motor Command **[spec]**

Type `0x09`, flags `0x00` (not a system message, no ACK), 9-byte payload:

| Payload byte | Field | Range |
|---|---|---|
| 0 | Sub-command | `00` |
| 1 | Motor bitmap | bit 3 **left impulse (trigger)**, bit 2 **right impulse (trigger)**, bit 1 left vibration, bit 0 right vibration |
| 2 | **Left impulse level** | **0 to 100** (percent of PWM) |
| 3 | **Right impulse level** | 0 to 100 |
| 4 | Left vibration level | 0 to 100 |
| 5 | Right vibration level | 0 to 100 |
| 6 | Duration | 0 = cancel all motors; 1 to 255 in 10 ms steps (at most 2.55 s) |
| 7 | Delay | 0 to 255 in 10 ms steps |
| 8 | Repeat | 0 = play once; 1 to 255 repeats |

Points that matter for us:

- The wire level is **0 to 100, not 0 to 255**. The API's 0.0 to 1.0 float is turned into a percentage by the driver.
- The command is **timed**. The pad itself must stop the motors when duration (and repeats) run out, so the host has to refresh a held rumble. We need a small timer per command, which the Xbox 360 pad did not need. How Windows fills duration/delay/repeat for a plain "set vibration" call is not in the spec; the probe will show it **[guess: long duration, refreshed]**.
- Rate: bounded by the OUT endpoint interval (4 ms or more, at most 250 per second). In practice games set vibration once per frame or on events.
- The bitmap says which motors the command changes. An XInput game produces commands that only carry the two main motors.

---

## 3. Feasibility over usbip-win2

### What we would present

A full-speed (or high-speed) vendor-class device with one interface and two interrupt endpoints. That is simpler than what `VirtualXbox360` already presents (four interfaces) and far simpler than `VirtualDualSense`/`VirtualDualShock4` (HID plus isochronous audio). The new parts are:

1. Answering string `0xEE` and the vendor request `0x90` / `wIndex` 4 with the compat ID descriptor. Today `VirtualXbox360.Control` stalls unknown string indexes and answers vendor IN requests with zeros (`src/Kopru.Core/Virtual/VirtualXbox360.cs`), so this is a few lines.
2. A small GIP state machine: Hello timer, metadata fragments with ACK handling, Set Device State, Status timer, sequence counters.
3. Report conversion both ways, next to `XboxReports`.

Nothing needs a new driver: `dc1-controller` and `xboxgip` are in-box on Windows 10/11.

### Will Windows bind xboxgip to it? **[guess, the main risk]**

Reasons to expect yes:

- usbip-win2's virtual host controller is built on Microsoft's USB device emulation framework, so Microsoft's own hub driver does the enumeration, including the OS descriptor query. To Windows the device is an ordinary USB device. The same reasoning held for `xusb22` and the Xbox 360 pad, which works today.
- The INF matches on VID/PID *or* on `USB\MS_COMP_XGIP10`; either is enough.
- The driver is a plain KMDF function driver plus filter; nothing in the INF ties it to a physical controller type.

Risks, in order of how much they worry us:

| Risk | Detail | How we would know |
|---|---|---|
| **Hidden checks in the driver** | The spec says USB authentication is no longer required, and `devauthe.sys` is still shipped. If the driver still starts a security exchange and waits for an answer, the pad would stall after Start. The documented opt-out GUID in metadata is the first remedy. | Probe log shows a type `0x06` message from the host and what follows |
| **Metadata strictness** | The host stops talking to the pad if the Hello firmware version is not in the metadata, and it caches metadata per VID/PID/firmware version. If we reuse a Microsoft PID, Windows may already hold (or ship) metadata for it and ignore ours. | Whether a Metadata Request arrives; whether a 32-byte Share report is accepted |
| **Timing over USB/IP** | GIP has 100 ms ACK windows and 500 ms retries. Our local TCP round trip is far below that, but the fragment/ACK logic must be exact. | Probe log timestamps |
| **Kernel driver robustness** | A malformed GIP stream reaches a kernel driver. There are public reports of `xboxgip.sys` bug checks with faulty real hardware **[community]**. A buggy probe could blue-screen the test machine. | Run the probe in a virtual machine first |
| **Guide button side effects** | Windows opens Game Bar on the Xbox button; the Xbox Accessories app may list the pad and offer a firmware update or remapping. | Observation |
| **Old Windows** | Before Windows 10 21H2 authentication is required. | OS build gate |
| **Polling-rate remark** | PCGamingWiki notes impulse triggers failing when a pad's polling rate is above 250 or 500 Hz **[community]**. Keep `bInterval` at 4 ms as the spec says. | - |

"Xbox Wireless Adapter style" versus wired: the adapter is a different, radio-side device that hosts several pads and needs its own firmware and pairing. Wired GIP is the only style worth emulating; it is the documented one and needs one interface.

### Which identity to use

- **A. Generic GIP device** (our own or a neutral VID/PID, recognised by `XGIP10`). Honest, no clash with cached Microsoft metadata, no firmware-update offers. Risk: games, SDL and Steam that recognise pads by VID/PID may not label it as an Xbox pad (XInput/WGI/GameInput games do not care).
- **B. Copy a Microsoft pad** (`045E:02EA` One S, or `045E:0B12` Series for the Share button). Best recognition everywhere, as we do for the Xbox 360 pad. Risk: metadata cache clash, Accessories app attention.

The probe should try A first (cleanest test of the protocol), then B.

### Safe verification: a probe device (described, not done)

A probe is a throw-away `IUsbIpDevice` on the existing virtual bus, not wired to any controller, that logs everything through `PadRequestLog`:

1. In a **virtual machine** with usbip-win2 installed (so a driver crash costs nothing), attach the probe: descriptors from 2.1, OS descriptor answers, Hello every 500 ms.
2. Watch Device Manager: does "Xbox Gaming Device" / "Xbox Controller" appear under "Xbox Peripherals" with no error code? Log every control request (is `0xEE` asked? is vendor request `0x90` asked?).
3. Log what the host sends on the OUT endpoint: Metadata Request? Then send the spec's example 182-byte blob in fragments and log the ACKs. Does Set Device State: Start arrive? Does a security message arrive?
4. Send a fixed input report (A pressed). Check joy.cpl, an XInput test tool and a tiny Windows.Gaming.Input test program: does a gamepad appear in each?
5. From the WGI test program set `Vibration` with all four values and log the Direct Motor Commands: bitmap, levels, duration/delay/repeat, how often they repeat, what is sent on focus loss.
6. Repeat with identity B and with the 32-byte Share report.

Steps 1 to 3 already answer go/no-go. No real controller, HidHide change or new driver is involved.

---

## 4. Mapping trigger rumble to DualSense trigger effects

### What we have today (code)

- Trigger effects are 11-byte blocks built in `src/Kopru.Core/Protocol/TriggerEffects.cs`. `Vibration(position, amplitude, frequency)` produces mode `0x26`: a zone mask, **amplitude 1 to 8** per zone and a **frequency 1 to 255 Hz**. `MultiplePositionVibration` gives a different amplitude per zone.
- A virtual pad hands the bridge a DualSense-format output report through `IVirtualPad.OutputReceived` (`src/Kopru.Core/Virtual/IVirtualPad.cs`). The Xbox 360 pad does this in `XboxReports.RumbleToDualSense`, setting only the two motor bytes (`src/Kopru.Core/Virtual/XboxReports.cs`).
- `ControllerBridge` (`src/Kopru.Core/Bridge/ControllerBridge.cs`) takes that report: it drops repeats of an identical block, remembers the game's effect per trigger, sets `triggersSeen` on the first real effect, scales effects by trigger strength in `ApplyTriggers` (`TriggerEffects.Scale` already handles mode `0x26`), applies the fallback effect only while the game has sent none, and sends the result at once with `BluetoothSession.UpdateOutput` (`src/Kopru.Core/Bluetooth/BluetoothSession.cs`).

### Proposed mapping

The new pad converts each Direct Motor Command into one DualSense output report that carries **both** the main motors (as the 360 pad does) **and** a trigger effect per trigger, with the `RightTriggerEffect` / `LeftTriggerEffect` valid bits set only for the triggers named in the motor bitmap. Nothing downstream has to change.

| Impulse level (0-100) | DualSense effect |
|---|---|
| 0, or duration expired | `TriggerEffects.Off()` |
| 1-100 | `Vibration(position 0, amplitude = round-up(level x 8 / 100), frequency f)` |

- **Amplitude** follows the level in 8 steps. That is coarse, and helpful: small level changes do not produce a new Bluetooth write, because the bridge drops identical blocks.
- **Frequency** is ours to choose; an impulse motor is a small spinning weight whose pitch rises with drive level. A starting point is a rising curve, roughly 25 Hz at low levels to 60 Hz at full **[guess]**, tuned by hand on the controller. It could become a per-game or global setting ("soft / sharp").
- **Position 0** so the buzz is felt wherever the finger rests. `MultiplePositionVibration` could later make it stronger deeper in the pull.
- **Duration/delay/repeat** are honoured by a timer in the pad class that emits the Off report when the command runs out.

### Latency and feel

- Path: game call, Windows driver, USB/IP OUT transfer (local TCP), conversion, one Bluetooth output report. This is the same path the DualSense pad's native trigger effects and the 360 pad's rumble already take; expect a few milliseconds plus the Bluetooth interval, then the trigger motor's own reaction time. No audio stream is involved, so it cannot be late the way haptics audio can.
- A real impulse trigger is a light, buzzy motor on the trigger blade. The DualSense produces vibration by driving the trigger's resistance mechanism back and forth. It is stronger and more "mechanical", is felt mostly while the finger rests on or presses the trigger, and can rattle audibly when the finger is off the trigger. Expect "different but clearly there" rather than an imitation. Level-to-amplitude scaling and the trigger strength slider tame it.
- Fast patterns (ABS pulsing, gear shifts, gunfire) arrive as a series of on/off commands. Each change is one Bluetooth write; at 60 changes per second that is fine. If a game modulates level every frame, the 8-step quantisation keeps writes down.

### Interaction with the app's fallback trigger effect and trigger strength

- **Trigger strength** (`TriggerIntensity`): applies automatically, since the converted effect is an ordinary mode `0x26` block and `TriggerEffects.Scale` scales its amplitudes while keeping at least the lowest step.
- **Fallback effect** (`BridgeProfile.FallbackTrigger`; Off / Feedback / Weapon / Vibration in `src/Kopru.Core/Games/GameProfiles.cs`): today it is shown only until the game sends its first real trigger effect in the session, then stays off. A translated impulse command would count as that first real effect. That is the right default (a game that drives the triggers itself should own them), but it means a user who likes a resistance fallback loses it the moment the first trigger rumble arrives. A trigger can hold only one effect, so resistance and vibration cannot be mixed. Possible later choice: "keep fallback, play impulse over it only while it is active" (put the fallback back when the impulse level returns to 0). That is a small rule change in `ApplyTriggers` and should be the owner's call.
- **Game use signals**: the bridge would raise `GameUse.TriggerEffect`, so the UI's "game is using triggers" indication works unchanged.
- Games that only use XInput send bitmap bits for the main motors only; triggers stay with the fallback effect exactly as on the Xbox 360 pad.

### A cheaper cousin worth noting

For the large majority of games that never send trigger rumble, the same feel can be approximated by deriving trigger vibration from the **main** rumble motors (the public tool X1nput does this for real Xbox pads **[community]**). That needs no new pad type and works with the existing Xbox 360 and DualShock 4 pads. It is a separate, small feature and complements this one.

---

## 5. What else an Xbox One / Series pad would give, and the downsides

Gains over the Xbox 360 pad:

- **Trigger rumble** in games that send it (the point of this study).
- **Share button** on a Series identity: the DualSense Create button or mute button could map to it, leaving View and Menu clean.
- **Seen as a modern pad** by GameInput and Windows.Gaming.Input, which is what Microsoft Store / Game Pass / GDK titles expect. Whether this fixes any game the 360 pad fails in is unproven; XInput games behave the same.
- 10-bit triggers instead of 8-bit (no practical difference: the DualSense trigger is 8-bit).
- Based on a published specification with explicit permission to implement, rather than on community notes as the 360 pad is.

Downsides:

- **Only Windows 10 21H2 and later.**
- **More moving parts**: a stateful protocol (Hello, metadata fragments and ACKs, timers, sequence numbers, device states) against a kernel driver, versus the 360 pad's two fixed reports.
- **Steam Input**: when Steam wraps the pad, trigger rumble through Steam needs Steam's extended driver, and Steam may hide the trigger motors from the game. Users may have to turn Steam Input off per game, as they do with real Xbox pads.
- **Windows side effects**: Game Bar on the Xbox button, the pad appearing in the Xbox Accessories app.
- **Still no light bar, speaker, native haptics or adaptive effects** from the game: like the 360 pad it is a fallback for games without DualSense support.
- **One more pad type** in the UI and in testing (the choice already has DualSense, DualShock 4 and Xbox 360). It could replace the 360 type only after it has proven itself, since the 360 pad works on older Windows and with every XInput game.

---

## 6. Recommendation

**Go for the probe; decide on the full feature from its result.** Conditional go.

Why: the two things that usually kill this kind of emulation, an undocumented protocol and mandatory hardware authentication, are both gone. What is left is one empirical question (does the in-box driver run happily on the virtual bus) that a short probe answers. The pay-off is a feature no other Bluetooth DualSense tool offers at device level, in the game genres (racing, Microsoft shooters) where trigger feedback matters most. The limit is honest: around 130 games.

**Effort: M** overall.

| Step | Size | Done when |
|---|---|---|
| 1. Probe device on the virtual bus, in a VM (section 3) | S | We know: driver binds, metadata accepted, Start received, no security stall, Direct Motor Command contents logged |
| 2. Decide identity (generic GIP or Microsoft PID) and whether Share is in | - | From probe results |
| 3. `VirtualXboxOne` pad: descriptors, OS descriptor, GIP state machine, input conversion, Guide message | M | Pad works in joy.cpl, XInput and WGI test tools; unit tests for framing, fragments, timers |
| 4. Direct Motor Command to DualSense report (main motors + trigger vibration, duration timer) | S | Trigger rumble from a WGI test program is felt on the controller; trigger strength scales it |
| 5. Tune frequency/amplitude curve by hand in two or three real games (one racing, one shooter) | S | Owner is happy with the feel |
| 6. UI: pad type choice, OS build gate, texts (TR/EN), fallback-effect rule decision | S | - |
| 7. Optional: "rumble to triggers" for games that send no trigger rumble (all pad types) | S | Separate decision |

No-go conditions after step 1: the driver refuses the device or demands authentication despite the opt-out, or it proves unstable on the virtual bus. In that case step 7 alone still delivers most of the feel for all games, at small cost.

---

## 7. Making the DualSense trigger feel like an impulse trigger

Researched 2026-11-18, after the first hardware run (trigger rumble felt at a fixed 40 Hz). Sources are public teardowns, motor-maker application notes and datasheets, reviews and Microsoft/W3C documentation; no other project's source code was opened. Marks as in the table at the top, plus **[maker]** for a motor maker's application note or datasheet.

### 7.1 What is in an Xbox trigger

- **A small ERM (eccentric rotating mass) motor inside each trigger.** Fictiv's Xbox One controller teardown: the triggers "have embedded ERM vibration motors inside" **[community]**. iFixit has a separate "Trigger Rumble Motor Replacement" guide: the motor sits in the trigger assembly and is soldered by two wires **[community]**. Precision Microdrives (a vibration motor maker) describes them as two extra vibration motors that give "precise haptic feedback to the fingertips" **[maker]**. No source gives the part number or size; replacement parts are tiny cylinder motors.
- **The motor moves with the trigger blade**, so it is felt wherever the finger is on the travel, released or fully pulled **[guess, from the placement]**.
- **Drive:** the Direct Motor Command level is 0-100 % of PWM (section 2.7) **[spec]**. WGI's `GamepadVibration.LeftTrigger` / `RightTrigger` are 0.0-1.0 **[spec]**.

### 7.2 How an ERM behaves

From Precision Microdrives' application bulletins **[maker]**:

- **Frequency follows speed, speed follows voltage.** Vibration frequency = rpm / 60; "as the applied voltage is increased, the vibration frequency increases proportionally", and the vibration amplitude rises roughly with the square (centrifugal force m·r·ω²). An ERM cannot change pitch and strength separately: one voltage gives one frequency and one amplitude (their 3D voltage/amplitude/frequency plots). PWM drive acts like a lower average voltage.
- **Numbers for a motor of this class** (4 mm cylinder ERM, model 304-10K): rated 2.7 V, start voltage 1 V, 13,500 rpm at rated voltage = **225 Hz**; lag time (to a first feelable 0.08 G) **10 ms**, rise time (to 50 % of the amplitude) **27 ms**, stop time **30 ms**, with active braking 16 ms. If speed is proportional to voltage, the start voltage (37 % of rated) is roughly 80 Hz: a usable pitch range of about **3 : 1** from the lowest to the highest drive.
- Power Electronic Tips gives 50-100 ms for an ERM to rise or fall to 90 % of its target **[community]**, i.e. a time constant of about 22-43 ms; consistent with the datasheet.
- The motor needs time to spin up: very short pulses ("clicks") never reach steady state, so a short pulse is weaker and lower in pitch than a held level **[maker]**. Motor makers use overdrive and braking pulses to sharpen this; whether the Xbox pad does is unknown.

### 7.3 How games drive it

- **Racing, continuous and level-modulated:** Forza Motorsport 5 (the launch showcase) puts tyre slip on the triggers: the throttle trigger when the driven wheels spin, the brake trigger when the wheels lock, and the ABS pulsing under braking; reviewers describe the left trigger reacting to hard braking and the right one twitching at the top of the rev range (Shacknews, Expert Reviews, news.xbox.com) **[community]**.
- **Shooters, short pulses:** recoil kicks on the fire trigger are the commonly described use **[community, no primary source found]**. The protocol is built for this: the Direct Motor Command has duration, delay and repeat fields in 10 ms steps, i.e. pulse trains played by the pad itself **[spec]**.
- **Web games:** the W3C Gamepad "trigger-rumble" effect (proposed by Microsoft Edge engineers; announced for Chrome 126) takes `leftTrigger` / `rightTrigger` 0.0-1.0 plus `duration` and `startDelay` in ms; its example plays 1 s at 0.5 / 1.0 **[spec: explainer / MDN]**.

### 7.4 What the emulation does (defaults: preset "Xbox-like")

| Real ERM | Emulation | Default | Grounding |
|---|---|---|---|
| Pitch rises in proportion to drive | frequency = MinHz + (MaxHz − MinHz) × x^FrequencyCurve, x = level / 100, in eighths of the range | **30-90 Hz, curve 1.0** (linear) | PMD: frequency ∝ voltage; 3 : 1 range (start to rated voltage) kept. The band itself is moved down from the real 80-225 Hz: the DualSense trigger vibration is the trigger's resistance motor shaking the blade through its gears, and the app's own Vibration fallback uses 30 Hz. **[guess]** that 90 Hz is still strong on the DualSense: tune by feel. |
| Strength rises steeply (≈ square) with drive | amplitude = round(8 × x^AmplitudeCurve), 1-8, at least 1 | **curve 0.8** | Deliberately not the physical square: games use low levels expecting a light but felt rumble, and the rising pitch already makes low levels softer. Between the first build's 0.6 and linear. |
| Spin-up / spin-down (inertia) | first-order lag on the level, time constant AttackMs rising, DecayMs falling; a motor slowing towards a stop stalls below 10 % | **25 ms / 35 ms** | Datasheet rise 27 ms, stop 30 ms (stop a little longer: no braking assumed); 22-43 ms from the 90 % figure. First report about 10 ms after a game's command (pump tick), like the ERM's 10 ms lag. A 20 ms full-level pulse peaks around amplitude 5 instead of 8, as an unbraked ERM would. |
| Vibrates at any trigger depth | `TriggerEffects.Vibration` from **zone 0** to 9 (mask `0x3FF`) | zone 0 | Zone 0 is the released trigger, the earliest start mode `0x26` has; the first hardware run felt it with the finger resting. Without a finger the blade rattles (as noted in section 4); a later "start zone" knob is not needed. |
| — | at most one output report per 10 ms (both triggers share it), and only when a trigger's (amplitude, Hz) step changes | 10 ms | Keeps the Bluetooth output well below the controller's report rate; a full spin-up is about 5-10 reports. |

Steady state of the default: 10 % → amplitude 1 at 38 Hz, 25 % → 3 at 45 Hz, 50 % → 5 at 60 Hz, 75 % → 6 at 75 Hz, 100 % → 8 at 90 Hz.

Other presets: **Soft** 25-60 Hz, amplitude curve 1.2, 45 / 80 ms (gentle, lower pitch, slow). **Sharp** 45-130 Hz, frequency curve 0.8, amplitude curve 0.6, 10 / 15 ms (punchy, like an overdriven and braked motor).

Not emulated: the real motor's dead band below its start voltage (a game's 5 % stays a felt amplitude 1 here, on purpose), and overdrive/braking pulses.

### 7.5 Tuning by feel

All six numbers live in `ImpulseTuning` and can be changed while the app runs, **only with the experimental switch on**: the WGI probe's "Trigger feel" sliders and preset buttons write `%LOCALAPPDATA%\Untethered\impulse-tuning.json`, the app checks it every 0.1 s (`ImpulseTuningFile`) and re-maps a rumble that is playing. How to use it: `xbox-one-probe.md`, "How to run it", step 6. The numbers to report back are in `wgi-probe.log` ("tuning written: ...") and `app.log` ("impulse trigger tuning: ...").

### 7.6 Sources

- Fictiv, Xbox One controller teardown (trigger ERMs): <https://fictiv.com/?p=4298>
- iFixit, Xbox One Wireless Controller Trigger Rumble Motor Replacement: <https://www.ifixit.com/Guide/Xbox+One+Wireless+Controller+Trigger+Rumble+Motor+Replacement/36558>
- Precision Microdrives, Xbox One controller haptics: <https://www.precisionmicrodrives.com/new-xbox-one-controller-increasing-haptic-feedback-offering>
- Precision Microdrives AB-004, ERM characteristics (frequency ∝ voltage, amplitude ≈ square): <https://www.precisionmicrodrives.com/?p=319>
- Precision Microdrives, voltage/amplitude/frequency in 3D: <https://www.precisionmicrodrives.com/visualising-voltage-amplitude-and-frequency-relationships-in-3d>
- Precision Microdrives AB-013, rise time, momentum, overdrive: <https://www.precisionmicrodrives.com/ab-013>
- Precision Microdrives 304-10K datasheet page (rated/start voltage, speed, lag/rise/stop/brake times): <https://www.precisionmicrodrives.com/product/304-10k-4mm-vibration-motor-6mm-type>
- Power Electronic Tips, haptics components part 1 (ERM rise/fall 50-100 ms): <https://www.powerelectronictips.com/haptics-components-pt-1-lra-erm-and-piezo-actuators/>
- Shacknews, how Forza Motorsport 5 uses impulse triggers: <https://www.shacknews.com/article/81340/how-forza-motorsport-5-uses-xbox-ones-impulse-triggers>
- Expert Reviews, Forza Motorsport 5 review: <https://www.expertreviews.co.uk/technology/gaming/forza-motorsport-5-review>
- Xbox Wire, Forza Motorsport 5 and impulse triggers: <https://news.xbox.com/en-us/?p=11803>
- Chromium intent to prototype, Gamepad trigger-rumble: <https://groups.google.com/a/chromium.org/g/blink-dev/c/ncvmDRl0AzM/m/z4v34arEAQAJ>
- MDN, `GamepadHapticActuator.playEffect()`: <https://developer.mozilla.org/en-US/docs/Web/API/GamepadHapticActuator/playEffect>
- Microsoft, `GamepadVibration`: <https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.gamepadvibration>; Direct Motor Command: see the `[MS-GIPUSB]` list below.

---

## Sources

Microsoft specification `[MS-GIPUSB]` (published 2024-09-16, revision 1.0), sections used:

- Overview and start-up: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/d50b090d-6218-4165-b097-5c23b23e02b9>
- OS and compat ID descriptors: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/1d965c3f-b367-4060-a15e-83e45550119e>
- Device descriptor: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/d326fff6-60c1-4354-be28-895321df4357>
- Configuration descriptor: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/9472e04d-35ca-4c25-82dd-26b54255a4a3>
- GIP data interface and endpoints: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/9c679e2d-9af0-433b-a43a-527b8976f166>
- Message header and flags: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/0c2e568f-8274-45ce-bc34-16b519e8a9f0>, <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/6f1fca56-3d86-40bd-8da6-94a2a724c04a>
- Message summary: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/c66cee2f-7728-4ec1-8f3c-e9ab3aa4d696>
- Hello: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/767240e3-ec9e-443e-b364-9cfba82b9fe6>
- Device ID: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/16b9702e-553e-46e3-b44f-03255cd24f90>
- Metadata exchange, example JSON and blob: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/405cbf7a-642f-4ae5-94a4-51b4dee057de>
- Supported interfaces: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/12feb7ea-3095-4dea-8288-ae66def54ecb>
- Metadata response fragments: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/cc499d6b-e986-4a12-8e51-234ff05d15e8>
- Acknowledgement rules: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/5a4be035-9e20-4350-b81a-a08627a985ca>
- State machine: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/bb1ee3e9-60c4-4a27-ad8f-228ed36c650c>
- Set Device State: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/8eaad00e-97e6-4ae0-86fa-471131649d70>
- Status: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/97ec033b-af57-4dad-9b6c-580bdd1b2971>
- Guide button: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/3562e315-bab4-4e29-a923-8d3e5b984ce1>
- **Direct Motor Command:** <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/ee8c5b28-e8da-4cc4-bb48-17781b8371af>
- Gamepad Input Report: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/ff8092de-b56e-4bbc-8e6e-71957afa9172>
- Share button: <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/4a57f35e-8e41-4454-aa03-56ca79b30a5e>
- **Security, PC opt-out:** <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/a9992580-283a-4aeb-94ed-dd9d7c9e7359>
- **Appendix B, authentication removed on 21H2+:** <https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-gipusb/bbdfaabf-e873-4b6b-a6ab-d4a1676f302f>

Other Microsoft documentation:

- GameInput device types and wire protocols: <https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/input/hardware/input-hardware-interfaces>
- API pages listed at the end of section 1.

Local, read-only: `C:\Windows\INF\dc1-controller.inf`, `xboxgip.inf`, `xboxgipsynthetic.inf`, `xinputhid.inf` (Windows 11 build 26200, driver version 10.0.26100.9818).

Community pages are listed at the end of section 1. Related earlier study in this repo: `docs/research/xbox-emulation.md`.
