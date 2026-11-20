// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Kopru.Core.Hid;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// Where the virtual pad's identity (descriptors, feature reports) comes from.
public interface IIdentitySource
{
	// Null when there is none yet; throws when there is one but it can't be read.
	DeviceIdentity? Load();
}

// Finds and opens the Bluetooth DualSense.
public interface IControllerSource
{
	// Null while no Bluetooth DualSense can be opened.
	IHidLink? TryOpen();

	// Every Bluetooth DualSense present now, in a stable order (see BridgeManager).
	IReadOnlyList<ControllerPresence> Enumerate() => [];

	// Opens the controller with that id; null while it can't be opened.
	IHidLink? TryOpen(string id) => null;
}

// Plugs the USB/IP device served on the loopback into the system and unplugs it.
public interface IUsbIpAttacher
{
	// Returned by AttachAsync when the device was plugged in but the client did not say on which port. Ports start at 0
	// on Linux, so 0 is a real port.
	public const int UnknownPort = -1;

	// Returns the virtual hub port, or UnknownPort. Throws FileNotFoundException when the USB/IP client is not installed.
	Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel);

	Task DetachAsync(int port);

	// The devices the client has imported (all of them, not only ours). Throws when the listing failed.
	Task<IReadOnlyList<UsbIp.UsbIpImport>> ListAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<UsbIp.UsbIpImport>>([]);
}

// The newest captures/*.json (made by "Kopru.Probe capture"), looked up in the app's data folder, the working directory,
// then next to the app.
public sealed class CaptureIdentitySource : IIdentitySource
{
	private readonly string[] directories;

	public CaptureIdentitySource(params string[] directories)
	{
		this.directories = directories.Length > 0 ? directories :
			[AppPaths.Captures, Path.Combine(Environment.CurrentDirectory, "captures"), Path.Combine(AppContext.BaseDirectory, "captures")];
	}

	public static string? NewestCapture(string directory) =>
		Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault() : null;

	public DeviceIdentity? Load()
	{
		foreach (string directory in directories)
		{
			string? file = NewestCapture(directory);
			if (file != null)
			{
				return DeviceIdentity.FromCaptureFile(file);
			}
		}
		return null;
	}
}

