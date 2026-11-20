// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Mods;

// Whether any IPv4 UDP socket on this machine is bound to a local port, from /proc/net/udp (proc(5): one socket per line,
// "local_address" as hex IP:port; no addresses or process details are kept). The DSX listener uses it to tell that a mod
// closed its socket (exited), and to see a port taken by another program before binding. Unknown (the table cannot be
// read): treated as bound, so nothing ends early.
public static class UdpPortTable
{
	public const string ProcNetUdp = "/proc/net/udp";

	public static bool IsBound(int port)
	{
		try
		{
			return IsBound(File.ReadAllText(ProcNetUdp), port) ?? true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return true;
		}
	}

	// The table's text: true or false, or null when it has no data line at all.
	public static bool? IsBound(string table, int port)
	{
		bool any = false;
		foreach (string line in table.Split('\n').Skip(1))
		{
			string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (fields.Length < 2)
			{
				continue;
			}
			any = true;
			int colon = fields[1].LastIndexOf(':');
			if (colon > 0 && int.TryParse(fields[1].AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int local) && local == port)
			{
				return true;
			}
		}
		return any ? false : null;
	}
}
