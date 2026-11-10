// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Hid;

namespace Kopru.App.Services;

// Controller details (0x20 firmware info, 0x09 address), "connected for", Reconnect and the USB <-> Bluetooth switch.
// DeviceTick runs on every presence tick (RefreshPresence). Bluetooth details come from BridgeHost (read by the bridge
// on connect, or inside the battery probe's read-only open while it is stopped); USB ones are read here once per plug.
//
// Matching the cable and the Bluetooth controller: a USB DualSense is opened read-only and its feature report 0x09
// (bytes 1..6, LSB first) gives its Bluetooth address. It is the bridged controller when that address equals the
// address read over Bluetooth (0x09 again, BridgeHost.Details) or the address of a paired DualSense in the Windows list
// (BTHENUM / BluetoothFindFirstDevice, HealthEnvironment.Paired). The virtual pad answers 0x09 with
// PairingInfo.VirtualAddress and is ignored.
public sealed partial class RealControllerService
{
	public static readonly TimeSpan ReopenWait = TimeSpan.FromSeconds(3);

	private readonly object deviceGate = new();
	private readonly Dictionary<string, ControllerDetails?> usbDetails = new(StringComparer.OrdinalIgnoreCase);
	private readonly CableSwitch cable = new();
	private string? connectedKey;
	private DateTimeOffset? connectedSince;
	private ulong? lastBluetoothAddress;

	// Opens a USB DualSense for feature reads only (tests swap it).
	public Func<HidDevice, IHidLink?> OpenUsbReadOnly { get; set; } = d => WindowsHid.OpenReadOnly(d);

	public bool IsPausedForCable => cable.PausedForCable;

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

	private static bool IsRealController(ControllerDetails? d) => d is { Address: { } a } && a != PairingInfo.VirtualAddress;

	// Reads USB details once per plug, tracks "connected since" and decides the cable switch.
	public void DeviceTick()
	{
		if (Volatile.Read(ref disposed) != 0)
		{
			return;
		}
		List<HidDevice> all;
		try
		{
			all = [.. finder.Find()];
		}
		catch (Exception)
		{
			all = [];
		}
		HidDevice? bt = all.FirstOrDefault(d => d.Transport == Transport.Bluetooth) ?? (host.Device is { Transport: Transport.Bluetooth } h ? h : null);
		bool changed = false;
		List<ulong> usbAddresses = [];
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
				read = ReadUsb(usb);
				lock (deviceGate)
				{
					usbDetails[usb.Path] = read;
				}
				changed |= IsRealController(read);
			}
			if (IsRealController(read))
			{
				usbAddresses.Add(read!.Address!.Value);
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
		CableDecision decision = cable.Next(started, usbAddresses, KnownAddresses(), bt != null);
		if (decision == CableDecision.StopForCable)
		{
			_ = Task.Run(async () =>
			{
				await StopBridgeAsync("controller on a USB cable").ConfigureAwait(false);
				RaiseNotice(new ServiceNotice(ServiceNoticeKind.CableConnected));
			});
		}
		else if (decision == CableDecision.Resume)
		{
			_ = Task.Run(() => StartBridgeAsync("USB cable removed"));
		}
		if (changed)
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
