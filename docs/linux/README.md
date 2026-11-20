# DS5 Untethered for Linux (experimental)

**Status: experimental and community-maintained.** This branch (`linux`) is the Linux app. It is built and released
separately from the Windows app, which lives on `main`; the two are never merged, and the Windows-only code (the Windows
platform, HidHide, usbip-win2, WASAPI, the updater and installer) is not on this branch. Shared parts (the bridge, the
virtual pads, the USB/IP server, haptics, lighting, profiles, localization, the games catalog and the view models) keep
the same files and names as on `main`, so fixes can be carried over by hand.

If you try it, thank you. Logs and reports of what worked and what did not are what moves it forward (see
[What to send](#what-to-send)).

## What works and what does not yet

Works (experimental):

- The desktop app opens on X11, and on Wayland through XWayland, with all its pages: Overview, Games, Haptics And
  Triggers, Controller, Light and Settings, in English and Turkish.
- Settings and profiles are saved in `$XDG_CONFIG_HOME/ds5-untethered` (by default `~/.config/ds5-untethered`); the log
  is in `$XDG_STATE_HOME/ds5-untethered/logs` (by default `~/.local/state/ds5-untethered/logs`).
- Starting and stopping the virtual controller from the Overview, Settings or the tray. Plugging it in runs
  `pkexec usbip ...`, so polkit asks for your password in a dialog; unplugging normally asks nothing (see
  [Root rights](#root-rights-pkexec-and-polkit)).
- The virtual DualSense, with the game's haptics (as USB audio) and adaptive triggers. DualShock 4 and Xbox 360 are
  selectable too, through the same USB/IP path.
- The setup page checks the system and says how to fix what is missing: the `vhci_hcd` module, the `usbip` and `pkexec`
  programs, and whether your user can open the DualSense's hidraw node ("Copy Commands" copies the fix).
- One copy at a time: starting the app again (from the menu) shows the running copy's window.
- A tray icon where the desktop shows StatusNotifierItem icons (KDE Plasma, most others; GNOME needs the AppIndicator
  extension). Without one the app still works: closing the window keeps it running, and starting it again from the
  menu brings the window back. Turn off "Keep Running In Background" to make closing the window quit the app.
- "Start When I Sign In": an XDG autostart entry in `~/.config/autostart/ds5-untethered.desktop`.
- Turning the controller off ("Power Off", and after the idle time) through `bluetoothctl disconnect`.
- Game detection for per-game controller types and profiles: the running Steam game (native or Proton), found by the
  `SteamAppId` / `STEAM_COMPAT_APP_ID` that Steam sets in the game's environment. There is no "window in front" on Linux,
  so the newest running Steam game counts as the game being played. `/proc` is scanned at most every 3 seconds, only
  your own processes are looked at, and each process is read once.
- Games added by hand: any program file (Linux programs have no extension), a script or desktop entry, or a Windows exe.

Not there yet:

- **Hiding the real controller.** Games may see the Bluetooth DualSense next to the virtual one. The setup page and the
  Overview say so. See [question (c)](#c-how-can-the-real-pad-be-hidden-without-breaking-bluez-input).
- Sound to haptics (capturing a game's normal sound), the system sound device pickers and the default-device guard:
  hidden on Linux (they need a PipeWire capture). Native game haptics do not need them.
- Mouse Mode and gyro mouse (no uinput sink yet): the Mouse tab and the tray item are hidden.
- The Xbox Series pad type (it needs a Windows driver): hidden.
- In-app updates: none. Download a new build instead.
- Notifications are the app's own small windows; on Wayland (XWayland) they may not stay on top.
- The Steam Input check (the Steam Input chip shows "not checked").

## Install and run

You need:

- the `usbip` tools from your distribution (Debian, Fedora, Arch: `usbip`; Ubuntu: `linux-tools-generic`),
- the `vhci_hcd` kernel module loaded,
- polkit's `pkexec` (installed on almost every desktop),
- the DualSense paired over Bluetooth (your desktop's Bluetooth settings, or `bluetoothctl`).

Download the build from the [`linux-spike` pre-release](https://github.com/Midzer00/ds5-untethered/releases/tag/linux-spike)
(no .NET needed, only the ICU library desktop distributions already have):

```sh
curl -LO https://github.com/Midzer00/ds5-untethered/releases/download/linux-spike/ds5-untethered-linux-app-x64.tar.gz
tar -xzf ds5-untethered-linux-app-x64.tar.gz
cd ds5-untethered-linux-app
./install.sh        # optional: adds "DS5 Untethered" to your application menu (your user only, no root)
./Kopru.App         # or start it from the menu
```

`./install.sh --remove` takes the menu entry out again. The folder holds the app, `ds5-untethered.desktop`, the icon
(`ds5-untethered.png`) and the optional polkit rule (`50-ds5-untethered.rules`, commented out, see below).

### One-time system setup

```sh
# Load the USB/IP client's virtual host controller now, and at every boot:
sudo modprobe vhci-hcd
echo vhci-hcd | sudo tee /etc/modules-load.d/vhci-hcd.conf
```

On Ubuntu the module may be in a separate package: if `modprobe` says it is not found, install
`linux-modules-extra-$(uname -r)` first.

The app opens and reads the Bluetooth DualSense through its hidraw node, which must be readable and writable for you. If
the setup page says it is not, add this udev rule (for example `/etc/udev/rules.d/70-ds5-untethered.rules`), run
`sudo udevadm control --reload` and `sudo udevadm trigger`, then reconnect the controller:

```
# DualSense (054C:0CE6) and DualSense Edge (054C:0DF2) hidraw nodes, for the logged-in user
KERNEL=="hidraw*", KERNELS=="*054C:0CE6*", MODE="0660", TAG+="uaccess"
KERNEL=="hidraw*", KERNELS=="*054C:0DF2*", MODE="0660", TAG+="uaccess"
```

### Build it yourself

```sh
git clone https://github.com/Midzer00/ds5-untethered.git
cd ds5-untethered
git checkout linux
dotnet publish src/Kopru.App -c Release -r linux-x64 --self-contained -o ~/ds5-untethered
cp packaging/linux/* ~/ds5-untethered/ && cp src/Kopru.App/Assets/icon-256.png ~/ds5-untethered/ds5-untethered.png
~/ds5-untethered/install.sh
```

You need the .NET 8 SDK to build.

## Root rights: pkexec and polkit

`usbip attach` writes to the `vhci_hcd` driver and needs root. The app itself never runs as root: to plug the virtual
controller in it runs `pkexec /usr/bin/usbip --tcp-port N attach -r 127.0.0.1 -b 1-1` (programs are only taken from
`/usr/sbin`, `/usr/bin`, `/sbin` and `/bin`, never from your `PATH`), and polkit asks for your password in a dialog.
Closing the dialog stops the start; the Overview says so ("The password prompt was closed").

Unplugging asks for nothing: the app closes its USB/IP server's connection, and `vhci_hcd` then releases the port. Only
if the port is still held by our closed connection a few seconds later does the app run `pkexec usbip detach -p <port>`
(and you are asked once more). That `vhci_hcd` always releases the port by itself is still to be confirmed on hardware.

### The optional polkit rule (off by default)

The download (and `packaging/linux/` in the source) has `50-ds5-untethered.rules`. **Every line of it is commented out**:
it does nothing until you edit it. Uncommented, it lets an active, local user in the `sudo` or `wheel` group run exactly
the two commands above (`attach` to 127.0.0.1 with bus id `1-1` and a numeric TCP port, `detach` with a numeric port)
through pkexec without a password, matched with an anchored pattern on polkit's `command_line` detail.

**Warning:** even narrowed like this, any program you run can use it. Any local program can serve its own USB device on
127.0.0.1 and have it plugged in as root without asking, for example a fake keyboard that types commands. Enable it only
if you accept that; without it the app asks for your password instead. That the `command_line` match works on your
distribution is not yet confirmed on a real system: after enabling it, start the virtual controller once; if a password
is still asked, the rule did not match and nothing changed.

```sh
# after removing the leading "// " from the rule's lines:
sudo cp 50-ds5-untethered.rules /etc/polkit-1/rules.d/
# to remove it again:
sudo rm /etc/polkit-1/rules.d/50-ds5-untethered.rules
```

## How the bridge works

The app makes a Bluetooth DualSense look like a wired one:

```
 game  --(USB HID + USB audio)-->  virtual USB DualSense  --(USB/IP over 127.0.0.1)-->  our USB/IP server
                                                                                         |
 real DualSense  <--(Bluetooth HID output reports: triggers, lights, haptics)-- BridgeHost / BluetoothSession
```

- Our own USB/IP server (in `Kopru.Core`) serves a virtual USB DualSense (also DualShock 4 and Xbox 360 types). The
  system's USB/IP client (`usbip` + `vhci_hcd`) plugs it in.
- Games see a wired DualSense. Their trigger effects arrive as HID output reports; their haptics arrive as the pad's
  4-channel USB audio (channels 3 and 4), as isochronous OUT transfers, straight at our server.
- The bridge turns both into Bluetooth output reports for the real pad.

So **native game haptics do not need PipeWire**: the game's haptics audio reaches our server through the virtual USB
audio device. PipeWire is only needed later for "turn sound into haptics".

## Architecture

Shared (in `src/Kopru.Core`, same files as on `main`): the USB/IP server (`UsbIp/`), the protocol (`Protocol/`), the
virtual pads (`Virtual/`), the bridge (`Bridge/`), haptics, lighting, profiles, localization, the games catalog and the
audio DSP and Opus code. The app (`src/Kopru.App`) holds the Avalonia views and view models.

Everything that touches the operating system comes from one platform object, `IPlatform`
(`src/Kopru.Core/Platform/IPlatform.cs`), implemented by `LinuxPlatform` in `src/Kopru.Platform.Linux`:

| `IPlatform` member | Linux (`LinuxPlatform`) |
|---|---|
| `CreateControllerSource` | `HidrawControllerSource` (hidraw) |
| `CreateControllerFinder` | `HidrawFinder` (sysfs) |
| `CreateUsbIpAttacher` | `LinuxUsbIpAttacher` (`usbip`, `vhci_hcd`; the app uses pkexec) |
| `CreateHiding` | `LinuxHiding` - **only logs**, see question (c) |
| `CreateInputSink` | none yet (uinput later) |
| `CreateAudioCapture` | none yet (PipeWire later) |
| `ControllerPower` | `BluezControllerPower` (`bluetoothctl disconnect`) |
| `CreatePreciseWait` | the portable fallback (sleep, then spin); timerfd later |

More Linux parts in `src/Kopru.Platform.Linux`:

- `Setup/LinuxSystemCheck`: the setup page's checks (vhci_hcd, usbip, pkexec, hidraw access), the Bluetooth radio from
  sysfs/rfkill, and the health chips.
- `Games/LinuxProcessSource`: the Steam game scan of `/proc`.
- `Hidraw/`, `UsbIp/`, `Hiding/`, `Bluetooth/`, `Diagnostics/` as named.

In the app: `Services/LinuxAppServices` (the Linux factories), `Services/XdgAutostartRegistration`,
`Kopru.Core.Instance.UnixSingleInstance` (a lock file and a Unix domain socket in `$XDG_RUNTIME_DIR/ds5-untethered`),
and `ViewModels/AppFeatures` (what the views hide).

Parsing is kept pure and unit-tested in `tests/Kopru.Core.Tests/Linux`.

## The headless spike

`tools/Kopru.LinuxSpike` runs the same bridge without the app, with detailed logging. It is the tool for the questions
below. It is in the same pre-release (`ds5-untethered-linux-spike-x64.tar.gz`):

```sh
tar -xzf ds5-untethered-linux-spike-x64.tar.gz
sudo modprobe vhci-hcd
./ds5-untethered-linux-spike/Kopru.LinuxSpike --list                 # system checks and the DualSense hidraw nodes
sudo ./ds5-untethered-linux-spike/Kopru.LinuxSpike --log spike.log   # run the bridge; Ctrl+C stops it and unplugs the pad
```

The spike's `--attach` option: `manual` (the default for a normal user) prints the exact `sudo usbip ...` command for
you to run in another terminal and waits up to 5 minutes; `sudo` runs `sudo usbip ...` for you; `direct` runs `usbip`
itself and needs the spike to run as root. Other options: `--seconds N` stops after N seconds; `--no-trim` sends output
reports at the full length the shared code builds instead of cutting them to the length the report descriptor declares.

### Where the logs go

- **The app**: `~/.local/state/ds5-untethered/logs/app.log` (rolled over to `app.1.log` at 512 KB). Settings >
  General > Export Diagnostics makes a zip with addresses and your user name masked.
- **The spike**: the console, and with `--log FILE` also FILE. Every line is masked (Bluetooth addresses, your user name,
  `SUDO_USER`). The bridge's own unmasked `app.log` goes next to the `--log` file; its lines also appear, masked, in the
  spike log as `bridge: ...`, so you never need to send it.

### What the spike log shows

- `system:`, `usbip:`, `hidraw:`, `sound:` - the checks at start.
- `state:` - the bridge: `WaitingForController`, `Connecting`, `Running`, or `Error` with the reason.
- `virtual pad:` - served on 127.0.0.1, imported by the usbip client, and then seen as a new hidraw node and sound card.
- `game:` - output reports from a game: trigger effects (`R2 effect 0x..`), rumble, lightbar.
- `1s:` - once a second while anything arrives: audio transfers and milliseconds of audio per second, the longest gap,
  dropouts, game output reports, Bluetooth haptics reports per second (about 94 while haptics play), input rate.
- `status:` - every 5 seconds: input rate, battery, haptics, triggers, rumble, writes and failures, link quality.
- `total:` - at exit.

## Open questions

### (a) Is the virtual pad's USB audio through `vhci_hcd` stable?

1. Start the spike and wait for `state: Running` and `virtual pad: new sound card`.
2. Play 4-channel audio to the virtual pad without a game (pick the card from `aplay -l`):
   `speaker-test -D hw:CARD=<card>,DEV=0 -c 4 -r 48000 -F S16_LE -t sine -l 0`.
   The haptics channels are 3 and 4, so you should feel a buzz while those two play.
3. Leave it for 10 minutes, then Ctrl+C.

We look at the `1s:` lines (about 1000 ms of audio per second, few or no dropouts) and the `total:` line.

### (b) Does a GE-Proton game with Steam Input off send haptics and triggers to the virtual pad?

Start a game with native DualSense haptics and adaptive triggers through Steam with GE-Proton, with Steam Input turned
off for that game, and play a few minutes where the game uses triggers and haptics. Tell us which pad the game used:
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

Anything you try and what happened helps. Undo any change afterwards (reconnecting the controller usually rebinds the
driver).

## What to send

Attach to issue #1 (or a new issue):

- the app's diagnostics zip (Settings > General > Export Diagnostics) or the spike log (`--log spike.log`); both are
  masked already, but have a look before posting,
- `usbip port`, `lsmod | grep vhci`, `aplay -l`,
- `dmesg | tail -n 80` right after the virtual pad was plugged in. This one is **not** masked: remove Bluetooth
  addresses (`XX:XX:XX:XX:XX:XX`) and your user name before posting,
- your distribution, kernel, desktop (and X11 or Wayland), Bluetooth adapter, and the game and Proton version.

## Known limits

- Experimental: expect rough edges; the log is what we need.
- No hiding: games may see the real pad and the virtual one.
- Password prompts: the app asks for your password (pkexec) every time it plugs the virtual controller in. That is when
  you start it (Overview, Settings, tray, shortcuts), when the controller type changes (a pad-type swap: per-game types,
  Automatic, the Overview choice), when the controller reconnects or "Reconnect" is used, and at sign-in when "Start When
  I Sign In" and "Start The Virtual Controller Automatically" are both on (the dialog shows right after you sign in).
  Unplugging asks only in the fallback case above. The optional polkit rule removes the prompts, with the risk it
  describes.
- Output reports are cut to the length the report descriptor declares; whether the pad over Linux Bluetooth wants that
  or the full length is unknown (`--no-trim` in the spike).
- The ioctl numbers follow the generic Linux layout (x86-64, arm64 and most others), not powerpc or mips.
- CI (`.github/workflows/ci.yml`) builds on Ubuntu, runs the Core tests (except `[Trait("Category", "RealTime")]`, the
  timer pacing measurements) and the App tests headless, and publishes the app and the spike to the pre-release. The
  `linux-spike` tag stays where it was first created; each push replaces its two files, and the release notes name the
  commit (short SHA) they were built from.
- A running copy that hangs: a second start says so on the terminal and in the log (it does not open a second copy).
- The pacing of report timing uses the portable wait (sleep, then spin) until a timerfd wait exists.

Contributions: see [CONTRIBUTING.md](../../CONTRIBUTING.md). Pull requests for Linux go to the `linux` branch.
