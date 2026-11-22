// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Hid;

namespace Kopru.Core.Tests;

// A controller's identity (WindowsControllerSource.IdOf): the instance id of its Bluetooth HID node, the same whether
// Windows answers the lookup or not, so a failed lookup never makes it a second controller. Made-up ids, no real address.
public class ControllerIdentityTests
{
	private const string BtPath =
		@"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6#9&2a3b4c5d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
	private const string BtInstance = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\9&2A3B4C5D&0&0000";

	[Fact]
	public void PathText_SpellsTheInstanceId()
	{
		Assert.Equal(BtInstance, DeviceInstance.FromInterfacePathText(BtPath), StringComparer.OrdinalIgnoreCase);
		Assert.Equal(@"HID\VID_054C&PID_0CE6&MI_03\7&1a2b3c4d&0&0000",
			DeviceInstance.FromInterfacePathText(@"\\?\hid#vid_054c&pid_0ce6&mi_03#7&1a2b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"),
			StringComparer.OrdinalIgnoreCase);
		Assert.Null(DeviceInstance.FromInterfacePathText(""));
		Assert.Null(DeviceInstance.FromInterfacePathText(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_test"));
		Assert.Null(DeviceInstance.FromInterfacePathText(@"\\?\hid#only-two#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
	}

	[Fact]
	public void Id_IsTheSame_WhenTheLookupFailsForAMoment()
	{
		string answered = WindowsControllerSource.IdOf(BtPath, _ => BtInstance);
		string failed = WindowsControllerSource.IdOf(BtPath, _ => null);
		Assert.Equal(answered, failed, StringComparer.OrdinalIgnoreCase);
		Assert.True(DeviceInstance.IsBluetoothHid(failed));
		// A path in an unknown form keeps the path itself.
		Assert.Equal(@"\\?\odd", WindowsControllerSource.IdOf(@"\\?\odd", _ => null));
	}
}
