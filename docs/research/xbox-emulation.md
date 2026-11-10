# Xbox 360 emulation for games without DualSense support

Researched 2026-11-09. This is opt-in per game. In Xbox mode the game gets no native haptics, adaptive triggers or speaker. Its rumble is still played as haptics through RumbleSynth, which is BT-safe.

## Options

### ViGEmBus (kernel bus driver) + Nefarius.ViGEm.Client
- **Status:** retired. The GitHub repo (nefarius/ViGEmBus) says "THIS PROJECT HAS BEEN RETIRED" and has been archived (read-only) since 2023-11-02. It gets no more fixes and no new signed builds.
- **Last release:** the 1.22.x line from 2023. The archived repo's Releases list was empty when fetched, so the exact version is not verified here. The existing production-signed installer still installs on Windows 10/11, but nobody will re-sign it if Microsoft revokes or tightens anything.
- **License:** BSD-3-Clause (driver).
- **Managed client:** NuGet `Nefarius.ViGEm.Client` 1.21.256 (2023-02-05), MIT. It is also unmaintained.
- **Cost for us:** a second third-party kernel driver to install, detect in SystemCheck and support. It is retired, and some anti-cheat systems flag it.

### USB/IP virtual wired Xbox 360 controller (chosen)
- We already serve a virtual DualSense over USB/IP (usbip-win2's vhci driver, `UsbIpServer`). A wired Xbox 360 controller is a plain full-speed USB device, VID `045E`, PID `028E`. Its descriptors and reports are publicly documented: the Linux `xpad` driver notes, free60 "Xbox 360 controller USB protocol", and public `lsusb -v` dumps.
- **Driver binding:** Windows' in-box XUSB driver (`xusb22.sys`, "Xbox 360 Controller for Windows") matches the hardware ID `USB\VID_045E&PID_028E`. To Windows, a USB/IP device is an ordinary USB device under the vhci root hub, so the in-box driver should bind with no extra install. Hobby XInput devices that copy these descriptors (Arduino/Teensy XInput cores) enumerate as XInput pads on Windows without the security handshake, so the security interface is described but never answers. **This is still to be confirmed on hardware (see the test steps).**
- **Protocol:** interrupt IN `0x81` carries a 20-byte report. Interrupt OUT `0x01` carries rumble `00 08 00 <large> <small> 00 00 00` and LED `01 03 <pattern>`, which is ignored. Vendor control requests from the driver are answered with zeros (IN) or accepted (OUT).
- **Cost:** about 300 lines, no new driver, same attach/detach path as the DualSense.

### DualShock 4 over USB/IP
The DS4 v2 (`054C:09CC`) descriptors and its HID report are also public. It would mostly reuse the HID path of `VirtualDualSense`, but it needs its own descriptors, feature reports (calibration 0x02, 0x12/0xA3 pairing info) and an input report conversion. **Left for later.** Xbox 360 covers the "no DualSense support" case, because XInput is what those games read. `VirtualPadType` and `IVirtualPad` are ready for a `DualShock4` entry.

## Choice
USB/IP virtual Xbox 360 pad (`Virtual/VirtualXbox360.cs`) behind `IVirtualPad`. If xusb22 refuses the USB/IP device on real hardware, ViGEmBus can be added behind the same `IVirtualPad`/`UsePad` interface as an optional backend, detected by SystemCheck and never required.

## Mapping (DualSense → XInput)
| DualSense | Xbox 360 |
|---|---|
| Cross / Circle / Square / Triangle | A / B / X / Y |
| L1 / R1 | LB / RB |
| L2 / R2 (analog) | LT / RT (0..255) |
| Create, touchpad click | Back |
| Options | Start |
| PS | Guide |
| L3 / R3 | LS / RS click |
| D-pad hat | D-pad bits (diagonals = two bits) |
| Sticks 0..255 (Y down) | int16, 128 → 0, Y inverted (up positive) |
| Mute | not mapped |

Applied after the app's shortcuts and InputTransform (remap, curves, gyro), so user tuning still works. Rumble OUT becomes a DualSense output report with only the classic motors, and the existing rumble-as-haptics path plays it.
