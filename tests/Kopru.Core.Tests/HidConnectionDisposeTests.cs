// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hid;
using Xunit;

namespace Kopru.Core.Tests;

// A session disposes its link and the link's owner may dispose it again (Kopru.Probe micload did): the second Dispose
// and a late CancelRead must not throw. An ordinary temp file stands in for the HID device (CreateFile opens both).
public sealed class HidConnectionDisposeTests : IDisposable
{
	private readonly string file = Path.GetTempFileName();

	public void Dispose() => File.Delete(file);

	private HidDevice Device => new(file, HidDevice.SonyVendorId, HidDevice.DualSenseProductId, 64, 48, 64);

	[Fact]
	public void ConnectionCanBeDisposedTwice()
	{
		HidConnection connection = WindowsHid.Open(Device);
		connection.Dispose();
		connection.Dispose();
		connection.CancelRead();
	}

	[Fact]
	public void ReadOnlyConnectionCanBeDisposedTwice()
	{
		ReadOnlyHidConnection connection = WindowsHid.OpenReadOnly(Device);
		connection.Dispose();
		connection.Dispose();
		connection.CancelRead();
	}
}
