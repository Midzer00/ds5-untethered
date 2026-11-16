// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Mods;

namespace Kopru.App.Services;

// DSX mod support: the listener runs while the setting is on (ApplySettings), on 127.0.0.1 only. Controller index 0 is
// slot 1 (this host), index i slot i + 1 (the controller manager's hosts). A port taken by another program (DSX itself) is
// tried again every DsxRetryInterval while the setting stays on.
public sealed partial class RealControllerService
{
	public static readonly TimeSpan DsxRetryInterval = TimeSpan.FromSeconds(10);

	private readonly object dsxGate = new();
	private DsxListener? dsx;
	private DateTimeOffset dsxTriedAt;

	// The port the listener uses (tests: 0, any free port).
	public int DsxPort { get; init; } = DsxListener.DefaultPort;

	// The running listener while the setting is on (tests).
	public DsxListener? Dsx => dsx;

	public DsxStatus DsxStatus => dsx?.Status ?? DsxStatus.Off with { Port = DsxPort };

	public event EventHandler? DsxStatusChanged;

	private void ApplyDsx(bool on)
	{
		DsxListener? stopping = null;
		lock (dsxGate)
		{
			if (on == (dsx != null) || (on && Volatile.Read(ref disposed) != 0))
			{
				return;
			}
			if (on)
			{
				DsxListener listener = new(new BridgeHostsDsxTarget(DsxHost), time) { Port = DsxPort };
				listener.StatusChanged += _ => post(() => DsxStatusChanged?.Invoke(this, EventArgs.Empty));
				dsx = listener;
				dsxTriedAt = time.GetUtcNow();
				listener.Start();
			}
			else
			{
				stopping = dsx;
				dsx = null;
			}
		}
		stopping?.Stop();
		post(() => DsxStatusChanged?.Invoke(this, EventArgs.Empty));
	}

	// Every game tick: a listener that could not take its port tries again now and then.
	private void RetryDsx()
	{
		lock (dsxGate)
		{
			if (dsx is not { } listener || listener.Status.State is not (DsxListenerState.PortInUse or DsxListenerState.Failed)
				|| time.GetUtcNow() - dsxTriedAt < DsxRetryInterval)
			{
				return;
			}
			dsxTriedAt = time.GetUtcNow();
			listener.Start();
		}
	}

	private void StopDsx(string reason)
	{
		DsxListener? stopping;
		lock (dsxGate)
		{
			stopping = dsx;
			dsx = null;
		}
		stopping?.Stop(reason);
	}

	private BridgeHost? DsxHost(int index) => index == 0 ? host
		: multi is { } m && index > 0 && index < BridgeManager.MaxControllers ? m.Host(index + 1) : null;
}
