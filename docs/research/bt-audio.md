# DualSense speaker / headset audio over Bluetooth (research, 2026-11-09)

Status: **experimental, nothing here is verified on our hardware yet.** Prototype: `src/Kopru.Core/Audio/`
(`SpeakerReports`, `AudioOutput`, `StereoResampler`, `SpeakerAudioSender`, `ConcentusOpusEncoder`, optional
`NativeOpusEncoder`), wired into `BluetoothSession.Speaker` (null = off) and into the bridge
(`ControllerBridge.SpeakerEnabled`, `BridgeHost.ApplySpeaker`, Settings → Controller Speaker; off by default). Clean-room: only public write-ups (READMEs, PR/issue prose,
release notes) and the Linux kernel driver documentation were read; no other project's code.

## What is known

| Fact | Confidence | Source |
|---|---|---|
| BT audio is not PCM: it is compressed, non-standard; Linux has no BT audio support | high | kernel maintainer review of the jack-detect series [1], `hid-playstation.c` comments [2] |
| The codec is **Opus** (not SBC): 48 kHz, stereo, CBR, 10 ms frames (480 samples) | high (three independent projects agree) | [3][4][5][6] |
| Opus frame size 200 B (160 kbit/s CBR) in the single-frame report | medium (one source; another uses 80 B frames, mono input) | [4] vs [6] |
| Speaker audio goes in output report **0x36**, 398 B, CRC-32 (seed 0xA2, like 0x31/0x32) over the first 394 B | medium | [4][6] |
| 0x36 carries sub-packets: 0x11 stream control, 0x12 haptics (64 B, same as 0x32), **0x13 speaker** Opus; **0x16** instead of 0x13 sends to the headphone jack (true stereo); a 0x10 "state" sub-packet is also mentioned | medium | [4] (0x15/0x16 numbering in [7]) |
| One 0x36 per 10.667 ms (the 0x32 clock), i.e. 480 samples per 512/48000 s: the game's 48 kHz audio is resampled 512 -> 480 (one project drops 1 frame in 16 instead) | medium | [4][6] |
| Haptics and speaker share report 0x36 and one clock; without speaker audio the stream is plain 0x32 | medium | [6][3] |
| Report **0x39** (547 B, every 21.333 ms) = two haptics blocks + two Opus frames; DS5Dongle's default | medium | [3][5] |
| Stream-control byte 4 = 0xFE keeps the mic off; 0xFF enables mic Opus in input 0x31 (marker 0xD4, 48 kHz mono) | low-medium | [4] |
| Output block audio fields (offsets in the 47-byte block): 4 headphone volume (0..0x7F), 5 speaker volume (0..0xFF, reacts ~0x3D..0x64), 6 mic volume (0..0x40), 7 audio_control (bits 4-5 path: 0 = L+R to jack, 3 = right channel to speaker), 37 audio_control2 (bits 0-2 speaker preamp, 2 ~ +6 dB) | high (USB), assumed same on BT | [1][2] |
| Valid flags: flag0 0x20 speaker volume, 0x40 mic volume, 0x80 audio control; flag1 0x80 audio control2; flag0 0x10 headphone volume (not named by the kernel; inferred) | high / 0x10 low | [2] |
| Speaker plays only after an audio-control 0x31 (flag0 0x80, path 0x30); volume enable alone (0x20) also reported | medium | [5][6] |
| Headset detect: input body byte 53 (`status[1]`) bit 0 headphones, bit 1 mic, bit 2 mic-mute; same body on BT 0x31 | high (USB), likely BT | [2] |

## What is uncertain

1. Exact byte layout of 0x36 after the haptics sub-packet. **Our hypothesis** (in `SpeakerReports`): bytes 0..76
   identical to 0x32 (`0x91 07 FE 00 00 00 00 depth counter`, `0x92 40` + 64 haptics bytes), then `0x93 C8` + 200 B Opus
   at 79..278, zeros to 393, CRC at 394. Whether the 0x10 state sub-packet is required is unknown.
2. Whether bit 7 in the sub-packet id (0x91/0x92 in our working 0x32) applies to 0x13/0x16 too (we send 0x93/0x96).
3. Whether 0x36 is defined in Windows' BT HID descriptor (writes need the full 547 B; 0x32 worked, so likely yes).
4. 200-B vs 80-B Opus frames; CELT-only (restricted low-delay) vs restricted-CELT application mode.
5. Volume curve; whether the headphone jack needs its own enable bit (0x10) over BT.
6. One source reports Linux hid-playstation's audio-control writes halving the speaker level when noise-cancel bits are
   not kept [3]; we keep the rest of `audio_control` untouched.

## Recommended approach (prototyped)

- Keep 0x32 as is. When `BluetoothSession.Speaker` is set and has audio, each 10.67 ms tick sends 0x36 = the same
  haptics sub-packets + one 200-B Opus frame (0x13 speaker / 0x16 jack). The counter and sequence run on across both
  report types, so haptics never stop. 0x39 (double-frame) is a later optimisation.
