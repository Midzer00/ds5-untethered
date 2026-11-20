// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.Versioning;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;

namespace Kopru.Platform.Linux.Hidraw;

// The Bluetooth DualSense controllers, found and opened through hidraw. Opening fails (null) while the node is not
// readable and writable by this user; the first such failure per node is logged with the fix.
[SupportedOSPlatform("linux")]
public sealed class HidrawControllerSource(HidrawFinder finder, Action<string>? log = null) : IControllerSource, IReadOnlyControllerSource
{
	private readonly HashSet<string> reported = new(StringComparer.Ordinal);

	// Cut output reports to their declared length (HidrawLink.TrimOutput).
	public bool TrimOutput { get; set; } = true;

	// Raised with each link this source opened (the spike reads its write counters).
	public event Action<HidrawLink>? Opened;

	public IReadOnlyList<ControllerPresence> Enumerate()
	{
		try
		{
			return [.. finder.FindNodes().Where(n => n.IsBluetooth).Select(n => new ControllerPresence(n.StableId, n.ToHidDevice()))];
		}
		catch (Exception)
		{
			return [];
		}
	}

	public IHidLink? TryOpen() => Open(finder.FindNodes().FirstOrDefault(n => n.IsBluetooth), readOnly: false);

	public IHidLink? TryOpen(string id) => Open(finder.FindNodes().FirstOrDefault(n => n.IsBluetooth && n.StableId == id), readOnly: false);

	public IHidLink? TryOpenReadOnly() => Open(finder.FindNodes().FirstOrDefault(n => n.IsBluetooth), readOnly: true);

	private HidrawLink? Open(HidrawNode? node, bool readOnly)
	{
		if (node == null)
		{
			return null;
		}
		try
		{
			HidrawLink link = readOnly ? HidrawLink.OpenReadOnly(node) : HidrawLink.Open(node);
			link.TrimOutput = TrimOutput;
			Opened?.Invoke(link);
			return link;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			bool first;
			lock (reported)
			{
				first = reported.Add(node.Name + e.GetType().Name);
			}
			if (first)
			{
				log?.Invoke(e is UnauthorizedAccessException
					? $"hidraw: no access to {node.DevicePath}; add a udev rule (docs/linux/README.md) or run as root for the spike"
					: $"hidraw: cannot open {node.DevicePath}: {e.Message}");
			}
			return null;
		}
	}
}
