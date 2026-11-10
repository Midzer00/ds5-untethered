// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kopru.Core.UsbIp;

// One device imported by the USB/IP client: the virtual hub port and, when known, where it comes from.
public sealed record UsbIpImport(int Port, string? Host, int? TcpPort, string? BusId)
{
	public bool IsLoopback => Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
}

// Parses the output of "usbip.exe port" (usbip-win2). Each device starts with a "Port NN:" line; the remote shows up
// on a later line as "usbip://host:tcpport/busid". Nothing attached prints nothing at all.
public static partial class UsbIpPortList
{
	[GeneratedRegex(@"^\s*Port\s+(\d+)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PortLine();

	[GeneratedRegex(@"usbip://(\[[^\]]+\]|[^:/\s]+)(?::(\d+))?/(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex RemoteUrl();

	public static IReadOnlyList<UsbIpImport> Parse(string output)
	{
		List<UsbIpImport> imports = [];
		UsbIpImport? entry = null;
		foreach (string line in output.Split('\n'))
		{
			Match port = PortLine().Match(line);
			if (port.Success && int.TryParse(port.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
			{
				if (entry != null)
				{
					imports.Add(entry);
				}
				entry = new UsbIpImport(number, null, null, null);
				continue;
			}
			Match remote = RemoteUrl().Match(line);
			if (remote.Success && entry is { Host: null })
			{
				int? tcp = int.TryParse(remote.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int t) ? t : null;
				entry = entry with { Host = remote.Groups[1].Value, TcpPort = tcp, BusId = remote.Groups[3].Value.Trim() };
			}
		}
		if (entry != null)
		{
			imports.Add(entry);
		}
		return imports;
	}
}
