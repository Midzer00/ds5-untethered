# DS5 Untethered on Linux (experimental)

**Status: experimental and community-maintained.** The Windows app is the product; this
branch (`linux`) is where Linux support grows, one pull request at a time. Nothing here is released, and the desktop
app does not start on Linux yet. What exists today is a headless spike, `tools/Kopru.LinuxSpike`, that runs the same
bridge the Windows app runs, with a Linux platform layer underneath.

If you test it, thank you. The three questions below are what we need answered first.

## How the bridge works

The app makes a Bluetooth DualSense look like a wired one:

```
 game  --(USB HID + USB audio)-->  virtual USB DualSense  --(USB/IP over 127.0.0.1)-->  our USB/IP server
                                                                                         |
 real DualSense  <--(Bluetooth HID output reports: triggers, lights, haptics)-- BridgeHost / BluetoothSession
```

- Our own USB/IP server (in `Kopru.Core`) serves a virtual USB DualSense (also DualShock 4, Xbox 360 and Xbox Series
  types). The system's USB/IP client plugs it in: usbip-win2 on Windows, `usbip` + `vhci_hcd` on Linux.
- Games see a wired DualSense. Their trigger effects arrive as HID output reports; their haptics arrive as the pad's
  4-channel USB audio (channels 3 and 4), as isochronous OUT transfers, straight at our server.
- The bridge turns both into Bluetooth output reports for the real pad.

