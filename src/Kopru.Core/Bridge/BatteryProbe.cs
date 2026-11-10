// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

// Opens the Bluetooth DualSense for reading only (shared, no write access) while the bridge is stopped.
public interface IReadOnlyControllerSource
{
	// Null while no Bluetooth DualSense can be opened. The link's Write must never be called.
	IHidLink? TryOpenReadOnly();
}

// Reads the battery of a Bluetooth DualSense while the bridge is stopped: opens it read-only, reads input reports for
// up to ReadTime and closes it again. Nothing is ever written to the controller.
//
// Over Bluetooth the pad starts with the short "simple" report 0x01 (report ID, sticks, buttons, triggers: about 10
// bytes); it carries no battery or status byte. Only the full report 0x31 has it (body byte 52, see BatteryStatus).
// When only simple reports arrive, the probe reads feature report 0x05 once, which switches the pad to full reports -
// exactly what BluetoothSession.Start does first anyway, so it changes nothing the bridge wouldn't. No output report
// is sent (output would take the lightbar/haptics state away from whoever else uses the pad).
public sealed class BatteryProbe
{
	public static readonly TimeSpan DefaultReadTime = TimeSpan.FromSeconds(1);

	// The short Bluetooth input report sent until the pad is switched to full reports.
	public const byte SimpleInputId = 0x01;

	private readonly IReadOnlyControllerSource source;

	public BatteryProbe(IReadOnlyControllerSource source)
	{
		this.source = source;
	}

	public TimeSpan ReadTime { get; set; } = DefaultReadTime;

	// Called with the open (read-only) Bluetooth link before the battery is read, e.g. to read feature reports once.
	public Action<IHidLink>? Opened { get; set; }

	// The read-only source (also used for the controller test page's live input).
	public IReadOnlyControllerSource Source => source;

	// Runs Read on its own thread (reads block).
	public Task<BatteryStatus?> ReadAsync(CancellationToken cancel = default) =>
		Task.Factory.StartNew(() => Read(cancel), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

	// The battery, or null when there is no controller, it can't be opened, or no full report came in time.
	public BatteryStatus? Read(CancellationToken cancel = default)
	{
		if (cancel.IsCancellationRequested)
		{
			return null;
		}
		IHidLink? link;
		try
		{
			link = source.TryOpenReadOnly();
		}
		catch (Exception)
		{
			return null;
		}
		if (link == null)
		{
			return null;
		}
		using (link)
		{
			if (link.Device.Transport != Transport.Bluetooth)
			{
				return null;
			}
			Opened?.Invoke(link);
			return ReadFrom(link, cancel);
		}
	}

	private BatteryStatus? ReadFrom(IHidLink link, CancellationToken cancel)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		deadline.CancelAfter(ReadTime);
		using CancellationTokenRegistration unblock = deadline.Token.Register(link.CancelRead);
		// A cancel that lands just before a read starts doesn't unblock it; keep cancelling until the loop ends.
		using Timer retry = new(_ =>
		{
			if (deadline.IsCancellationRequested)
			{
				link.CancelRead();
			}
		}, null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));
		byte[] buffer = new byte[Math.Max(link.Device.InputLength, BluetoothReports.InputSize)];
		bool askedFull = false;
		while (!deadline.IsCancellationRequested)
		{
			int length;
			try
			{
				length = link.Read(buffer);
			}
			catch (Exception)
			{
				return null;
			}
			ReadOnlySpan<byte> report = buffer.AsSpan(0, length);
			if (BluetoothReports.InputCrcValid(report))
			{
				return BatteryStatus.Parse(InputState.Body(report));
			}
			if (length > 0 && report[0] == SimpleInputId && !askedFull && !deadline.IsCancellationRequested)
			{
				askedFull = true;
				try
				{
					link.GetFeature(BluetoothSession.CalibrationFeature);
				}
				catch (Exception)
				{
					// Some pads answer late; the time simply runs out then.
				}
			}
		}
		return null;
	}
}
