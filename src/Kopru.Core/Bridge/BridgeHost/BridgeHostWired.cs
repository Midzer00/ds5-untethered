// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Hid;

namespace Kopru.Core.Bridge;

// EXPERIMENTAL wired mode: while the bridged controller is also on a USB cable (and its twin is hidden from games),
// the running connection's session moves over to the cable (BluetoothSession.AttachWired: input 0x01, output 0x02),
// and when the controller's own USB audio endpoint could be opened (OpenWiredAudio) the haptics stream's blocks and
// the game's speaker audio go over it; its USB capture endpoint is the mic's source the same way (OpenWiredMic).
// Once USB input is live the controller's Bluetooth link is disconnected (HandOffBluetoothAsync; owner decision
// 2026-11-24: wired mode runs over USB alone, no dual-link fallback). The session and the virtual pad stay through the
// switch; when the cable is pulled the controller is simply gone until it reconnects over Bluetooth. An endpoint missing at enter or
// lost later is asked for again every WiredAudioRetryInterval while wired (the endpoints can appear a moment after
// the cable is in), slower once WiredAudioBackoffTries misses say it is not coming soon. The virtual pad, the
// server and the bridge objects are never touched by entering or leaving.
public sealed partial class BridgeHost
{
	// Opens the controller's USB HID interface read+write for the wired link (tests replace it).
	public Func<HidDevice, IHidLink?> OpenWired { get; set; } = d => WindowsHid.Open(d);

	// Opens the controller's USB audio endpoint for haptics and speaker (tests replace it; null = they stay on
	// Bluetooth and the open is asked for again every WiredAudioRetryInterval, slower after WiredAudioBackoffTries
	// misses, while wired).
	public Func<HidDevice, Audio.IWiredAudioSink?> OpenWiredAudio { get; set; } = Audio.WiredAudio.OpenDefault;

	// Opens the controller's USB microphone capture feeding the mic pipeline (tests replace it; null = the mic stays
	// on Bluetooth and the open is asked for again every WiredAudioRetryInterval, slower after WiredAudioBackoffTries
	// misses, while wired).
	public Func<HidDevice, Audio.MicPcmHandler, IDisposable?> OpenWiredMic { get; set; } = Audio.WiredAudio.OpenMicDefault;

	// How often a missing USB audio output or USB microphone is asked for again while wired.
	// Timeout.InfiniteTimeSpan disables the retries.
	public TimeSpan WiredAudioRetryInterval { get; set; } = TimeSpan.FromSeconds(2);

	// The slower ask pace after WiredAudioBackoffTries misses in a row (an endpoint that never appears should not
	// be polled forever at the fast rate).
	public TimeSpan WiredAudioSlowRetryInterval { get; set; } = TimeSpan.FromSeconds(15);

	// Misses in a row after which the wired audio/mic opens slow to WiredAudioSlowRetryInterval.
	private const int WiredAudioBackoffTries = 10;

	public bool IsWired => current?.Session.IsWired ?? false;

	// Disconnect the controller's Bluetooth link once wired mode has USB input (the default). Off: Bluetooth stays
	// connected beside the cable (a failed USB link then falls back to it).
	public bool DisconnectBluetoothWhenWired { get; set; } = true;

	// How long the hand-off waits for the first USB input report before it keeps Bluetooth connected.
	public TimeSpan WiredInputWait { get; set; } = TimeSpan.FromSeconds(2);

	// The last Bluetooth hand-off (tests wait for it).
	internal Task? BluetoothHandOff { get; private set; }

	public HidDevice? WiredDevice => current?.Session.WiredDevice;

	public bool IsWiredAudio => current?.Session.IsWiredAudio ?? false;

	// The running connection was opened on the controller's USB link (taken over on the cable alone).
	public bool IsUsbOnly => current?.Session.IsUsbOnly ?? false;

