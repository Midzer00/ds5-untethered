# DSX mod compatibility

Many PC game mods were written for the third-party app "DSX" (DualSenseX, by Paliverse). They send adaptive-trigger,
light-bar, player-LED and mic-LED instructions to a local app over UDP. DS5 Untethered can receive those messages
(Settings, "DSX Mod Support", off by default) so such mods work with a Bluetooth DualSense through our bridge.

"DSX" is used here only to say which mods are compatible. DS5 Untethered is not affiliated with DSX or Paliverse.

## How this spec was made (clean room)

Only prose documentation was used: README text, documentation pages, issue and pull-request descriptions, the DSX
developer page and Steam discussions. No source code was opened: no DSX code, no mod source, no example clients, no
generator gists. Where a detail exists only in source code it is listed under "Unknown" below and the implementation
logs what it does not understand so it can be filled in later.

Sources (each fact below names the one it comes from):

- [S1] DSX developer page (Paliverse), text-file method and trigger parameter ranges: https://dualsensex.com/game-devs/
- [S2] DualSenseX GitHub README (Paliverse), UDP over localhost, mod list: https://github.com/Paliverse/DualSenseX
- [S3] PadForge documentation, "Game Mods Internals" (a third-party DSX-compatible server, prose reference):
  https://padforge.org/docs/reference/game-mods-internals/
- [S4] PadForge documentation, "Game Mods" (user guide, mod list, behaviour): https://padforge.org/docs/features/game-mods/
- [S5] PadForge issue #529, "DSX game mods: adaptive triggers, lights and trigger thresholds over UDP" (design text):
  https://github.com/hifihedgehog/PadForge/issues/529
- [S6] DS4Windows fork PR #86 description, "DSX UDP mod protocol compatibility server (port 6969)":
  https://github.com/hbashton/DS4Windows/pull/86
- [S7] DS4Windows fork documentation, DSX game mod support: https://github.com/hbashton/DS4Windows/blob/master/docs/DSX-game-mod-support.md
- [S8] DSZ README (a third-party DSX-compatible app): https://github.com/MaximumFreak/dsz
- [S9] Steam discussion on the DSX UDP port (developer reply: allow the port in the Windows firewall):
  https://steamcommunity.com/app/1812620/discussions/0/3497635791229036362
- [S10] Steam discussion (Cactus Cowboy) on the port-number file and the 6969 fallback:
  https://steamcommunity.com/app/2554800/discussions/0/4632610189255715186
- [S11] PadForge documentation, "Adaptive Triggers" (prose meaning of slope / multi-position effects):
  https://padforge.org/docs/features/adaptive-triggers/
- [S12] Controlify documentation, adaptive trigger effects (prose parameter ranges of the standard effects):
  https://moddedmc.wiki/en/project/controlify/latest/docs/resource-packs/adaptive-trigger-effects.md

## Transport

- UDP over localhost [S2]. Default port 6969 [S3][S6][S8][S9]. Compatible servers bind 127.0.0.1 only [S3][S6][S7].
- Mods may read the port from a file:
  - DSX v2 era mods: `C:\Temp\DualSenseX\DualSenseX_PortNumber.txt`; some stop when it is missing [S3][S5][S10].
  - DSX v3.1 era mods: `%LOCALAPPDATA%\DSX\DSX_UDP_PortNumber.txt`; they fall back to 6969 when it is missing [S3][S4].
  - DSX itself rewrites these files on start [S5].
- One app per port: when DSX runs, the port is taken [S4][S7].
- Some mods (DualSense4GTAV) also check for a running process named "DSX" [S4][S5]. We do not imitate that.

## Packet

One JSON object per datagram: `{"instructions":[{"type":<type>,"parameters":[...]}, ...]}` [S3][S5].

- `type` is a number or a name; keys are matched case-insensitively by compatible servers [S3][S5].
- `parameters` may be null or missing (empty) [S3][S5]; values may be numbers, numeric strings, booleans (player LEDs)
  or null (read as 0) [S3][S5].
- The v2 example sends a fixed-size array padded with `{"type":0,"parameters":null}` entries [S3][S5].
- The first parameter of every instruction is the controller index [S3][S5]. Mods send 0 in every case reviewed
  [S4][S5]; a negative index means all controllers in some servers [S3][S5].
- Instructions apply in order; unknown ones are ignored [S5].

## Instruction types

