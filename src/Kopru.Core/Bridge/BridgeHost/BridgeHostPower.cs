// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Diagnostics;

namespace Kopru.Core.Bridge;

// Turning the controller off: the running connection is torn down (virtual pad unplugged first), then the controller
// is disconnected from the PC radio, which powers it down. The host then waits for it again.
public sealed partial class BridgeHost
{
	private IControllerPower power = Platform.PlatformServices.Current.ControllerPower;

	public IControllerPower Power
	{
		get => power;
		set => power = value;
	}

	// When on, IdleTimeout also turns the controller off.
	public bool PowerOffWhenIdle { get; set; }

	private string? pauseHapticsReason;

	// "Pause Haptics In Silence" for this host's controller (null reason: off): applied to the running connection at once
	// and to every later one. reason ("setting" or "low power") goes to the log line written on a change.
	public bool PauseHapticsInSilence => Volatile.Read(ref pauseHapticsReason) != null;

	public void SetPauseHapticsInSilence(string? reason)
	{
		string? was = Interlocked.Exchange(ref pauseHapticsReason, reason);
		if ((was != null) != (reason != null) || (reason != null && was != reason))
		{
			AppLog.Write($"haptics{LogSlot}: pause in silence {(reason != null ? "on" : "off")} ({reason ?? was ?? "setting"})");
		}
		if (current is { } c)
		{
			c.Session.PauseHapticsInSilence = reason != null;
		}
	}

	// Raised after a power-off attempt (true: the radio accepted the disconnect); the second argument is true for idle.
	public event Action<bool, bool>? PoweredOff;

	// Turns the running controller off. False when nothing is running, its address is unknown or the disconnect failed.
	public Task<bool> PowerOffAsync() => PowerOffAsync("unspecified");

	// reason: who asked (logged).
	public Task<bool> PowerOffAsync(string reason)
	{
		Connection? c = current;
		AppLog.Write($"power off requested{LogSlot}: {reason}{(c == null ? " (nothing running)" : string.Empty)}");
		return c == null ? Task.FromResult(false) : RequestPowerOff(c, false);
	}

	private Task<bool> RequestPowerOff(Connection c, bool idle)
	{
		if (idle)
		{
			AppLog.Write($"power off requested{LogSlot}: idle for {IdleMinutes} min");
		}
		ulong? address;
		try
		{
			address = power.FindAddress(c.Session.Device);
		}
		catch (Exception e)
		{
			AppLog.Write("power off: address lookup failed: " + e.Message);
			address = null;
		}
		if (address is not ulong a)
		{
			AppLog.Write("power off: controller address unknown");
			PoweredOff?.Invoke(false, idle);
			return Task.FromResult(false);
		}
		PowerOffRequest request = new(a, idle);
		PowerOffRequest? existing = Interlocked.CompareExchange(ref c.PowerOff, request, null);
		return (existing ?? request).Done.Task;
	}

	// Called by the supervisor after the connection was closed.
	private async Task FinishPowerOffAsync(PowerOffRequest request)
	{
		bool ok;
		try
		{
			ok = await power.DisconnectAsync(request.Address).ConfigureAwait(false);
		}
		catch (Exception e)
		{
			AppLog.Write("power off: disconnect failed: " + e.Message);
			ok = false;
		}
		AppLog.Write(ok ? "power off: controller disconnected" : "power off: radio refused the disconnect");
		request.Done.TrySetResult(ok);
		PoweredOff?.Invoke(ok, request.Idle);
	}

	private sealed class PowerOffRequest(ulong address, bool idle)
	{
		public ulong Address { get; } = address;

		public bool Idle { get; } = idle;

		public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
}
