// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Bridge;

// Battery while the bridge is stopped: a short read-only probe (BatteryProbe). It never runs while the bridge starts or
// runs, or while a test holds the controller; StartAsync cancels a probe in progress and waits for it.
public sealed partial class BridgeHost
{
	private CancellationTokenSource? probeCancel;

	private int startsPending;

	// Null turns probing off.
	public BatteryProbe? BatteryProbe { get; set; }

	// Reads the battery once when the bridge is stopped; the reading goes into Status.Battery. Null when skipped or
	// nothing was read.
	public async Task<BatteryStatus?> ProbeBatteryAsync()
	{
		BatteryProbe? probe = BatteryProbe;
		if (probe == null || !CanProbe())
		{
			return null;
		}
		if (!await sessionGate.WaitAsync(0).ConfigureAwait(false))
		{
			return null;
		}
		CancellationTokenSource cts = new();
		try
		{
			lock (gate)
			{
				if (supervisor != null || startsPending > 0)
				{
					return null;
				}
				probeCancel = cts;
			}
			probe.Opened = ReadDetailsOnce;
			BatteryStatus? reading = await probe.ReadAsync(cts.Token).ConfigureAwait(false);
			if (reading is not { } b)
			{
				return null;
			}
			lock (gate)
			{
				if (supervisor != null || cts.IsCancellationRequested)
				{
					return null;
				}
				battery = b;
			}
			StatusUpdated?.Invoke(Status);
			return b;
		}
		finally
		{
			lock (gate)
			{
				if (probeCancel == cts)
				{
					probeCancel = null;
				}
				cts.Dispose();
			}
			sessionGate.Release();
		}
	}

	private bool CanProbe()
	{
		lock (gate)
		{
			return supervisor == null && startsPending == 0;
		}
	}

	// StartAsync calls this before it waits for the session gate: a probe in progress stops at once.
	private void BeginStart()
	{
		lock (gate)
		{
			startsPending++;
			probeCancel?.Cancel();
		}
	}

	private void EndStart()
	{
		lock (gate)
		{
			startsPending--;
		}
	}
}
