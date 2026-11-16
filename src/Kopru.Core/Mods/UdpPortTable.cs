// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Runtime.InteropServices;

namespace Kopru.Core.Mods;

// Whether any IPv4 UDP socket on this PC is bound to a local port (the IP Helper's UDP table; no addresses or process
// details are kept). The DSX listener uses it to tell that a mod closed its socket (exited), and to see a port taken by
// another program before binding. Unknown (the table cannot be read): treated as bound, so nothing ends early.
public static class UdpPortTable
{
	private const int AfInet = 2;
	private const int UdpTableBasic = 0;
	private const int ErrorInsufficientBuffer = 122;

	public static bool IsBound(int port)
	{
		if (!OperatingSystem.IsWindows())
		{
			return true;
		}
		try
		{
			int size = 0;
			int result = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, UdpTableBasic, 0);
			for (int attempt = 0; attempt < 3 && result == ErrorInsufficientBuffer; attempt++)
			{
				IntPtr buffer = Marshal.AllocHGlobal(size);
				try
				{
					result = GetExtendedUdpTable(buffer, ref size, false, AfInet, UdpTableBasic, 0);
					if (result != 0)
					{
						continue;
					}
					int rows = Marshal.ReadInt32(buffer);
					// MIB_UDPROW: DWORD local address, DWORD local port (network order in the low 16 bits).
					for (int i = 0; i < rows; i++)
					{
						int local = Marshal.ReadInt32(buffer, 4 + i * 8 + 4);
						if ((ushort)IPAddress.NetworkToHostOrder((short)(local & 0xFFFF)) == port)
						{
							return true;
						}
					}
					return false;
				}
				finally
				{
					Marshal.FreeHGlobal(buffer);
				}
			}
			// The table could not be read.
			return true;
		}
		catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
		{
			return true;
		}
	}

	[DllImport("iphlpapi.dll", SetLastError = false)]
	private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, int reserved);
}
