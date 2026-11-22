// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Hid;
using Kopru.Core.Input;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

// Live input for the controller test page while the bridge is stopped: the pad is opened read-only (the battery
// probe's source) and its input is streamed until cancelled. Like the battery probe it holds the session gate, so it
// never runs while the bridge starts or runs or a test holds the controller; StartAsync and the output tests cancel it.
// Nothing is ever written; when only the short report 0x01 arrives, feature report 0x05 is read once (see BatteryProbe).
public sealed partial class BridgeHost
{
	// Streams input until cancel fires, the bridge starts, a test needs the pad or the pad goes away. False when it could
	// not start (no probe source, no pad, bridge running or busy).
	public async Task<bool> StreamInputAsync(Action<InputSnapshot> onSnapshot, CancellationToken cancel)
	{
		IReadOnlyControllerSource? source = BatteryProbe?.Source;
		if (source == null || !CanProbe() || cancel.IsCancellationRequested)
		{
			return false;
		}
		if (!await sessionGate.WaitAsync(0).ConfigureAwait(false))
		{
			return false;
		}
		CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		try
		{
			lock (gate)
			{
				if (supervisor != null || startsPending > 0)
				{
					return false;
				}
				probeCancel = cts;
			}
			return await Task.Factory.StartNew(() => ReadLive(source, onSnapshot, cts.Token), CancellationToken.None,
				TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
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

	// Output tests take the pad: a live stream (or battery probe) gives it up at once.
	private void CancelReadOnlyUse()
	{
		lock (gate)
		{
			probeCancel?.Cancel();
		}
	}

	// The live stream's report counts and stick-spike lines in app.log (tests swap the writer).
	public Func<LiveInputDiagnostics> LiveDiagnostics { get; set; } = () => new LiveInputDiagnostics();

	private bool ReadLive(IReadOnlyControllerSource source, Action<InputSnapshot> onSnapshot, CancellationToken cancel)
	{
		IHidLink? link;
		try
		{
			link = source.TryOpenReadOnly();
		}
		catch (Exception)
		{
			return false;
		}
		if (link == null)
		{
			return false;
		}
		using (link)
		{
			using CancellationTokenRegistration unblock = cancel.Register(link.CancelRead);
			using Timer retry = new(_ =>
			{
				if (cancel.IsCancellationRequested)
				{
					link.CancelRead();
				}
			}, null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
			bool bluetooth = link.Device.Transport == Transport.Bluetooth;
			byte[] buffer = new byte[Math.Max(link.Device.InputLength, BluetoothReports.InputSize)];
			bool askedFull = false;
			// One stream, one layout: once the pad sends full reports (0x31) it stays in that mode for this link, so a short
			// 0x01 report after that is not the simple Bluetooth layout and is dropped instead of being drawn as a jump.
			bool sawFull = false;
			LiveInputDiagnostics diagnostics = LiveDiagnostics();
			while (!cancel.IsCancellationRequested)
			{
				int length;
				try
				{
					length = link.Read(buffer);
				}
				catch (Exception)
				{
					break;
				}
				ReadOnlySpan<byte> report = buffer.AsSpan(0, length);
				if (cancel.IsCancellationRequested)
				{
					break;
				}
				diagnostics.Count(LiveInputDiagnostics.KindOf(report, bluetooth), report);
				if (bluetooth && length > 0 && report[0] == UsbReports.InputId && sawFull)
				{
					continue;
				}
				if (InputSnapshot.FromReport(report, bluetooth) is { } snapshot)
				{
					sawFull |= snapshot.IsFull;
					diagnostics.Sample(snapshot, report);
					onSnapshot(snapshot);
					if (bluetooth && !snapshot.IsFull && !askedFull)
					{
						askedFull = true;
						try
						{
							link.GetFeature(BluetoothSession.CalibrationFeature);
						}
						catch (Exception)
						{
							// The simple report still shows sticks, buttons and triggers.
						}
					}
				}
			}
			diagnostics.Flush(final: true);
		}
		return true;
	}
}
