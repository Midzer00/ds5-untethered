# DualSense microphone over Bluetooth (research, 2026-11-10)

Status: **report layout and codec measured on our hardware** (controller fw 06.30, `Kopru.Probe mic`, 2026-11-10;
room noise only, nobody speaking). Turning the mic off is not verified yet. Clean-room: only READMEs, kernel driver
documentation / mailing-list prose, our own speaker research and our own probe dumps were used; no other project's code.

## Measured (2026-11-10)

Mic report, input report id 0x31, 78 bytes:

| Bytes | Content |
|---|---|
| 0 | 0x31 |
| 1 | high nibble: sequence (16 values, like gamepad input); **low nibble: type, 1 = gamepad input, 2 = mic** |
| 2 | counter, +1 per mic report (256 values, wraps) |
| 3..73 | **one Opus packet, 71 bytes, CBR, no length byte**; TOC byte [3] = 0xD4 in every report (CELT-only, 10 ms, stereo flag, one frame) |
| 74..77 | CRC-32 with the input seed: **valid**, so these reports pass the gamepad input check |

- Rates: ~100 mic reports/s (one 10 ms frame each) next to ~560-650 gamepad reports/s.
- Offline decode of both 10 s dumps with `ConcentusMicDecoder` (mono): 1037 / 1037 packets decoded, 480 samples each,
  10.37 s of audio, RMS about -27..-29 dBFS, 100 ms levels between -60 and -20 dBFS (room sound, not constant).
  Payload from byte 4 instead: 73-78 % of packets fail and the rest decode to clipping noise, so the boundary is byte 3.
- Gamepad reports: every one of ~12,500 had type 1 in byte 1, so the type nibble alone separates the two. Byte 3 is not
  used to recognise mic reports (only seen as 0xD4).
- **Control run surprise:** a run that never asked for the mic (no stream reports sent at all), started ~2 s after a
  run that had asked, got the same ~100 mic reports/s. The controller keeps sending once it was asked, at least until
  told otherwise or switched off. Our old code never told it: switching the mic off stopped the stream at once, so not a
  single report with the off value (0xFE) went out. Read as gamepad input, mic reports move the left stick (counter in
  byte 2 = left X, 0xD4 = left Y) and press random buttons: the "haywire" the user saw.

What the code does now: mic reports are never gamepad input (mic on or off); switching the mic off sends 24 stream
reports (~250 ms) with 0xFE before the stream may stop; mic reports arriving while the mic is off make the session send
that burst again, at most every 2 s.

Still unknown: whether 0xFE stops the controller (run `Kopru.Probe mic 12 stop` and watch the per-second count), whether
anything else (power cycle only?) stops it, and whether the stereo flag carries two real channels.

## What is known

| Fact | Confidence | Source |
|---|---|---|
| Over USB the mic is a normal UAC1 capture interface (our virtual pad: iso IN 0x82, 2 ch, 48 kHz, 16-bit, from the captured config) | high | own capture, [2] |
| Over Bluetooth there is no standard audio; the mic uses a custom protocol that Linux does not support | high | kernel driver author on LKML (2021) [3], review in [1] |
| The mic is turned on through the same stream-control sub-packet that carries the speaker/haptics stream: byte 4 = **0xFE** mic off (what we send in 0x32 today), **0xFF** mic on | low-medium (one README) | [4] |
| Mic audio comes back **under input report id 0x31** (the gamepad report id), marked by **0xD4**; codec **Opus**, **48 kHz mono, 10 ms frames** | low-medium (one README; consistent with the speaker side using Opus 48 kHz 10 ms, which three projects agree on) | [4], [5] |
| While the mic runs, 0xD4 reports mixed into 0x31 can be misread as gamepad input (phantom stick movement) unless filtered | medium | [4] |
| Mute button: body byte 9 (buttons[2]) bit 2; mic-mute status: body byte 53 bit 2; mute LED: output block byte 8 with flag1 bit 0; the kernel driver toggles mute itself on button press (USB) via the power-save "mic mute" bit | high | [2] |
| Mic volume: output block byte 6 (0..0x40), flag0 bit 6 | high (USB), assumed BT | [2] |

## What was unknown before the hardware test (items 1, 2 and 4 are answered above)

1. **Byte layout of a 0xD4 report**: where the marker sits (we assume byte 1, the place of the BT header/sequence
   byte), whether a length byte follows (we assume byte 2), where the Opus payload starts (byte 3) and whether the
   report keeps the 78-byte size and input CRC. Not public. `MicPackets` holds every offset in one place.
2. Opus frame size (bytes per packet) and whether one report carries one or several 10 ms frames.
3. Whether the mic needs anything besides 0xFF (e.g. mic volume / audio-control bits in 0x31, the 0x10 "state"
   sub-packet), and whether it works while the stream is plain 0x32 (no speaker audio).