So **native game haptics do not need PipeWire**: the game's haptics audio reaches our server through the virtual USB
audio device. PipeWire is only needed later for "turn sound into haptics" (capturing a game's normal sound output).

## Architecture: what is shared, what is platform code

Shared and platform-neutral (in `src/Kopru.Core`): the USB/IP server (`UsbIp/`), the protocol (`Protocol/`), the
virtual pads (`Virtual/`), the bridge (`Bridge/`), haptics (`Haptics/`), lighting, profiles, localization, the audio DSP
and Opus code, and the app's view models.

Everything that touches the operating system is created by one platform object, `IPlatform`
(`src/Kopru.Core/Platform/IPlatform.cs`). The app picks it once at start (`PlatformSelection` in `Kopru.App`) and puts
it in `PlatformServices.Current`; `BridgeHost.Create(platform)` and `BridgeManager.Create(platform, primary)` take
everything from it.

| `IPlatform` member | Windows (`WindowsPlatform`) | Linux (`LinuxPlatform`) |
|---|---|---|
| `CreateControllerSource` | `WindowsControllerSource` (hid.dll, SetupAPI) | `HidrawControllerSource` - done, experimental |
| `CreateControllerFinder` | `HidControllerFinder` | `HidrawFinder` (sysfs) - done, experimental |
| `CreateUsbIpAttacher` | `UsbIpExeAttacher` (usbip-win2) | `LinuxUsbIpAttacher` (`usbip`, `vhci_hcd`) - done, experimental |
| `CreateHiding` | `HidHideHidingService` (HidHide) | `LinuxHiding` - **only logs**, see question (c) |
| `CreateInputSink` | `WindowsInputSink` (SendInput) | none yet (uinput later) |
| `CreateAudioCapture` | `WasapiCaptureFactory` | none yet (PipeWire later); audio-to-haptics is off |
| `ControllerPower` | Bluetooth radio ioctl | none yet (BlueZ `Device1.Disconnect` later) |
| `CreatePreciseWait` | high-resolution waitable timer | the portable fallback (sleep, then spin); timerfd later |
| `PrepareProcess` | power throttling opt-out | nothing |

Linux code goes in `src/Kopru.Platform.Linux` (plain `net8.0`, builds on any OS):

- `Hidraw/`: `HidrawUevent` and `HidId` (uevent and `HID_ID` parsing), `HidReportDescriptor` (report sizes from the
  descriptor), `HidrawFinder`, `HidrawLink` (read/write through `FileStream` on `/dev/hidrawN`, feature reports through
  the `HIDIOCGFEATURE`/`HIDIOCSFEATURE` ioctls), `HidrawControllerSource`.
- `UsbIp/`: `LinuxUsbIpAttacher` and the command lines in `UsbIpLinuxCommands`.
- `Hiding/`: `LinuxHiding`, with the known options written down as a TODO.
- `Diagnostics/`: what the spike measures (`IsoAudioMeter`, `OutputReportInfo`) and how it masks its log (`LogMask`).

Parsing is kept pure and unit-tested in `tests/Kopru.Core.Tests/Linux`; those tests run on every OS.

Still Windows-only in the desktop app (next seams, not needed for the spike): the tray and toasts, start with the system
(Run key), the foreground-window and full-screen checks, the setup wizard and health checks (registry, installed
drivers), the default audio device guard, the game detection by Windows exe paths, the updater (Inno Setup installer).

## Root rights

`usbip attach` writes to the `vhci_hcd` driver and needs root. The app itself never asks for root:

- `--attach manual` (the default for a normal user) prints the exact `sudo usbip ...` command for you to run in another
  terminal and waits until the virtual pad shows up. It waits 5 minutes; after that the bridge stops in the `Error`
  state (`state: Error (AttachFailed: ...)` in the log) and does not try again: press Ctrl+C and start the spike again.
- `--attach sudo` runs `sudo usbip ...` for you (sudo asks for your password).
- `--attach direct` runs `usbip` itself and needs the whole spike to run as root (`sudo ./Kopru.LinuxSpike`). This is
  the easiest way for a first test.

Later, root-only work (attach and detach, maybe hiding) should move into a tiny separate helper; it stays out of the app.

## Download a ready build

Every push to `linux` publishes a self-contained spike (no .NET needed, only the ICU library desktop distributions
already have) to the [`linux-spike` pre-release](https://github.com/Midzer00/ds5-untethered/releases/tag/linux-spike):

```sh
curl -LO https://github.com/Midzer00/ds5-untethered/releases/download/linux-spike/ds5-untethered-linux-spike-x64.tar.gz
tar -xzf ds5-untethered-linux-spike-x64.tar.gz
sudo modprobe vhci-hcd
./ds5-untethered-linux-spike/Kopru.LinuxSpike --list
sudo ./ds5-untethered-linux-spike/Kopru.LinuxSpike --log spike.log
```

Each CI run (pushes and pull requests) also keeps the same file as a build artifact for 30 days, on the run's page.

## Build and run the spike

You need the .NET 8 SDK to build (the published spike is self-contained and needs no .NET on the machine that runs it,
only the ICU library that desktop distributions install anyway, e.g. `icu` or `libicu`),
the `usbip` tools from your distribution (the package is usually called `usbip` or is part of `linux-tools`), and the
DualSense paired and connected over Bluetooth (BlueZ, e.g. `bluetoothctl`).

```sh
git clone https://github.com/Midzer00/ds5-untethered.git
cd ds5-untethered
git checkout linux
dotnet publish tools/Kopru.LinuxSpike -c Release -r linux-x64 --self-contained -o ~/ds5-spike

sudo modprobe vhci-hcd
~/ds5-spike/Kopru.LinuxSpike --list                 # system checks and the DualSense hidraw nodes
sudo ~/ds5-spike/Kopru.LinuxSpike --log spike.log   # run the bridge; Ctrl+C stops it and unplugs the pad
```

To run as a normal user instead, the Bluetooth DualSense's hidraw node must be readable and writable for you. An example
udev rule (for example `/etc/udev/rules.d/70-ds5-untethered.rules`; then `sudo udevadm control --reload` and reconnect
the controller):

```
# DualSense (054C:0CE6) and DualSense Edge (054C:0DF2) hidraw nodes, for the logged-in user
KERNEL=="hidraw*", KERNELS=="*054C:0CE6*", MODE="0660", TAG+="uaccess"
KERNEL=="hidraw*", KERNELS=="*054C:0DF2*", MODE="0660", TAG+="uaccess"
```

Then `~/ds5-spike/Kopru.LinuxSpike --attach manual --log spike.log` and run the `sudo usbip ...` line it prints.

Other options: `--seconds N` stops after N seconds; `--no-trim` sends output reports at the full length the shared code
builds (as Windows does) instead of cutting them to the length the report descriptor declares. Try it only if triggers
or haptics do nothing.

### Where the logs go

- **The spike log**: the console, and with `--log FILE` also FILE (appended to). Every line is masked: Bluetooth
  addresses, your user name and, under sudo, the name of the user who ran sudo (`SUDO_USER`), with the same helper the
  Windows app uses for its diagnostics zip. This is the file to send.
- **The bridge's own log**: `app.log` (rolled over to `app.1.log` at 512 KB) in the folder of the `--log` file, or in
  the current folder without `--log`. It is **not** masked; its lines also appear, masked, in the spike log as
  `bridge: ...`, so you never need to send it. The spike prints both paths at start (`logs: ...`).
- Nothing is written to `~/.local/share/Untethered` by the spike's logging. Under sudo the files belong to root.

### What the log shows

Every line is masked (see above).

- `system:`, `usbip:`, `hidraw:`, `sound:` - the checks at start: usbip client, `vhci_hcd`, the DualSense hidraw nodes,
  their bus, driver, report sizes and whether you can open them.
- `state:` - the bridge: `WaitingForController`, `Connecting`, `Running`, or `Error` with the reason.
- `virtual pad:` - served on 127.0.0.1, imported by the usbip client, and then seen as a new hidraw node and sound card.
- `game:` - output reports from a game: trigger effects (`R2 effect 0x..`), rumble, lightbar.
- `1s:` - once a second while anything arrives: audio transfers per second, milliseconds of audio per second (about
  1000 when steady), the longest gap, dropouts (gaps over 50 ms), game output reports, Bluetooth haptics reports per
  second (about 94 while haptics play), input rate.
- `audio: DROPOUT` - a gap in the audio transfers.
- `status:` - every 5 seconds: input rate, battery, whether the game sent haptics, triggers or rumble, controller writes
  and failures, Bluetooth link quality.
- `bridge:` - the shared bridge's own log lines.
- `total:` - at exit.

## The three questions

### (a) Is the virtual pad's USB audio through `vhci_hcd` stable?

1. Start the spike and wait for `state: Running` and `virtual pad: new sound card`.
2. Play 4-channel audio to the virtual pad without a game, for example (pick the card from `aplay -l`):
   `speaker-test -D hw:CARD=<card>,DEV=0 -c 4 -r 48000 -F S16_LE -t sine -l 0`.
   speaker-test plays one channel after the other; the haptics channels are 3 and 4, so you should feel a buzz while
   those two play. The audio stream itself runs the whole time.
3. Leave it for 10 minutes, then Ctrl+C.

We look at the `1s:` lines (about 1000 ms of audio per second, few or no dropouts) and the `total:` line.

### (b) Does a GE-Proton game with Steam Input off send haptics and triggers to the virtual pad?

1. With the spike running, start a game with native DualSense haptics and adaptive triggers through Steam with
   GE-Proton, with Steam Input turned off for that game.
2. Play a few minutes where the game uses triggers and haptics.

We look for `game: output report: R2 effect ...` lines (triggers), `1s:` lines with audio while you play and Bluetooth
haptics reports around 94 per second (haptics), and the `status:` lines. Please also tell us which pad the game used:
until hiding works, the game may pick the real Bluetooth pad instead of the virtual one.

### (c) How can the real pad be hidden without breaking BlueZ input?

Games should see only the virtual pad, while we keep reading the real one through hidraw and BlueZ keeps the Bluetooth
link. Options we know of (see the TODO in `src/Kopru.Platform.Linux/Hiding/LinuxHiding.cs`):

1. Unbind the kernel DualSense driver (`hid-playstation`) from the Bluetooth device. Does the hidraw node stay usable,
   and does BlueZ keep the link?
2. Keep the driver and grab its evdev nodes (`EVIOCGRAB`) from our process. Games that read hidraw directly still see it.
3. A udev rule that leaves the Bluetooth pad's hidraw node to us only (games run as the same user, so plain modes alone
   are not enough).
4. Per game: SDL or Steam Input settings that ignore the Bluetooth pad.

Anything you try and what happened (games, Steam, `evtest`, `ls -l /dev/hidraw* /dev/input/by-id`) helps. Undo any
change afterwards (reconnecting the controller usually rebinds the driver).

### What to send

Attach to issue #1 (or a new issue):

- the spike log (`--log spike.log`; masked already, but have a look before posting),
- `usbip port`, `lsmod | grep vhci`, `aplay -l`,
- `dmesg | tail -n 80` right after the virtual pad was plugged in. This one is **not** masked: remove Bluetooth
  addresses (`XX:XX:XX:XX:XX:XX`) and your user name before posting,
- your distribution, kernel, desktop, Bluetooth adapter and the game and Proton version.

## Known limits

- Experimental: expect the first runs to fail; the log is what we need.
- No hiding: games may see the real pad and the virtual one.
- The desktop app does not start on Linux; only the spike runs. No tray, notifications or autostart yet
  (StatusNotifierItem and XDG autostart are the plan).
- Audio-to-haptics (capturing a game's normal sound) is off: no PipeWire capture yet. Native game haptics do not need it.
- No desktop mode or gyro mouse (no uinput sink), no turning the controller off (no BlueZ call), no controller speaker
  or microphone tests.
- `usbip attach` needs root; the app never escalates by itself.
- Only one controller (slot 1) is used by the spike.
- Output reports are cut to the length the report descriptor declares; whether the pad over Linux Bluetooth wants that
  or the full length is unknown (`--no-trim`).
- The ioctl numbers follow the generic Linux layout (x86-64, arm64 and most others), not powerpc or mips.
- On Linux, CI skips the Core tests marked `[Trait("Category", "Windows")]` (Windows APIs or Windows paths) and the
  timer pacing tests (`RealTime`).

Contributions: see [CONTRIBUTING.md](../../CONTRIBUTING.md). Pull requests for Linux go to the `linux` branch.