- Audio: virtual pad's ch1-2 -> mono (L+R)/2 on the speaker, stereo on headphones -> linear resample 48 k -> 45 k ->
  480-sample frames (buffer max ~64 ms, oldest dropped) -> Opus 48 kHz stereo CBR 160 kbit/s, padded to exactly 200 B.
- Route/volume: 0x31 with audio_control path (0x30 speaker / 0x00 jack), preamp 2, speaker volume mapped 0x3D..0x64,
  headphone volume 0..0x7F; switched automatically when input bit body[53].0 changes.
- Encoder (decided 2026-11-09): **Concentus 2.2.2** (managed C# Opus, BSD-3-Clause, NuGet; listed in
  THIRD-PARTY-NOTICES.md). `ConcentusOpusEncoder`: 48 kHz stereo, 10 ms frames (480 samples/ch), CBR at
  `SpeakerReports.OpusFrameBytes` x 8 x 100 = 160 kbit/s, restricted low-delay (CELT only), complexity 5, no DTX/FEC,
  signal type music. Built with the encoder constructor, not `OpusCodecFactory`, so it never switches to a native
  library on its own. Measured on the dev machine: ~0.33 ms per 10 ms frame (10 s of audio in ~333 ms); a 1 kHz sine
  round-trips through the Concentus decoder at ~39 dB SNR.
- Frame size: CBR fills the 200-B slot. If a packet ever comes out shorter, `OpusPadding` pads it the RFC 6716
  section 3.2.5 way (what libopus `opus_packet_pad` does): the code-0 packet becomes code 3 with one frame and the
  padding flag, padding length bytes (255 = 254 bytes + another length byte), then zero padding after the frame data.
  Decoders ignore it (tested: padded and unpadded packets decode to identical samples).
- libopus (`opus.dll`) stays an optional fallback, not shipped: used only when `KOPRU_OPUS=native` is set and opus.dll
  loads (`OpusEncoders.Create`).
- Bridge: with the setting on, every chunk of the game's 4-channel audio still feeds the haptics converter (ch3-4) as
  before; chunks with sound on ch1-2 (|sample| > 32) plus up to 200 ms of following silence go to
  `BluetoothSession.PushSpeakerAudio`. Longer silence is not sent, the ~64 ms speaker buffer runs dry and `TakeFrame`
  returns false, so the stream goes back to plain 0x32. The game's own output reports would overwrite the audio fields
  of the 0x31 block, so the speaker route/volume is written back into each of them while the speaker is on. The
  headphone bit is tracked always (`BluetoothSession.HeadphonesPlugged`); speaker and headphone volume are separate.

## Hardware test plan

No `opus.dll` needed (Concentus; `KOPRU_OPUS=native` plus a 64-bit opus.dll next to the program compares with libopus).
Controller on Bluetooth, not hidden by HidHide.

1. `Kopru.Probe speaker 5` with nothing plugged in. Expected: a steady 440 Hz tone from the pad speaker for 5 s, plus a
   short buzz in the grips every other second (haptics still alive). The probe prints the number of 0x36 reports
   (~470 for 5 s).
2. Same with wired headphones in the jack: the tone in both ears, speaker silent; the probe prints `Headphones plugged: True`.
3. Plug/unplug during a 20 s run: the tone should move between speaker and headphones within ~100 ms.
4. If silence: (a) check that haptics buzz -> report path is fine, the 0x36 layout is the suspect (try adding a 0x10
   sub-packet or dropping bit 7 from 0x93); (b) if nothing at all plays, the 0x36 write may be rejected - note any error.
5. If the tone is too high/low pitched or crackles: the 45 kHz resample ratio is wrong (try 48 kHz with 1-in-16 frame drop).
6. Afterwards, check that game haptics still work normally (no power cycle needed).

## Sources

1. LKML, review of "HID: playstation: Support DualSense audio jack hotplug detection" (2025-06):
   https://lkml.indiana.edu/hypermail/linux/kernel/2506.1/01932.html, cover letter https://lwn.net/Articles/1026850/
2. Linux `drivers/hid/hid-playstation.c` (struct `dualsense_output_report_common`, `DS_STATUS1_*`):
   https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c
3. DS5Dongle v0.8.0 release notes (MIT): https://newreleases.io/project/github/awalol/DS5Dongle/release/v0.8.0
4. LinuxAudio4Dualsense5 README (AGPL-3.0; README only): https://github.com/ebraminio/LinuxAudio4Dualsense5
5. KytyPS5 PR #148 description: https://github.com/chenxiao07/KytyPS5/pull/148
6. dualsense-bridge PR #121 description: https://github.com/matt400/dualsense-bridge/pull/121
7. dualsense-companion README: https://github.com/vingt-2/dualsense-companion
8. SAxense feasibility notes (0x32 haptics): https://drqp.readthedocs.io/en/latest/Dev/saxense-feasibility.html
9. SensePost, DualSense reverse engineering (2020): https://sensepost.com/blog/2020/dualsense-reverse-engineering/
