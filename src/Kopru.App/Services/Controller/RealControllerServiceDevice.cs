// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Hid;
using Kopru.Core.Hiding;
using Kopru.Core.Localization;

namespace Kopru.App.Services;

// Controller details (0x20 firmware info, 0x09 address), "connected for", Reconnect and the USB <-> Bluetooth switch.
// DeviceTick runs on every presence tick (RefreshPresence). Bluetooth details come from BridgeHost (read by the bridge
// on connect, or inside the battery probe's read-only open while it is stopped); USB ones are read here once per plug.
//
// Matching the cable and the Bluetooth controller: a USB DualSense is opened read-only and its feature report 0x09
// (bytes 1..6, LSB first) gives its Bluetooth address. Which set decides what: a USB twin is hidden only for an
// address whose Bluetooth link a running bridge holds right now (0x09 read over Bluetooth, BridgeHost.Details); the
// Windows paired list (BTHENUM / BluetoothFindFirstDevice, HealthEnvironment.Paired) counts only for the stop when
// Bluetooth is lost. Our virtual pads (slots 1-4) answer 0x09 with a virtual address (PairingInfo.IsVirtual) and are
// ignored.
//
// The cable only charges (CableMode.ChargeOnly): per bridged controller the CableSwitch gives the wanted state each
// tick — the USB twin of a bridged-and-cabled controller should be hidden — and the service reconciles the actual
// hiding with it (retrying a failure). With CableMode.Wired (EXPERIMENTAL) the bridge then also talks to the
// controller over the hidden USB link. Only when Bluetooth drops while a known controller stays on the cable for the
// whole delay does the bridge stop (debounced); it resumes once Bluetooth is back, the cable may still be in.
public sealed partial class RealControllerService
{
	public static readonly TimeSpan ReopenWait = TimeSpan.FromSeconds(3);
	// How long a change must hold before it is acted on (CableSwitch): a little over two presence ticks, so a USB or
	// Bluetooth listing that drops out for one tick while the cable settles does not unhide, stop or resume.
	public static readonly TimeSpan CableResumeDelay = TimeSpan.FromSeconds(5);
	// How often a failed hide or unhide of a USB twin is tried again; also how often a USB pad that did not answer
	// 0x09 is asked again.
	public static readonly TimeSpan CableRetryInterval = TimeSpan.FromSeconds(5);

	private readonly object deviceGate = new();
	private readonly Dictionary<string, ControllerDetails?> usbDetails = new(StringComparer.OrdinalIgnoreCase);
	// Listed USB pads that did not answer 0x09 yet -> the earliest next read (only an answer is kept in usbDetails).
	private readonly Dictionary<string, DateTimeOffset> usbReadRetryAt = new(StringComparer.OrdinalIgnoreCase);
	private readonly CableSwitch cable = new(CableResumeDelay);
	private readonly object cableGate = new();
	private string? connectedKey;
	private DateTimeOffset? connectedSince;
	private ulong? lastBluetoothAddress;
	// What IsWired showed at the last tick: wired starting or ending (the reconcile or a session's own fallback) is a
	// state change the connection text shows.
	private bool lastWired;
	// The USB twins that should be hidden now (address -> the listed pad, or null while its listing is out for a tick),
	// published every tick; what is actually hidden, and when a failed hide or unhide may be tried again (twinGate).
	private IReadOnlyDictionary<ulong, HidDevice?> wantedTwins = new Dictionary<ulong, HidDevice?>();
	// The wired links wanted now (CableMode.Wired: address -> the listed USB pad and the host bridging it), published
	// with wantedTwins under cableGate; the reconcile enters them only once the twin is hidden on the same path.
	// Listed is false for an entry carried over a one-tick listing gap: it keeps a running wired link but is never
	// an enter candidate.
	private IReadOnlyDictionary<ulong, (HidDevice Usb, BridgeHost Host, bool Listed)> wantedWired =
		new Dictionary<ulong, (HidDevice, BridgeHost, bool)>();
	// Why a wired host without a wanted entry leaves, published with wantedWired (the reconcile passes it on).
	private IReadOnlyDictionary<BridgeHost, string> wiredLeaveReasons = new Dictionary<BridgeHost, string>();
	private readonly object twinGate = new();
	private readonly Dictionary<ulong, (string Path, string Id)> hiddenTwins = [];
	// Self-fallback bookkeeping (twinGate): the USB path a reconcile entered wired on per address (and since when it
	// has been wired there without a break), how often the session fell back by itself with the cable still in, and
	// the addresses given up on this plug. All reset when the twin leaves CableSwitch.HiddenTwins (the cable stayed
	// out), when the mode is toggled, and on a bridge stop. The entered record, the count and the given-up flag also
	// start over on a tick whose listing lacks the USB pad or the Bluetooth link (a fallback then came from the link
	// going away, and a quick replug or Bluetooth return is a fresh start), and the count after WiredStableReset of
	// continuous wired.
	private readonly Dictionary<ulong, (string Path, DateTimeOffset Since)> wiredEntered = [];
	private readonly Dictionary<ulong, int> wiredFallbacks = [];
	private readonly HashSet<ulong> wiredGivenUp = [];
	// Self-fallbacks before wired mode is given up for this plug.
	public const int WiredFallbackLimit = 3;
	// Continuous wired operation after which earlier self-fallbacks on this plug are forgotten.
	public static readonly TimeSpan WiredStableReset = TimeSpan.FromSeconds(30);
	private DateTimeOffset twinRetryAt = DateTimeOffset.MinValue;
	private int reconciling;
	// The reconcile flight is running; tests wait for it to end before their next step.
	internal bool IsReconcilingTwins => Volatile.Read(ref reconciling) != 0;
	private string? lastTwinProblem;
	private string? twinWarning;