4. Report rate of mic packets (100/s expected for 10 ms frames) and its impact on the input rate.

## What we built

- `Audio/MicPackets.cs` — hypothesised layout (`IsMicReport`, `TryGetOpus`, `Build` for tests), stream-control bytes,
  `ConcentusMicDecoder` (Concentus, 48 kHz mono, with packet-loss concealment).
- `Audio/MicPipeline.cs` — `MicJitterBuffer` (30 ms prefill, 120 ms cap, drop-oldest, re-prime after underrun),
  `MonoResampler` (for other rates if the hardware test shows one), `MicPipeline` (decode -> buffer -> gain 0..4 ->
  noise gate (-46 dBFS peak, 200 ms hold) -> mute -> mono copied to both channels of the virtual mic), level meter,
  `TestToneMicSource`, `WavWriter`.
- `Audio/MicMute.cs` — `MicMuteState` (toggle on press edge, consumes the button while the mic feature is on) and
  `CoreAudioMicMute` (IMMDeviceEnumerator -> default capture endpoint, communications role -> IAudioEndpointVolume.SetMute).
- Hooks: `BluetoothSession.MicStreamEnabled` (0xFF in every 0x32/0x36 + CRC, keeps the stream running),
  `MicReportReceived`/`RawReportReceived`, 0xD4 reports kept out of the input path; `VirtualDualSense.Mic` feeds the
  iso IN endpoint (silence when null, as before); `ControllerBridge.MicMute` / `KeepMuteLed`; `BridgeHostMic`
  (`ApplyMic(MicSettings)`, mute LED "follow mic", Windows mute); Settings -> Controller Microphone; probe `mic`.

Mute button decision (2026-11-11, the user's choice): the controller's mic button is the **microphone switch**, not a
mute. Each press (edge only, not while held, not again within 300 ms) turns the whole mic feature on or off through the
same setting as the Quick Settings tile and the Settings switch, so the Bluetooth mic stream (and its load) is gone while
it is off. The mute LED in "Follow Microphone" mode is **lit while the mic is on** (the opposite of the PS5 convention,
on purpose); an explicit On/Off LED choice is kept. The button is consumed whenever the bridge runs with the virtual
DualSense, mic on or off, so games never see it. Not switched: under Low Power (the button then goes to the game, as
before) and with the DualShock 4 / Xbox 360 pads (the button is passed on as before; those pads have no mic button).
"Also mute the Windows microphone": a press that turns the mic off mutes the default Windows capture device, one that
turns it on unmutes it; changing the switch in the app does not touch Windows. The old in-app mute state is gone.

## Hardware test plan

Controller on Bluetooth, not hidden by HidHide, app closed.

1. `Kopru.Probe mic 10`, speak into the controller. The probe prints the report kinds seen (`id XX b1 YY len N: count`)
   and writes `%TEMP%\kopru-mic-<time>.wav`, `.bin` (every non-gamepad report: u16 length + bytes) and `.txt` (kinds +
   first 40 unusual reports in hex).
2. Expected if the hypothesis holds: ~100 reports/s of `id 31 b1 D4`, `Mic reports` > 0, decoded ~= mic reports, and the
   WAV plays your voice.
3. If `id 31 b1 D4` shows up but `failed` is high or the WAV is noise: the payload offset/length is wrong. Use the hex
   dump: find where the varying bytes start/end; a valid Opus TOC byte for CELT-only 10 ms mono is typically 0xE0..0xF8.
   Fix only the constants in `MicPackets` (and `TryGetOpus`).
4. If nothing unusual appears: the 0xFF request alone is not enough. Retry with the speaker probe running at the same
   time (`Kopru.Probe speaker 10` in another window) to see whether 0x36 is required; note the result.
5. In the app: Settings -> Controller Microphone on, start the bridge, open Windows Sound settings -> Input -> the
   virtual DualSense: the level bar should move when you speak; the meter in our Settings page too.
6. Mute button: press it -> mute LED on, our meter drops to 0, Windows' input bar flat; press again -> back. With "Also
   Mute the Windows Microphone" on, the default Windows mic shows muted too. A game must not react to the button.
7. Report the probe's `.txt` and whether 2/5/6 worked.

## Sources

1. LKML, "HID: playstation: Support DualSense audio jack hotplug detection" review (2025-06):
   https://lkml.indiana.edu/hypermail/linux/kernel/2506.1/01932.html
2. Linux `drivers/hid/hid-playstation.c` (output report fields, `DS_STATUS1_*`, mute button/LED handling):
   https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c
3. LKML, DualSense driver review thread, Bluetooth audio "custom protocol" remark (2021):
   https://lkml.iu.edu/hypermail/linux/kernel/2102.2/01520.html
4. LinuxAudio4Dualsense5 README (README only): https://github.com/ebraminio/LinuxAudio4Dualsense5
5. Our speaker research: docs/research/bt-audio.md
