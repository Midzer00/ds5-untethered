// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Mods;

namespace Kopru.Core.Bridge;

// DSX mod effects for this controller (ControllerBridgeMods): kept across connections and pad swaps until the mod's
// effects end (DsxListener), written to the running connection. Writes are coalesced: at most one per ModWriteGap, the
// newest output wins (a mod may send hundreds of packets a second).
public sealed partial class BridgeHost
{
	public static readonly TimeSpan ModWriteGap = TimeSpan.FromMilliseconds(4);

	private volatile ModOutput? modOutput;
	private long modWrittenAt = long.MinValue / 2;
	private int modWritePending;

	// What a mod asks of this controller now; null: nothing.
	public ModOutput? ModOutput => modOutput;

	// Sets (null ends) the mod's output; when its light ends, the user's light (or the game's) is written again.
	public void SetModOutput(ModOutput? value)
	{
		modOutput = value is { IsEmpty: true } ? null : value;
		TimeSpan since = Stopwatch.GetElapsedTime(Interlocked.Read(ref modWrittenAt));
		if (since >= ModWriteGap)
		{
			WriteModToCurrent();
			return;
		}
		if (Interlocked.Exchange(ref modWritePending, 1) == 0)
		{
			_ = Task.Delay(ModWriteGap - since).ContinueWith(_ =>
			{
				Volatile.Write(ref modWritePending, 0);
				WriteModToCurrent();
			}, TaskScheduler.Default);
		}
	}

	// Writes the host's mod output to the running connection's bridge (nothing when they already agree on "none").
	private void WriteModToCurrent()
	{
		Connection? c = current;
		if (c == null)
		{
			return;
		}
		ModOutput? wanted = modOutput;
		if (wanted == null && c.Bridge.ModOutput == null)
		{
			return;
		}
		Interlocked.Exchange(ref modWrittenAt, Stopwatch.GetTimestamp());
		try
		{
			if (c.Bridge.SetModOutput(wanted))
			{
				if (!InvalidateLightSender())
				{
					ApplyLightTo(c.Session);
				}
				c.Bridge.RestoreGameLight();
			}
		}
		catch (Exception)
		{
			// The controller is going away; the mod's output is written again on the next connect.
		}
	}

	// A new bridge (connect, pad swap) after it is in place: the mod's output as the host holds it now (it may have changed
	// between the bridge's seed and now), written to the controller.
	private void SyncModOutput(ControllerBridge bridge)
	{
		bridge.ModLightAllowed = light.Brightness > 0;
		ModOutput? wanted = modOutput;
		if (!Equals(bridge.ModOutput, wanted))
		{
			bridge.SetModOutput(wanted);
		}
		else if (wanted != null)
		{
			bridge.WriteModOutput();
		}
	}

	// The user's light changed: the mod's light bar and player LEDs show only while the user's light is on (Low Power sets
	// it off).
	private void ApplyModLightAllowed(LightSettings settings)
	{
		Connection? c = current;
		if (c != null)
		{
			c.Bridge.ModLightAllowed = settings.Brightness > 0;
		}
	}

	// The controller as a DSX status reply describes it; null while the bridge does not run it.
	public DsxDevice? DsxDevice(int index) => State == BridgeState.Running && Device is { } device
		? new DsxDevice(index, device.IsEdge, Status.Battery?.Percent ?? 0)
		: null;
}

// The DSX listener's target: controller index i is slot i + 1 (host(i) gives that slot's host, or null when there is
// none yet). An output for a host that is not there yet is kept and placed once it appears (Sync).
public sealed class BridgeHostsDsxTarget(Func<int, BridgeHost?> host) : IDsxTarget
{
	private readonly ModOutput?[] wanted = new ModOutput?[DsxModState.MaxControllers];

	public void SetModOutput(int index, ModOutput? output)
	{
		if (index is >= 0 and < DsxModState.MaxControllers)
		{
			wanted[index] = output;
		}
		host(index)?.SetModOutput(output);
	}

	public void Sync()
	{
		for (int i = 0; i < DsxModState.MaxControllers; i++)
		{
			if (host(i) is { } h && !Equals(h.ModOutput, wanted[i] is { IsEmpty: true } ? null : wanted[i]))
			{
				h.SetModOutput(wanted[i]);
			}
		}
	}

	public IReadOnlyList<DsxDevice> Devices()
	{
		List<DsxDevice> devices = [];
		for (int i = 0; i < DsxModState.MaxControllers; i++)
		{
			if (host(i)?.DsxDevice(i) is { } d)
			{
				devices.Add(d);
			}
		}
		return devices;
	}
}
