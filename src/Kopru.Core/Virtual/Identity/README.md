# Built-in DualSense identity

`dualsense.json` (embedded in Kopru.Core) is what the virtual controller reports about itself when the user has no
capture of their own in `%LOCALAPPDATA%\Untethered\captures`. It was taken from a cabled DualSense (`Kopru.Probe
capture`) and stripped of everything unit specific.

Kept as captured (identical on every DualSense of this model):

- Device, configuration and HID report descriptors (VID 054C / PID 0CE6). The device descriptor has no serial number
  string (iSerialNumber = 0).
- String descriptors 1 and 2: manufacturer and product name only.
- Feature 0x20: firmware build date and version numbers. Shared by every controller on that firmware; games and
  drivers read it to pick the feature set.
- Features 0x81, 0x83, 0x85, 0xE0, 0xF1, 0xF2, 0xF5: status/test-command replies with constant content (zeros or
  fixed flags), answered so drivers that probe them see what a real pad says.

Replaced:

- Features 0x09 and 0x0B (pairing info): the controller's Bluetooth address is the virtual one (`02:4B:50:52:55:01`,
  also forced at runtime) and the paired host's address is zeroed.

Dropped:

- Feature 0x05 (IMU calibration): unit specific. At runtime the live controller's own 0x05 is read over Bluetooth and
  overlaid in USB form (see `CalibrationOverlay`); a neutral calibration is used if it can't be read.
- Feature 0x22 (hardware info): contains the controller's Bluetooth address and a unit-unique id.
- Reports the capture could not read (0x08, 0x0A, 0x0C, 0x21, 0x80, 0x82, 0x84, 0xA0, 0xF0, 0xF4): the virtual pad
  stalls them, as the real one failed them.

The DualSense Edge identity (`dualsense-edge.json`) is described below.

# Built-in DualSense Edge identity

`dualsense-edge.json` is used when a DualSense Edge is connected and "Present as Edge" is on (the default; off presents
the Edge with the DualSense identity above, for games that only know the DualSense). Nobody on the project owns an
Edge, so it is assembled from public data and is **not verified on hardware**:

- Device descriptor: the DualSense one with the product id changed to 0x0DF2 (the Edge's public USB id).
  bcdDevice (0x0100), class and packet size are kept from the DualSense: the Edge's own values are unknown.
- Strings: manufacturer as on the DualSense; product "DualSense Edge Wireless Controller" (the Edge's public name).
- HID report descriptor: the 405-byte USB descriptor of a real Edge as shared publicly on the linux-input mailing list
  (`references/edge/usb_descriptor.txt`). Its first 109 bytes (input report 0x01, output report 0x02) equal the
  DualSense's; the rest declares the same feature reports plus 0x60–0x65, 0x68 and 0x70–0x7B.
- Configuration descriptor: the DualSense one (same interfaces and endpoints) with the HID descriptor's
  wDescriptorLength changed from 0x0121 (289) to 0x0195 (405). The total length is unchanged.
- Feature reports shared with the DualSense: the DualSense answers above, as they are (0x09/0x0B virtual pairing
  address, 0x20 firmware info, status replies). 0x05 (calibration) is overlaid from the live controller exactly as for
  the DualSense. 0x20 carries the DualSense firmware build, not an Edge one; a driver that checks the Edge firmware
  version may see an unexpected value.
- Edge-only feature reports 0x60–0x7B (the controller's stored profiles and their management): GET is stalled, as a
  real pad does for a report it cannot answer, rather than inventing a profile format; SET is accepted and discarded
  (profiles are never written to the real controller). Software that requires these (Sony's Edge profile tools) will
  see an Edge without profiles or fail to manage profiles; games are not expected to read them.

Input from the Edge's Fn buttons and back paddles (byte 9 of the input body, see `EdgePaddleRemap`) is passed through
as the Edge reports it when presented as an Edge, and dropped unless remapped when presented as a DualSense.