	// Switches the running connection to the controller's own USB link. Null on success, else a short fixed reason
	// ("no running connection", "usb device not opened", "usb device not opened (<ExceptionTypeName>)").
	public string? EnterWired(HidDevice usb)
	{
		Connection? c = current;
		if (c == null)
		{
			return "no running connection";
		}
		BluetoothSession session = c.Session;
		if (session.WiredDevice is { } already && string.Equals(already.Path, usb.Path, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		if (session.IsWired)
		{
			LeaveWired("wired target changed");
		}
		IHidLink? link;
		try
		{
			link = OpenWired(usb);
		}
		catch (Exception e)
		{
			return "usb device not opened (" + e.GetType().Name + ")";
		}
		if (link == null)
		{
			return "usb device not opened";
		}
		// A new stretch on this plug: retry counters and the logged miss reason start over.
		Volatile.Write(ref c.WiredAudioMiss, null);
		Volatile.Write(ref c.WiredAudioTries, 0);
		Volatile.Write(ref c.WiredAudioStrikes, 0);
		Audio.IWiredAudioSink? audio = OpenWiredAudioLogged(c, usb);
		if (audio != null)
		{
			Volatile.Write(ref c.WiredAudioSince, time.GetUtcNow().UtcTicks);
		}
		try
		{
			session.AttachWired(link, audio);
		}
		catch
		{
			link.Dispose();
			audio?.Dispose();
			throw;
		}
		if (!session.IsWired)
		{
			// The link already fell the session back to Bluetooth (its reader failed at once).
			return "usb link dropped right after it was opened";
		}
		Diagnostics.AppLog.Write($"wired{LogSlot}: on (input and output over USB)");
		// A transport change: the first seconds after it say nothing about the radio (as on connect).
		linkQuality.Restart(NowMs, "wired");
		ReapplyMicForTransport(c);
		if (DisconnectBluetoothWhenWired)
		{
			BluetoothHandOff = Task.Run(() => HandOffBluetoothAsync(c, usb));
		}
		return null;
	}

	// Disconnects the controller's Bluetooth link once the wired link delivered input (as Power Off does; the
	// controller stays powered over the cable). The session ends its Bluetooth reader quietly and runs on USB alone.
	// Nothing happens when the connection or the wired link changed meanwhile, when no USB input came within
	// WiredInputWait, or when the address is unknown; a refused disconnect keeps Bluetooth (logged).
	private async Task HandOffBluetoothAsync(Connection c, HidDevice usb)
	{
		long until = Environment.TickCount64 + (long)WiredInputWait.TotalMilliseconds;
		while (!c.Session.IsWiredInputLive)
		{
			if (!ReferenceEquals(current, c) || c.Session.WiredDevice?.Path != usb.Path)
			{
				return;
			}
			if (Environment.TickCount64 > until)
			{
				Diagnostics.AppLog.Write($"wired{LogSlot}: no USB input yet, Bluetooth stays connected");
				return;
			}
			await Task.Delay(20).ConfigureAwait(false);
		}
		ulong? address;
		try
		{
			address = power.FindAddress(c.Session.Device);
		}
		catch (Exception)
		{
			address = null;
		}
		if (address is not ulong a)
		{
			Diagnostics.AppLog.Write($"wired{LogSlot}: Bluetooth address unknown, Bluetooth stays connected");
			return;
		}
		bool ok;
		try
		{
			ok = await power.DisconnectAsync(a).ConfigureAwait(false);
		}
		catch (Exception e)
		{
			Diagnostics.AppLog.Write($"wired{LogSlot}: Bluetooth disconnect failed ({e.GetType().Name}), Bluetooth stays connected");
			return;
		}
		Diagnostics.AppLog.Write(ok
			? $"wired{LogSlot}: Bluetooth disconnected, the controller runs over USB only"
			: $"wired{LogSlot}: the radio refused the Bluetooth disconnect, Bluetooth stays connected");
	}

	// A connection opened on the controller's USB link (wired mode took over a controller on the cable alone, see
	// CableControllerSource): its USB audio endpoint carries haptics and speaker from the start; a missing one is asked
	// for again like in wired mode.
	private void StartUsbOnly(Connection c, HidDevice usb)
	{
		Volatile.Write(ref c.WiredAudioMiss, null);
		Volatile.Write(ref c.WiredAudioTries, 0);
		Volatile.Write(ref c.WiredAudioStrikes, 0);
		if (OpenWiredAudioLogged(c, usb) is { } audio && c.Session.AttachWiredAudio(usb, audio))
		{
			Volatile.Write(ref c.WiredAudioSince, time.GetUtcNow().UtcTicks);
		}
		Diagnostics.AppLog.Write($"wired{LogSlot}: on (controller on the cable alone, no Bluetooth)");
		linkQuality.Restart(NowMs, "wired");
	}

	private long lastStrayDisconnect = long.MinValue;

	// The controller held on the cable alone also came up over Bluetooth (the user pressed the ø button): that link is
	// disconnected again, as the wired hand-off does (USB audio haptics do not play while Bluetooth is connected).
	// Only for this host's own controller (same address), at most every 5 s. True when the radio accepted.
	public async Task<bool> DisconnectStrayBluetoothAsync(HidDevice bluetooth)
	{
		Connection? c = current;
		if (c == null || !c.Session.IsUsbOnly || Details?.Address is not ulong mine)
		{
			return false;
		}
		ulong? address;
		try
		{
			address = power.FindAddress(bluetooth);
		}
		catch (Exception)
		{
			address = null;
		}
		long now = Environment.TickCount64;
		if (address != mine || now - Interlocked.Read(ref lastStrayDisconnect) < 5000)
		{
			return false;
		}
		Interlocked.Exchange(ref lastStrayDisconnect, now);
		bool ok;
		try
		{
			ok = await power.DisconnectAsync(mine).ConfigureAwait(false);
		}
		catch (Exception e)
		{
			Diagnostics.AppLog.Write($"wired{LogSlot}: Bluetooth disconnect failed ({e.GetType().Name})");
			return false;
		}
		Diagnostics.AppLog.Write(ok
			? $"wired{LogSlot}: the controller also came up over Bluetooth; disconnected it, it stays on USB"
			: $"wired{LogSlot}: the controller also came up over Bluetooth; the radio refused the disconnect");
		return ok;
	}

	// Back to Bluetooth over everything; a no-op while not wired.
	public void LeaveWired(string reason)
	{
		Connection? c = current;
		if (c == null || !c.Session.DetachWired(reason))
		{
			return;
		}
		Diagnostics.AppLog.Write($"wired{LogSlot}: off ({reason})");
		linkQuality.Restart(NowMs, "wired off");
		ReapplyMicForTransport(c);
	}

	// Sinks in a row lost right after opening before the open is given up on this plug.
	private const int WiredAudioStrikeLimit = 3;

	// A sink lost less than this after it attached counts as "lost right after opening" (a strike).
	private static readonly TimeSpan WiredAudioShortLife = TimeSpan.FromSeconds(10);

	// The due tick for the next wired audio/mic open; never when the interval that applies is Infinite. tries is the
	// misses in a row just counted: from WiredAudioBackoffTries on the slow interval applies.
	private long WiredAudioRetryDue(int tries = 0)
	{
		TimeSpan interval = tries >= WiredAudioBackoffTries ? WiredAudioSlowRetryInterval : WiredAudioRetryInterval;
		return interval == Timeout.InfiniteTimeSpan ? long.MaxValue : time.GetUtcNow().UtcTicks + interval.Ticks;
	}

	// The "trying again every N s" tail of a wired audio/mic miss line; empty when retries are off.
	private string WiredAudioRetryNote() => WiredAudioRetryInterval == Timeout.InfiniteTimeSpan
		? string.Empty
		: string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"; trying again every {WiredAudioRetryInterval.TotalSeconds:0.#} s while wired");

	// One open of the missing USB audio output with its bookkeeping: the miss reason is logged once while it stays
	// the same, and the next retry is always armed (at the slow interval from WiredAudioBackoffTries misses on).
	private Audio.IWiredAudioSink? OpenWiredAudioLogged(Connection c, HidDevice usb)
	{
		Audio.IWiredAudioSink? sink;
		string miss;
		try
		{
			sink = OpenWiredAudio(usb);
			miss = "no USB audio output found for this controller";
		}
		catch (Exception e)
		{
			sink = null;
			miss = "USB audio output not opened (" + Audio.WiredAudio.Describe(e) + ")";
		}
		if (sink == null)
		{
			int tries = Volatile.Read(ref c.WiredAudioTries) + 1;
			Volatile.Write(ref c.WiredAudioTries, tries);
			Volatile.Write(ref c.WiredAudioRetryAt, WiredAudioRetryDue(tries));
			if (tries == WiredAudioBackoffTries)
			{
				Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"wired{LogSlot}: USB audio output still missing after {WiredAudioBackoffTries} tries; trying every {WiredAudioSlowRetryInterval.TotalSeconds:0.#} s from now"));
			}
			if (miss != Volatile.Read(ref c.WiredAudioMiss))
			{
				Volatile.Write(ref c.WiredAudioMiss, miss);
				Diagnostics.AppLog.Write($"wired{LogSlot}: {miss} (haptics and speaker stay on Bluetooth{WiredAudioRetryNote()})");
			}
		}
		else
		{
			Volatile.Write(ref c.WiredAudioRetryAt, WiredAudioRetryDue());
		}
		return sink;
	}

	// From Monitor: while wired, a missing USB audio output or USB microphone is asked for again every
	// WiredAudioRetryInterval (slower after WiredAudioBackoffTries misses), and a USB microphone whose capture
	// gave up by itself is dropped back to Bluetooth. Core Audio calls can be slow, so the work runs off the
	// monitor pass (single-flight via WiredRetryBusy).
	private void RetryWiredAudio(Connection c)
	{
		if (c.Session.WiredDevice is not { } usb)
		{
			return;
		}
		long now = time.GetUtcNow().UtcTicks;
		// A due time of 0 is "not armed" (a transport change re-asks inside its own lock), not "due now".
		long sinkAt = Volatile.Read(ref c.WiredAudioRetryAt);
		bool sink = sinkAt != 0 && !c.Session.IsWiredAudio && Volatile.Read(ref c.WiredAudioStrikes) < WiredAudioStrikeLimit &&
			now >= sinkAt;
		bool micStopped = c.WiredMic is Audio.IWiredMicCapture { Stopped: true };
		long micAt = Volatile.Read(ref c.WiredMicRetryAt);
		bool mic = micAt != 0 && c.MicSource == MicSourceKind.Bluetooth && now >= micAt;
		if ((!sink && !micStopped && !mic) || Interlocked.Exchange(ref c.WiredRetryBusy, 1) != 0)
		{
			return;
		}
		_ = Task.Run(() =>
		{
			try
			{
				if (micStopped)
				{
					DropStoppedWiredMic(c);
				}
				if (sink)
				{
					RetryWiredSink(c, usb);
				}
				if (mic)
				{
					RetryWiredMic(c);
				}
			}
			catch (Exception e)
			{
				Diagnostics.AppLog.Write($"wired{LogSlot}: audio retry failed ({e.GetType().Name})");
			}
			finally
			{
				Volatile.Write(ref c.WiredRetryBusy, 0);
			}
		});
	}

	private void RetryWiredSink(Connection c, HidDevice usb)
	{
		if (OpenWiredAudioLogged(c, usb) is not { } sink)
		{
			return;
		}
		int tries = Volatile.Read(ref c.WiredAudioTries) + 1;
		Volatile.Write(ref c.WiredAudioSince, time.GetUtcNow().UtcTicks);
		if (!c.Session.AttachWiredAudio(usb, sink))
		{
			// The session disposed it (the link went away or a sink appeared first).
			return;
		}
		Volatile.Write(ref c.WiredAudioMiss, null);
		Volatile.Write(ref c.WiredAudioTries, 0);
		Diagnostics.AppLog.Write($"wired{LogSlot}: USB audio output opened on try {tries}");
	}

	private sealed partial class Connection
	{
		// When the next open of a missing USB audio output is due (GetUtcNow ticks).
		public long WiredAudioRetryAt;

		// Why the last audio open gave nothing: the line is logged once per wired enter while the reason stays.
		public string? WiredAudioMiss;

		// Audio opens in a row that gave nothing.
		public int WiredAudioTries;

		// When the current/last audio sink was attached (GetUtcNow ticks).
		public long WiredAudioSince;

		// Sinks in a row lost right after opening.
		public int WiredAudioStrikes;

		// Single-flight flag of the monitor's wired audio/mic retry task.
		public int WiredRetryBusy;

		// The session fell back to Bluetooth by itself (a failed USB read or write): logged, and the link quality
		// monitor starts over like after a reconnect.
		private void OnWiredLost(string reason)
		{
			Diagnostics.AppLog.Write($"wired{host.LogSlot}: lost ({reason}), back on Bluetooth");
			host.linkQuality.Restart(host.NowMs, "wired lost");
			// This can be raised from inside the very output write ApplyMic is doing: the mic re-apply runs on another
			// thread, and only while this is still the running connection.
			_ = Task.Run(() =>
			{
				try
				{
					if (ReferenceEquals(host.current, this))
					{
						host.ReapplyMicForTransport(this);
					}
				}
				catch (Exception e)
				{
					Diagnostics.AppLog.Write($"wired{host.LogSlot}: mic not re-applied ({e.GetType().Name})");
				}
			});
		}

		// The wired audio endpoint failed; haptics and speaker went back to Bluetooth (the wired link stays). A sink
		// lost right after it attached counts a strike: at WiredAudioStrikeLimit in a row it is not asked for again
		// on this plug. Raised on the haptics thread — stays lock-free.
		private void OnWiredAudioLost(string reason)
		{
			long now = host.time.GetUtcNow().UtcTicks;
			int strikes = now - Volatile.Read(ref WiredAudioSince) < BridgeHost.WiredAudioShortLife.Ticks
				? Volatile.Read(ref WiredAudioStrikes) + 1 : 0;
			Volatile.Write(ref WiredAudioStrikes, strikes);
			Volatile.Write(ref WiredAudioRetryAt, host.WiredAudioRetryDue());
			if (strikes >= BridgeHost.WiredAudioStrikeLimit)
			{
				Diagnostics.AppLog.Write($"wired{host.LogSlot}: audio lost ({reason}), haptics and speaker back on Bluetooth; lost {BridgeHost.WiredAudioStrikeLimit} times right after opening, not opened again on this plug");
			}
			else
			{
				Diagnostics.AppLog.Write($"wired{host.LogSlot}: audio lost ({reason}), haptics and speaker back on Bluetooth; opening it again");
			}
		}
	}
}