| Type | Name | Parameters after the index | Source |
|---|---|---|---|
| 0 | GetDSXStatus | none | [S3][S5][S8] |
| 1 | TriggerUpdate | side (1 = left, 2 = right), mode, mode parameters | [S3] |
| 2 | RGBUpdate | red, green, blue; v3 adds brightness 0-255 (absent = full) | [S3][S5][S7] |
| 3 | PlayerLED | five on/off values, left to right (bits 0-4) | [S3][S5] |
| 4 | TriggerThreshold | side, point 0-255 (below the point the trigger reads as released) | [S3][S5] |
| 5 | MicLED | 0 = on, 1 = pulse, 2 = off | [S3][S5] |
| 6 | PlayerLEDNewRevision | 0-4 = player 1-5 patterns (0x04, 0x0A, 0x15, 0x1B, 0x1F), 5 = all off | [S3][S5] |
| 7 | ResetToUserSettings | none: ends the mod's effects, the user's settings come back | [S3][S5][S7] |

## Trigger modes (TriggerUpdate)

Numbers from [S3][S5]; parameter order and ranges for the classic modes from the DSX developer page [S1]; the
v3 names follow the standard DualSense effect names and their parameter order [S8][S11][S12].

| Mode | Name | Parameters | Our mapping |
|---|---|---|---|
| 0 | Normal | none | off |
| 1 | GameCube | none (preset) | approximation (see Unknown) |
| 2-6 | VerySoft, Soft, Hard, VeryHard, Hardest | none (presets) | approximation: resistance from the start, rising strength |
| 7 | Rigid | none (preset) | approximation: full resistance |
| 8 | VibrateTrigger | frequency 0-255 (absent = 10) [S3][S5] | vibration over the whole pull, full amplitude |
| 9 | Choppy | none (preset) | approximation: alternating resistance zones |
| 10 | Medium | none (preset) | approximation: medium resistance |
| 11 | VibrateTriggerPulse | none (preset) | approximation: pulsed vibration zones |
| 12 | CustomTriggerValue | custom mode, then up to 7 values 0-255 [S1] | raw effect: mode byte per custom mode, values 1-6 to bytes 1-6, value 7 to byte 9 [S3][S5] |
| 13 | Resistance | start 0-9, force 0-8 [S1] | feedback |
| 14 | Bow | start 0-8, end 0-8, force 0-8, snap force 0-8 [S1] | bow (0x22) |
| 15 | Galloping | start 0-8, end 0-9, first foot 0-6, second foot 0-7, frequency 0-255 [S1] | galloping (0x23) |
| 16 | SemiAutomaticGun | start 0-7 (2-7 effective), end 0-8, force 0-8 [S1][S12] | weapon |
| 17 | AutomaticGun | start 0-9, strength 0-8, frequency 0-255 [S1] | vibration |
| 18 | Machine | start 0-8, end 0-9, strength A 0-7, strength B 0-7, frequency 0-255, period 0-2 (decimal) [S1] | machine (0x27) |
| 19 | VIBRATE_TRIGGER_10Hz | none | vibration at 10 Hz [S3][S5] |
| 20 | OFF | none | off |
| 21 | FEEDBACK | as Resistance | feedback |
| 22 | WEAPON | as SemiAutomaticGun | weapon |
| 23 | VIBRATION | as AutomaticGun | vibration |
| 24 | SLOPE_FEEDBACK | start, end, start strength, end strength (1-8) [S11][S12] | feedback zones ramping from start to end, holding the end strength after it [S11] |
| 25 | MULTIPLE_POSITION_FEEDBACK | ten strengths 0-8, one per zone [S12] | feedback zones |
| 26 | MULTIPLE_POSITION_VIBRATION | frequency, then ten amplitudes 0-8 [S3][S12] | vibration zones |

