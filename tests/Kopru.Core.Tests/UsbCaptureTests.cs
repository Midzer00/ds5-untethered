// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Usb;

namespace Kopru.Core.Tests;

public class UsbCaptureTests
{
	[Fact]
	public void HidInterfaces_AreFoundWithTheirReportDescriptorLength()
	{
		byte[] configuration =
		[
			9, 2, 0, 0, 2, 1, 0, 0xC0, 0xFA,          // configuration
			9, 4, 0, 0, 0, 1, 1, 0, 0,                // interface 0: audio control
			9, 4, 3, 0, 2, 3, 0, 0, 0,                // interface 3: HID
			9, 0x21, 0x11, 0x01, 0, 1, 0x22, 0x11, 0x01, // HID descriptor: one report descriptor of 0x111 bytes
			7, 5, 0x84, 3, 64, 0, 6,                  // endpoint
		];

		Assert.Equal([(3, 0x111)], UsbCapture.HidInterfaces(configuration));
	}
}