	// Opens a USB DualSense for feature reads only (tests swap it).
	public Func<HidDevice, IHidLink?> OpenUsbReadOnly { get; set; } = d => WindowsHid.OpenReadOnly(d);

	public bool IsPausedForCable => cable.PausedForCable;

	// What the bridge does while the bridged controller is also on a USB cable (CableSwitch.Mode). ChargeOnly is the
	// default; Wired (EXPERIMENTAL) takes effect on the next tick.
	public CableMode CableMode
	{
		get
		{
			lock (cableGate)
			{
				return cable.Mode;
			}
		}
		set
		{
			lock (cableGate)
			{
				cable.Mode = value;
			}
			lock (twinGate)
			{
				// A mode toggle starts the self-fallback count over.
				wiredEntered.Clear();
				wiredFallbacks.Clear();
				wiredGivenUp.Clear();
			}
		}
	}

	// Wired mode: the running bridge talks to the selected controller over its USB link (Bluetooth stays connected).
	public bool IsWired => Selected.IsWired;

	// The selected controller is on Bluetooth and its USB cable is plugged in too: the cable only charges, the bridge
	// keeps running on Bluetooth and the USB twin is (being) hidden.
	public bool CablePluggedIn => Selected.Details is { Transport: Transport.Bluetooth, Address: { } a } &&
		Volatile.Read(ref wantedTwins).ContainsKey(a);

	public ControllerDetails? Details
	{
		get
		{
			ControllerDetails? held = Selected.Details;
			if (Device is { } d && held != null && string.Equals(held.Path, d.Path, StringComparison.OrdinalIgnoreCase))
			{
				return held;
			}
			lock (deviceGate)
			{
				return usbDetails.Values.FirstOrDefault(IsRealController);
			}
		}
	}

	public DateTimeOffset? ConnectedSince
	{
		get
		{
			lock (deviceGate)
			{
				return connectedSince;
			}
		}
	}

	public Task ReconnectAsync() => ReconnectSequence.RunAsync(started, () => StopBridgeAsync("reconnect controller"), ReopenAsync,
		() => StartBridgeAsync("reconnect controller"));

	// With the bridge stopped: forgets what was read and opens the controller again (the battery probe's read-only open
	// reads battery and details afresh), then waits for that read to finish.
	private async Task ReopenAsync()
	{
		await WaitForProbeAsync().ConfigureAwait(false);
		host.ForgetDetails();
		lock (deviceGate)
		{
			usbDetails.Clear();
			usbReadRetryAt.Clear();
		}
		present = null;
		lastBatteryProbe = null;
		RefreshPresence();
		await WaitForProbeAsync().ConfigureAwait(false);
		Raise();
	}

