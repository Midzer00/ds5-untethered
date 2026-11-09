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

TODO: no DualSense Edge capture exists yet; Edge controllers are presented with this DualSense identity.
