// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Diagnostics;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// Stale virtual pads: a run that crashed while the bridge ran can leave its pad attached (or a dead hub port). Before
// every attach, and once at app start, ports that are ours are detached: loopback imports of our bus id, or the port
// the state file recorded when the listing can't tell. Other USB/IP devices are never touched.
public sealed partial class BridgeHost
{
	private readonly SemaphoreSlim cleanupGate = new(1, 1);

	// Where the attached port is recorded while running; null keeps nothing on disk.
	public BridgeStateFile? StateFile { get; set; }

	// The ports that belong to us, given the client's listing (null when listing failed) and the recorded state.
	public static IReadOnlyList<int> StalePorts(IReadOnlyList<UsbIpImport>? imports, BridgeAttachState? recorded) =>
		StalePorts(imports, recorded, VirtualDualSense.BusId);

	// The same for the pad with the given bus id (one per player slot, see BridgeHost.Slot).
	public static IReadOnlyList<int> StalePorts(IReadOnlyList<UsbIpImport>? imports, BridgeAttachState? recorded, string busId)
	{
		SortedSet<int> ports = [];
		if (imports == null)
		{
			if (recorded != null)
			{
				ports.Add(recorded.Port);
			}
			return [.. ports];
		}
		foreach (UsbIpImport i in imports)
		{
			bool ourBus = i.IsLoopback && string.Equals(i.BusId, busId, StringComparison.OrdinalIgnoreCase);
			bool recordedHere = recorded != null && i.Port == recorded.Port &&
				(i.Host == null || (i.IsLoopback && (i.TcpPort == null || i.TcpPort == recorded.TcpPort)));
			if (ourBus || recordedHere)
			{
				ports.Add(i.Port);
			}
		}
		return [.. ports];
	}

	// Detaches virtual pads left behind by an earlier run; never throws except for cancellation. Returns the ports detached.
	public async Task<IReadOnlyList<int>> CleanupStaleAsync(CancellationToken cancel = default)
	{
		await cleanupGate.WaitAsync(cancel).ConfigureAwait(false);
		try
		{
			BridgeAttachState? recorded = StateFile?.Load();
			IReadOnlyList<UsbIpImport>? imports;
			try
			{
				imports = await attacher.ListAsync(cancel).ConfigureAwait(false);
			}
			catch (FileNotFoundException)
			{
				// No USB/IP client: nothing can be attached.
				return [];
			}
			catch (Exception e) when (e is not OperationCanceledException)
			{
				AppLog.Write("cleanup: usbip port failed: " + e.Message);
				imports = null;
			}
			IReadOnlyList<int> stale = StalePorts(imports, recorded, BusId);
			foreach (int port in stale)
			{
				try
				{
					await attacher.DetachAsync(port).ConfigureAwait(false);
					AppLog.Write($"cleanup: detached stale virtual pad on port {port}");
				}
				catch (Exception e)
				{
					AppLog.Write($"cleanup: detach of port {port} failed: {e.Message}");
				}
			}
			if (recorded != null)
			{
				StateFile?.Clear();
			}
			return stale;
		}
		finally
		{
			cleanupGate.Release();
		}
	}

	private void RecordAttached(int port, int tcpPort) => StateFile?.Save(new BridgeAttachState(port, tcpPort, BusId));

	private void ClearAttached() => StateFile?.Clear();
}