	private async Task WaitForProbeAsync()
	{
		long until = Environment.TickCount64 + (long)ReopenWait.TotalMilliseconds;
		while (IsReadingBattery && Environment.TickCount64 < until)
		{
			await Task.Delay(20).ConfigureAwait(false);
		}
	}

	private void OnDetailsRead(ControllerDetails read)
	{
		if (read.Transport == Transport.Bluetooth && read.Address is { } a)
		{
			lastBluetoothAddress = a;
		}
		Raise();
	}

	private static bool IsRealController(ControllerDetails? d) => d is { Address: { } a } && !PairingInfo.IsVirtual(a);

	// Reads USB details once per plug, tracks "connected since" and decides the cable switch.
	public void DeviceTick() => DeviceTick(ListControllers());

	// The controllers Windows lists now (empty when the listing fails).
	private IReadOnlyList<HidDevice> ListControllers()
	{
		try
		{
			return [.. finder.Find()];
		}
		catch (Exception)
		{
			return [];
		}
	}

	private void DeviceTick(IReadOnlyList<HidDevice> all)
	{
		if (Volatile.Read(ref disposed) != 0)
		{
			return;
		}
		DateTimeOffset now = time.GetUtcNow();
		HidDevice? bt = all.FirstOrDefault(d => d.Transport == Transport.Bluetooth) ?? (host.Device is { Transport: Transport.Bluetooth } h ? h : null);
		bool changed = false;
		List<(HidDevice Device, ulong Address)> usbPads = [];
		string? usbKey = null;
		foreach (HidDevice usb in all.Where(d => d.Transport == Transport.Usb))
		{
			ControllerDetails? read;
			bool known;
			lock (deviceGate)
			{
				known = usbDetails.TryGetValue(usb.Path, out read);
			}
			if (!known)
			{
				bool due;
				lock (deviceGate)
				{
					due = !usbReadRetryAt.TryGetValue(usb.Path, out DateTimeOffset retryAt) || now >= retryAt;
				}
				if (!due)
				{
					continue;
				}
				read = ReadUsb(usb);
				lock (deviceGate)
				{
					// Only an answer is kept for the life of the plug; a pad that does not answer 0x09 yet (the cable
					// still settling) is asked again, at most every CableRetryInterval.
					if (read?.Address != null)
					{
						usbDetails[usb.Path] = read;
						usbReadRetryAt.Remove(usb.Path);
					}
					else
					{
						usbReadRetryAt[usb.Path] = now + CableRetryInterval;
					}
				}
				changed |= IsRealController(read);
			}
			if (IsRealController(read))
			{
				usbPads.Add((usb, read!.Address!.Value));
				usbKey ??= usb.Path;
			}
		}
		bool newConnection = false;
		lock (deviceGate)
		{
			foreach (string gone in usbDetails.Keys.Where(p => !all.Any(d => string.Equals(d.Path, p, StringComparison.OrdinalIgnoreCase))).ToList())
			{
				changed |= IsRealController(usbDetails[gone]);
				usbDetails.Remove(gone);
			}
			foreach (string gone in usbReadRetryAt.Keys.Where(p => !all.Any(d => string.Equals(d.Path, p, StringComparison.OrdinalIgnoreCase))).ToList())
			{
				usbReadRetryAt.Remove(gone);
			}
			string? key = bt?.Path ?? usbKey;
			if (key != connectedKey)
			{
				newConnection = connectedKey == null && key != null;
				connectedKey = key;
				connectedSince = key == null ? null : time.GetUtcNow();
			}
		}
		if (newConnection && host.Device == null)
		{
			// Read the details again for this connection (the next battery probe does it).
			host.ForgetDetails();
		}
		List<ulong> ours = KnownAddresses();
		// Bluetooth counts as up only while Windows lists it: the bridge may still hold a link that just dropped.
		bool bluetoothListed = all.Any(d => d.Transport == Transport.Bluetooth);
		List<(ulong Address, BridgeHost Host)> bridged = BridgedHosts(all);
		CableDecision decision;
		Dictionary<ulong, HidDevice?> wanted = [];
		Dictionary<ulong, (HidDevice Usb, BridgeHost Host, bool Listed)> wired = [];
		Dictionary<BridgeHost, string> leaveReasons = [];
		IReadOnlyDictionary<ulong, (HidDevice Usb, BridgeHost Host, bool Listed)> wiredBefore;
		IReadOnlyDictionary<ulong, HidDevice?> before;
		bool keysChanged;
		lock (cableGate)
		{
			decision = cable.Next(started, usbPads.Select(p => p.Address).ToList(), bridged.Select(b => b.Address).ToList(), ours, bluetoothListed, now);
			HashSet<ulong> givenUp;
			lock (twinGate)
			{
				// The self-fallback bookkeeping lives for one plug: a cable that stayed out (its twin left
				// HiddenTwins after the delay) starts over.
				HashSet<ulong> hiddenNow = [.. cable.HiddenTwins];
				foreach (ulong a in wiredEntered.Keys.Where(k => !hiddenNow.Contains(k)).ToList())
				{
					wiredEntered.Remove(a);
				}
				foreach (ulong a in wiredFallbacks.Keys.Where(k => !hiddenNow.Contains(k)).ToList())
				{
					wiredFallbacks.Remove(a);
				}
				wiredGivenUp.RemoveWhere(k => !hiddenNow.Contains(k));
				givenUp = [.. wiredGivenUp];
			}
			// What should be hidden now: the listed USB pad for each wanted twin (null while its listing is out for a
			// tick: whatever is hidden for it is kept meanwhile).
			foreach (ulong a in cable.HiddenTwins)
			{
				wanted[a] = usbPads.FirstOrDefault(p => p.Address == a).Device;
			}
			// CableMode.Wired: the listed USB pad and its bridging host for each twin wanted wired this tick. A
			// one-tick listing gap keeps the previous entry (not listed): WiredTwins keeps the address for one
			// tick and a running wired link must not leave on it. Given-up addresses get no entry: charge only.
			wiredBefore = Volatile.Read(ref wantedWired);
			foreach (ulong a in cable.WiredTwins)
			{
				if (givenUp.Contains(a))
				{
					continue;
				}
				if (usbPads.FirstOrDefault(p => p.Address == a).Device is { } usb &&
					bridged.FirstOrDefault(b => b.Address == a).Host is { } bridgeHost)
				{
					wired[a] = (usb, bridgeHost, true);
				}
				else if (wiredBefore.TryGetValue(a, out (HidDevice Usb, BridgeHost Host, bool Listed) prev))
				{
					wired[a] = (prev.Usb, prev.Host, false);
				}
			}
			// Why each wired host without a wanted entry leaves (the reconcile passes it to LeaveWired).
			foreach (BridgeHost wiredHost in multi?.Hosts ?? [host])
			{
				if (!wiredHost.IsWired || wired.Any(kv => ReferenceEquals(kv.Value.Host, wiredHost) &&
						wiredHost.WiredDevice is { } wd && string.Equals(wd.Path, kv.Value.Usb.Path, StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}
				ulong? a = wiredHost.Details?.Address;
				leaveReasons[wiredHost] = !started ? "bridge stopped"
					: cable.Mode != CableMode.Wired ? "charge only chosen"
					: a is { } ga && givenUp.Contains(ga) ? "wired mode given up for this plug"
					: a is not { } pa || !usbPads.Any(p => p.Address == pa) ? "usb pad not listed"
					: !bridged.Any(b => ReferenceEquals(b.Host, wiredHost)) ? "bluetooth link not held"
					: "twin no longer hidden";
			}
			// Published under the same gate as Next: two concurrent ticks then decide and publish in the same order,
			// so a stale tick's wanted set can never overwrite a newer one.
			before = Volatile.Read(ref wantedTwins);
			Volatile.Write(ref wantedTwins, wanted);
			Volatile.Write(ref wantedWired, wired);
			Volatile.Write(ref wiredLeaveReasons, leaveReasons);
			keysChanged = !before.Keys.ToHashSet().SetEquals(wanted.Keys);
		}
		changed |= keysChanged;
		// A new wanted set or a new target (the cable moved to another port) is tried at once, not after a retry wait.
		if (keysChanged || wanted.Any(kv => kv.Value is { } t &&
			(!before.TryGetValue(kv.Key, out HidDevice? p) || !string.Equals(p?.Path, t.Path, StringComparison.OrdinalIgnoreCase))))
		{
			lock (twinGate)
			{
				twinRetryAt = DateTimeOffset.MinValue;
			}
		}
		// A listed wired entry whose host is not wired on the path the reconcile entered any more: the session fell
		// back to Bluetooth by itself with the cable still in. Retried after the interval; three strikes on one
		// plug gives up (the twin stays hidden: charge-only behaviour).
		lock (twinGate)
		{
			// The USB pad not listed or the Bluetooth link gone on this tick: a session that fell back did so because
			// a link went away (the cable pulled, Bluetooth dropped), not by itself — no strike, and the count starts
			// over (a quick replug or Bluetooth return within the delay must not count against the plug). A given-up
			// address is also let go: "until the cable is replugged" ends here.
			foreach (ulong a in wiredEntered.Keys.Concat(wiredFallbacks.Keys).Concat(wiredGivenUp).Distinct()
				.Where(k => !usbPads.Any(p => p.Address == k) || !bridged.Any(b => b.Address == k)).ToList())
			{
				wiredEntered.Remove(a);
				wiredFallbacks.Remove(a);
				wiredGivenUp.Remove(a);
			}
			foreach ((ulong a, (HidDevice Usb, BridgeHost Host, bool Listed) e) in wired)
			{
				if (!e.Listed)
				{
					continue;
				}
				if (!wiredEntered.TryGetValue(a, out (string Path, DateTimeOffset Since) entered))
				{
					// Still wired on the listed path although its record was dropped (a listing gap the link rode
					// out): tracked again from now.
					if (e.Host.WiredDevice is { } kept && string.Equals(kept.Path, e.Usb.Path, StringComparison.OrdinalIgnoreCase))
					{
						wiredEntered[a] = (e.Usb.Path, now);
					}
					continue;
				}
				if (e.Host.WiredDevice is { } wd && string.Equals(wd.Path, entered.Path, StringComparison.OrdinalIgnoreCase))
				{
					// Wired without a break for WiredStableReset: earlier self-fallbacks on this plug are forgotten.
					if (now - entered.Since >= WiredStableReset)
					{
						wiredFallbacks.Remove(a);
					}
					continue;
				}
				wiredEntered.Remove(a);
				int fallbacks = (wiredFallbacks.TryGetValue(a, out int f) ? f : 0) + 1;
				wiredFallbacks[a] = fallbacks;
				twinRetryAt = now + CableRetryInterval;
				if (fallbacks >= WiredFallbackLimit && wiredGivenUp.Add(a))
				{
					AppLog.Write("cable: wired mode fell back to Bluetooth 3 times on this plug; staying on Bluetooth until the cable is replugged or the mode is toggled");
				}
			}
		}
		switch (decision)
		{
			case CableDecision.StopForCable:
				_ = Task.Run(async () =>
				{
					await StopBridgeAsync("cable: Bluetooth lost while the controller is on a USB cable; games use the wired pad").ConfigureAwait(false);
					RaiseNotice(new ServiceNotice(ServiceNoticeKind.CableConnected));
				});
				break;
			case CableDecision.Resume:
				_ = Task.Run(() => StartBridgeAsync("cable: controller back on Bluetooth"));
				break;
		}
		if (decision != CableDecision.StopForCable)
		{
			bool twinPending;
			lock (twinGate)
			{
				twinPending = now >= twinRetryAt && TwinPending();
			}
			if (twinPending && Interlocked.CompareExchange(ref reconciling, 1, 0) == 0)
			{
				_ = Task.Run(ReconcileTwinsAsync);
			}
		}
		// A warning a failed hide set does not outlive the cable: cleared once nothing is wanted or hidden anymore.
		if (wanted.Count == 0)
		{
			bool clear;
			lock (twinGate)
			{
				clear = hiddenTwins.Count == 0;
			}
			if (clear)
			{
				ClearTwinWarning();
			}
		}
		// Wired mode started or ended since the last tick (the reconcile flight or a session's own fallback).
		bool wiredNow = IsWired;
		changed |= wiredNow != lastWired;
		lastWired = wiredNow;
		if (changed)
		{
			Raise();
		}
	}

	// The (address, host) pairs a running bridge holds right now and Windows still lists: only these decide which USB
	// twin gets hidden (a paired but not bridged controller on USB never does) or wired.
	private List<(ulong Address, BridgeHost Host)> BridgedHosts(IReadOnlyList<HidDevice> all)
	{
		List<(ulong, BridgeHost)> bridged = [];
		foreach (BridgeHost h in multi?.Hosts ?? [host])
		{
			if (h.Device is { Transport: Transport.Bluetooth } d &&
				h.Details is { Transport: Transport.Bluetooth, Address: { } a, Path: { } p } &&
				string.Equals(p, d.Path, StringComparison.OrdinalIgnoreCase) &&
				!PairingInfo.IsVirtual(a) &&
				all.Any(l => string.Equals(l.Path, d.Path, StringComparison.OrdinalIgnoreCase)))
			{
				bridged.Add((a, h));
			}
		}
		return bridged;
	}

	// A hidden twin that is no longer wanted (or now on another port), a wanted pad not yet hidden, a host wired
	// without a matching wanted entry, or a wanted wired entry not entered yet. twinGate held.
	private bool TwinPending()
	{
		IReadOnlyDictionary<ulong, HidDevice?> wanted =
			started ? Volatile.Read(ref wantedTwins) : new Dictionary<ulong, HidDevice?>();
		bool pending = hiddenTwins.Any(kv => !wanted.TryGetValue(kv.Key, out HidDevice? t) ||
				(t is { } tt && !string.Equals(tt.Path, kv.Value.Path, StringComparison.OrdinalIgnoreCase))) ||
			wanted.Any(kv => kv.Value != null && !hiddenTwins.ContainsKey(kv.Key));
		if (pending)
		{
			return true;
		}
		IReadOnlyDictionary<ulong, (HidDevice Usb, BridgeHost Host, bool Listed)> wired =
			started ? Volatile.Read(ref wantedWired) : new Dictionary<ulong, (HidDevice, BridgeHost, bool)>();
		// A session that fell back to Bluetooth by itself is re-entered later: its wanted entry stays pending.
		return (multi?.Hosts ?? [host]).Any(h => h.IsWired &&
				!wired.Any(kv => ReferenceEquals(kv.Value.Host, h) && h.WiredDevice is { } wd &&
					string.Equals(wd.Path, kv.Value.Usb.Path, StringComparison.OrdinalIgnoreCase))) ||
			wired.Any(WiredPending);
	}

	// True while this wanted wired entry is ready to enter but its host is not wired on that path: the twin is
	// hidden on the same USB path (the bridge only switches to a link games cannot see). An entry carried over a
	// listing gap (Listed false) is never an enter candidate. twinGate held.
	private bool WiredPending(KeyValuePair<ulong, (HidDevice Usb, BridgeHost Host, bool Listed)> entry) =>
		entry.Value.Listed &&
		hiddenTwins.TryGetValue(entry.Key, out (string Path, string Id) rec) &&
		string.Equals(rec.Path, entry.Value.Usb.Path, StringComparison.OrdinalIgnoreCase) &&
		(entry.Value.Host.WiredDevice is not { } wd ||
			!string.Equals(wd.Path, entry.Value.Usb.Path, StringComparison.OrdinalIgnoreCase));

	// Reconciles what is hidden with what the last tick wanted (wantedTwins): unhides twins no longer wanted or moved
	// to another port, hides the wanted ones still visible. Under bridgeGate so it never lands in a session a start or
	// stop is still changing; a failure is tried again after CableRetryInterval. Loops while a later tick changed what
	// is wanted mid-pass, so no wanted state is skipped. Fire-and-forget, so it never throws; the flight flag is
	// released exactly once, inside the twinGate hold that also checks for pending work.
	private async Task ReconcileTwinsAsync()
	{
		for (; ; )
		{
			bool failed = false;
			bool wiredChanged = false;
			// An enter was tried this pass, and the wanted wired links it saw (checked again at the end of the pass).
			bool enterTried = false;
			IReadOnlyDictionary<ulong, (HidDevice Usb, BridgeHost Host, bool Listed)> wiredSeen =
				new Dictionary<ulong, (HidDevice, BridgeHost, bool)>();
			try
			{
				await bridgeGate.WaitAsync().ConfigureAwait(false);
				try
				{
					// Read after taking the gate: a start, stop or later tick may have changed what is wanted.
					IReadOnlyDictionary<ulong, HidDevice?> wanted =
						started ? Volatile.Read(ref wantedTwins) : new Dictionary<ulong, HidDevice?>();
					IReadOnlyDictionary<ulong, (HidDevice Usb, BridgeHost Host, bool Listed)> wired =
						started ? Volatile.Read(ref wantedWired) : new Dictionary<ulong, (HidDevice, BridgeHost, bool)>();
					IReadOnlyDictionary<BridgeHost, string> leaveReasons = Volatile.Read(ref wiredLeaveReasons);
					wiredSeen = wired;
					// Wired first: the bridge is back on Bluetooth before its twin is unhidden. A leave done by the
					// reconcile is not a self-fallback: its entered record is removed.
					foreach (BridgeHost h in multi?.Hosts ?? [host])
					{
						if (h.IsWired && !(started && wired.Any(kv => ReferenceEquals(kv.Value.Host, h) &&
								h.WiredDevice is { } wd && string.Equals(wd.Path, kv.Value.Usb.Path, StringComparison.OrdinalIgnoreCase))))
						{
							h.LeaveWired(leaveReasons.TryGetValue(h, out string? reason) ? reason : "twin no longer hidden");
							wiredChanged = true;
							lock (twinGate)
							{
								if (h.Details?.Address is { } left)
								{
									wiredEntered.Remove(left);
								}
							}
						}
					}
					List<KeyValuePair<ulong, (string Path, string Id)>> hidden;
					lock (twinGate)
					{
						hidden = [.. hiddenTwins];
					}
					foreach ((ulong address, (string path, string id)) in hidden)
					{
						if (wanted.TryGetValue(address, out HidDevice? target) &&
							(target == null || string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase)))
						{
							continue;
						}
						await hiding.UnhideUsbAsync(id).ConfigureAwait(false);
						lock (twinGate)
						{
							hiddenTwins.Remove(address);
						}
						AppLog.Write("cable: USB cable removed or Bluetooth link lost; USB instance unhidden");
					}
					foreach ((ulong address, HidDevice? target) in wanted)
					{
						if (target == null)
						{
							continue;
						}
						bool done;
						lock (twinGate)
						{
							done = hiddenTwins.ContainsKey(address);
						}
						if (!done)
						{
							failed |= !await HideTwinAsync(address, target).ConfigureAwait(false);
						}
					}
					// The USB pad is always hidden before the bridge switches to it.
					foreach (KeyValuePair<ulong, (HidDevice Usb, BridgeHost Host, bool Listed)> entry in wired)
					{
						bool enter;
						lock (twinGate)
						{
							enter = WiredPending(entry);
						}
						if (!enter)
						{
							continue;
						}
						enterTried = true;
						if (entry.Value.Host.EnterWired(entry.Value.Usb) is { } problem)
						{
							failed = true;
							TwinProblem("cable: wired mode not entered (" + problem + ")");
						}
						else
						{
							// Recorded even when the session already fell back (during the enter's own mic re-apply,
							// say): the next tick then counts it as a self-fallback strike.
							lock (twinGate)
							{
								wiredEntered[entry.Key] = (entry.Value.Usb.Path, time.GetUtcNow());
							}
							wiredChanged = true;
							if (entry.Value.Host.WiredDevice is { } held &&
								string.Equals(held.Path, entry.Value.Usb.Path, StringComparison.OrdinalIgnoreCase))
							{
								lastTwinProblem = null;
								AppLog.Write("cable: wired mode, controller input and output over USB; Bluetooth stays connected");
							}
							else
							{
								// Not wired any more right after the enter: a failure (the retry interval applies).
								failed = true;
								TwinProblem("cable: wired mode fell back to Bluetooth right after it was entered");
							}
						}
					}
				}
				finally
				{
					bridgeGate.Release();
				}
				if (!failed)
				{
					ClearTwinWarning();
				}
			}
			catch (Exception e)
			{
				failed = true;
				try
				{
					if (e is HidHideException or ArgumentException)
					{
						string text = Text.HidingFailed(e.Message);
						Volatile.Write(ref twinWarning, text);
						SetHidingWarning(text);
					}
					// No e.Message: it can carry the USB instance id of the CLI arguments or a path with the user name.
					TwinProblem("cable: hiding or unhiding the USB instance failed (" + e.GetType().Name + ")");
				}
				catch (Exception)
				{
					// A notification that fails itself (an event subscriber throwing through Raise or AppLog.Written)
					// must not strand the flight flag; the retry interval still applies.
				}
			}
			bool finished;
			lock (twinGate)
			{
				// Done, or wait for the retry interval after a failure: leave the flight only when nothing is
				// pending anymore — a tick that found the flight busy relies on this check picking up its want.
				// Freeing the flight inside the lock keeps a pending tick from slipping between the check and
				// the free (its CompareExchange would fail and the work would be lost).
				// A wired change was tried and a wanted wired link is still not entered (the session fell back during
				// or right after the enter): a failure, never an immediate re-enter. A success keeps a retry delay a
				// tick set meanwhile for a self-fallback (it is about that link, not this pass) and leaves the rest
				// to the first tick after it.
				DateTimeOffset end = time.GetUtcNow();
				failed |= (enterTried || wiredChanged) && wiredSeen.Any(WiredPending);
				if (failed)
				{
					twinRetryAt = end + CableRetryInterval;
				}
				else if (twinRetryAt <= end)
				{
					twinRetryAt = DateTimeOffset.MinValue;
				}
				finished = failed || end < twinRetryAt || !TwinPending();
				if (finished)
				{
					Volatile.Write(ref reconciling, 0);
				}
			}
			// Wired was entered or left: outside the gates, and a throwing subscriber must not strand the flight.
			if (wiredChanged)
			{
				try
				{
					Raise();
				}
				catch (Exception)
				{
				}
			}
			if (finished)
			{
				return;
			}
		}
	}

	// Hides the USB twin of one bridged-and-cabled controller (from ReconcileTwinsAsync, under bridgeGate). True when
	// it was hidden and recorded.
	private async Task<bool> HideTwinAsync(ulong address, HidDevice usb)
	{
		bool NotHidden(string reason)
		{
			TwinProblem("cable: controller charging over USB, staying on Bluetooth; USB instance not hidden (" + reason + ")");
			return false;
		}
		if (!hiding.IsInstalled)
		{
			return NotHidden("HidHide is not installed");
		}
		// Asked again right before HidHide rather than trusted from the tick's cached read: the wired pad and our
		// virtual pad share the instance id shape, and the pad behind this path could have been replugged since.
		ulong? answer = ReadUsb(usb)?.Address;
		if (answer == null || PairingInfo.IsVirtual(answer.Value) || answer.Value != address)
		{
			return NotHidden("it does not answer as the bridged controller");
		}
		string? id = instanceId(usb);
		if (id == null)
		{
			return NotHidden("no USB instance id");
		}
		if (!await hiding.HideUsbAsync(id).ConfigureAwait(false))
		{
			return NotHidden("no hiding session");
		}
		lock (twinGate)
		{
			hiddenTwins[address] = (usb.Path, id);
		}
		lastTwinProblem = null;
		AppLog.Write("cable: controller charging over USB, staying on Bluetooth; USB instance hidden");
		return true;
	}

	// Writes a USB twin problem once (a retry after CableRetryInterval must not repeat it); a success clears it.
	private void TwinProblem(string? line)
	{
		if (line == lastTwinProblem)
		{
			return;
		}
		lastTwinProblem = line;
		if (line != null)
		{
			AppLog.Write(line);
		}
	}

	// Clears the hiding warning a failed twin hide or unhide set, unless another path replaced it meanwhile.
	private void ClearTwinWarning()
	{
		if (Interlocked.Exchange(ref twinWarning, null) is not { } mine)
		{
			return;
		}
		bool cleared;
		lock (gate)
		{
			cleared = hidingWarning == mine;
			if (cleared)
			{
				hidingWarning = string.Empty;
			}
		}
		if (cleared)
		{
			Raise();
		}
	}

	// Our controller's Bluetooth addresses: read over Bluetooth (bridge or probe) and the Windows paired list.
	private List<ulong> KnownAddresses()
	{
		List<ulong> known = [.. (lastEnvironment?.Paired ?? []).Select(p => p.Address)];
		if (host.Details is { Transport: Transport.Bluetooth, Address: { } a })
		{
			known.Add(a);
		}
		if (lastBluetoothAddress is { } last)
		{
			known.Add(last);
		}
		known.RemoveAll(PairingInfo.IsVirtual);
		return known;
	}

	private ControllerDetails? ReadUsb(HidDevice device)
	{
		try
		{
			using IHidLink? link = OpenUsbReadOnly(device);
			return link == null ? null : ControllerDetails.Read(link);
		}
		catch (Exception)
		{
			return null;
		}
	}
}