Custom modes (CustomTriggerValue's first value) [S1][S3][S5]: 0 Off (0x00), 1 Rigid (0x01), 2 RigidA (0x21),
3 RigidB (0x05), 4 RigidAB (0x25), 5 Pulse (0x02), 6 PulseA (0x22), 7 PulseB (0x06), 8 PulseAB (0x26),
9 VibrateResistance (0x06), 10 VibrateResistanceA (0x26), 11 VibrateResistanceB (0x06), 12 VibrateResistanceAB (0x26),
13-16 VibratePulse, A, B, AB (0x27). The numbers 0-8 are inferred from the list order [S5].

Out-of-range or missing parameters change nothing: the trigger keeps its previous effect [S3]. A zero strength turns
the trigger off [S5].

## Reply

A reply goes to the sender's address for every packet (not only GetDSXStatus), before the packet is applied, so mods
that wait on a blocking receive always get an answer [S3][S5]. Fields (names and casing as DSX) [S3][S5]:

- `Status`: "DSX Received UDP Instructions"
- `TimeReceived`
- `isControllerConnected`
- `BatteryLevel`
- `Devices` (v3): one entry per controller, with `Index`, `MacAddress`, `DeviceType` (0 DualSense, 1 DualSense Edge,
  2 DualShock 4 v1, 3 DualShock 4 v2, 4 adapter), `ConnectionType` (0 USB, 1 Bluetooth, 2 adapter), `BatteryLevel`,
  `IsSupportAT`, `IsSupportLightBar`, `IsSupportPlayerLED`, `IsSupportMicLED`.

## Versions

| | v2 (GitHub DualSenseX) | v3 / v3.1 (Steam DSX) |
|---|---|---|
| Port file | `C:\Temp\DualSenseX\DualSenseX_PortNumber.txt` | `%LOCALAPPDATA%\DSX\DSX_UDP_PortNumber.txt`, fallback 6969 |
| RGBUpdate | no brightness | brightness as the 4th value |
| Reply | no `Devices` | `Devices` list |
| Instructions | fixed-size array with null padding | variable list |

[S3][S5]. The GitHub v1.4.9 is no longer supported; development moved to the Steam DSX [S2].

## End of a mod's effects

DSX keeps a mod's effects until the mod sends ResetToUserSettings; closing a mod sends nothing [S7]. Compatible servers
end them on reset, when the sending program exits, on a manual clear, or when the feature is turned off [S4][S7].

## Mods that use it

From [S2][S4][S5][S6][S8]: DualSense4GTA5 / DualSenseV (GTA V), GTASense (GTA III, Vice City, San Andreas),
DualSense4Rockstar (GTA IV, GTA V, RDR2), DSXSkyrim-NG and DSXFallout4, TarkovDSX, ForzaDSX / ForzaDSXlegacy (Forza
Horizon 4/5, Motorsport 7), WF2_DSX (Wreckfest 2), RBR_Adaptive_Trigger (Richard Burns Rally), Race-Element and
RacingDSX (sim racing), DualSenseXLink (Assetto Corsa), a Cyberpunk 2077 DualSense mod, Hogwarts Legacy, DSXpp.

## Unknown (only in source code or never published)

- The exact trigger bytes DSX sends for the presets GameCube, VerySoft, Soft, Medium, Hard, VeryHard, Hardest, Rigid,
  Choppy and VibrateTriggerPulse: never published [S3][S5]. We send our own approximations (as DSZ does [S8]).
- The bit order of Galloping's two foot values and Machine's period scale inside the effect bytes. We put the first
  foot in bits 0-2 and the second in bits 3-5, and the period in tenths; to verify on hardware.
- Where DSX puts the 7th CustomTriggerValue value (byte 9 or byte 7; servers disagree [S3][S5]). We use byte 9.
- The "v3.1 helper quirk" for custom values (values shifted by one, trailing null) [S3]: the exact shape is not
  described in prose. Not handled.
- The JSON type and format of `TimeReceived`. We send local time as text.
- What DSX does with a controller index past the last controller. We ignore it.
- Instruction types beyond 7 (DSZ adds `HapticPulse` and `Rumble` as its own extensions [S8]): not DSX. Unknown types
  are logged once each with a short sample of their JSON.

## What DS5 Untethered does (summary)

See `src/Kopru.Core/Mods/` and the "DSX mod effects" notes in `ControllerBridgeMods.cs`:

- Listens on 127.0.0.1:6969 only while the setting is on; a taken port is logged and shown in Settings, never fatal.
- The protocol has no authentication: any program of any user on this PC can send these commands (set trigger effects,
  lights, LEDs). That is why the setting is off by default.
- Does not write the port files and does not imitate the DSX process (the defaults already match 6969).
- Replies to every packet with the fields above.
- A mod's trigger effect wins over the game's, the fallback trigger and nothing (scaled by the user's trigger strength);
  the game's own effects come back when the mod's end. A game that sends its own effects while a mod's are active is
  logged once. Its light bar, player LEDs and mic LED are written over the game's and the user's light (the user turned
  the feature on); the app's own mic-mute LED still wins. Its trigger threshold zeroes the trigger below the point.
- Listens on IPv4 loopback only; a mod that sends to "localhost" resolved as IPv6 (::1) would not reach it (not seen
  in any documentation; to confirm with real mods).
- The mod's effects end on ResetToUserSettings, when the sending socket is closed (the mod exited), after 5 minutes
  without a message (60 s when the mod's messages came from more than one socket, so its exit cannot be seen), when the
  setting is turned off, or when the app exits. Writes to a controller are coalesced to one per 4 ms (the newest wins).
- In Low Power (or with the user's light brightness at 0) the mod's light bar and player LEDs are not shown.
