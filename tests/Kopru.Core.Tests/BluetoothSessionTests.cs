// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Kopru.Core.Bluetooth;
using Kopru.Core.Haptics;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

internal sealed class FakeLink(Transport transport = Transport.Bluetooth, int outputLength = 547) : IHidLink
{
	private readonly BlockingCollection<byte[]> inputs = [];

	private readonly CancellationTokenSource closed = new();

	public HidDevice Device { get; } = new(transport == Transport.Bluetooth ? "\\\\?\\hid#{00001124-0000-1000-8000-00805f9b34fb}_x" : "\\\\?\\hid#usb",
		0x054C, 0x0CE6, transport == Transport.Bluetooth ? 78 : 64, outputLength, 64);

	public ConcurrentQueue<byte[]> Writes { get; } = new();

	public List<byte> Features { get; } = [];

	public void Feed(byte[] report) => inputs.Add(report);

	public int Read(Span<byte> buffer)
	{
		byte[] next = inputs.Take(closed.Token);
		next.CopyTo(buffer);
		return next.Length;
	}

	public void Write(ReadOnlySpan<byte> report) => Writes.Enqueue(report.ToArray());

	public byte[] GetFeature(byte reportId)
	{
		Features.Add(reportId);
		return new byte[64];
	}

	public void CancelRead() => closed.Cancel();

	public void Dispose() => closed.Cancel();

	public static byte[] Input(byte l2, byte sequence)
	{
		byte[] report = new byte[78];
		report[0] = 0x31;
		report[2 + 4] = l2;
		report[2 + 6] = sequence;
		report[2 + 52] = 0x08;
		BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), Crc.Compute(Crc.InputSeed, report.AsSpan(0, 74)));
		return report;
	}
}

public class BluetoothSessionTests
{
	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 3000), "timed out");

	[Fact]
	public void Start_ReadsCalibration_AndReleasesTheStartUpGlow()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);

		session.Start();

		Assert.Equal([BluetoothSession.CalibrationFeature], link.Features);
		byte[] first = link.Writes.First();
		Assert.Equal(547, first.Length);
		Assert.Equal(0x31, first[0]);
		Assert.Equal(OutputBlock.LightOut, first[3 + OutputBlock.LightbarSetup]);
	}

	[Fact]
	public void Output_IsSentInHapticsMode_WithValidCrcAndRisingSequence()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		session.Start();

		session.UpdateOutput(b => OutputBlock.SetRumble(b, 200, 100));
		session.UpdateOutput(b => OutputBlock.SetLightbar(b, 0, 200, 40));

		byte[][] writes = link.Writes.ToArray();
		byte[] last = writes[^1];
		Assert.Equal(0, last[3 + OutputBlock.Flag0] & 0x03);
		Assert.Equal(0x62, last[3 + OutputBlock.Flag1] & 0x62);
		Assert.Equal((0, 200, 40), (last[3 + OutputBlock.Red], last[3 + OutputBlock.Red + 1], last[3 + OutputBlock.Red + 2]));
		Assert.Equal(Crc.Compute(Crc.OutputSeed, last.AsSpan(0, 74)), BinaryPrimitives.ReadUInt32LittleEndian(last.AsSpan(74)));
		Assert.Equal(((writes[^2][1] >> 4) + 1) & 0x0F, last[1] >> 4);
	}

	[Fact]
	public void Output_WithoutHapticsMode_KeepsRumble()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link) { HapticsMode = false };

		session.UpdateOutput(b => OutputBlock.SetRumble(b, 200, 100));

		byte[] last = link.Writes.Last();
		Assert.Equal((0x03, 100, 200), (last[3] & 0x03, last[3 + 2], last[3 + 3]));
	}

	[Fact]
	public void Input_IsParsed_AndBadCrcIgnored()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		List<InputState> seen = [];
		session.InputReceived += s => { lock (seen) { seen.Add(s); } };
		session.Start();

		byte[] bad = FakeLink.Input(99, 1);
		bad[10] ^= 1;
		link.Feed(bad);
		link.Feed(FakeLink.Input(77, 2));

		WaitUntil(() => session.InputReports == 1);
		Assert.Equal((77, 80), (session.LastInput.L2, session.LastInput.BatteryLevel));
	}

	[Fact]
	public void Haptics_StreamWhatIsPushed_ThenStopAfterASilentTail()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		for (int i = 0; i < 64; i++)
		{
			session.Haptics.Push(50, -50);
		}

		session.WakeHaptics();

		WaitUntil(() => session.HapticsReports >= 2 + BluetoothSession.SilentReportsBeforeStop);
		Thread.Sleep(200);
		long reports = session.HapticsReports;
		Assert.Equal(2 + BluetoothSession.SilentReportsBeforeStop, reports);
		byte[] first = link.Writes.First(w => w[0] == 0x32);
		Assert.Equal((50, unchecked((byte)-50)), (first[13], first[14]));
	}

	[Fact]
	public void Haptics_KeepTime()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		for (int i = 0; i < HapticsMixer.MaxFrames; i++)
		{
			session.Haptics.Push(10, 10);
		}

		System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
		session.WakeHaptics();
		WaitUntil(() => session.HapticsReports >= 30);
		double perReport = clock.Elapsed.TotalMilliseconds / session.HapticsReports;

		Assert.InRange(perReport, 9.5, 12.5);
	}

	[Fact]
	public void UsbController_IsRejected() =>
		Assert.Throws<ArgumentException>(() => new BluetoothSession(new FakeLink(Transport.Usb, 48)));
}

public class HapticsMixerTests
{
	[Fact]
	public void TakesInOrder_PadsWithSilence_AndDropsTheOldestWhenFull()
	{
		HapticsMixer mixer = new();
		for (int i = 0; i < HapticsMixer.MaxFrames + 2; i++)
		{
			mixer.Push((sbyte)(i % 100), 0);
		}
		sbyte[] left = new sbyte[HapticsMixer.MaxFrames + 4];
		sbyte[] right = new sbyte[left.Length];

		Assert.True(mixer.Take(left, right));

		Assert.Equal(2, left[0]);
		Assert.Equal(0, left[^1]);
		Assert.False(mixer.Take(left, right));
	}
}
