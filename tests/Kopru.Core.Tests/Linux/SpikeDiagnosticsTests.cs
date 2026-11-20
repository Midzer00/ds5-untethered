// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Protocol;
using Kopru.Platform.Linux.Diagnostics;

namespace Kopru.Core.Tests.Linux;

// What the Linux spike measures and how it masks its log.
public class SpikeDiagnosticsTests
{
	private const int BytesPerMs = 384;

	[Fact]
	public void Meter_SteadyStream_HasNoDropouts_AndASecondOfAudioPerSecond()
	{
		IsoAudioMeter meter = new(BytesPerMs);
		// 1 ms packets, 8 per transfer, every 8 ms for one second.
		for (int t = 0; t < 1000; t += 8)
		{
			Assert.Null(meter.Record(t, 8 * BytesPerMs));
		}
		IsoAudioWindow window = meter.TakeWindow(1000);

		Assert.True(window.Active);
		Assert.Equal(125, window.Transfers);
		Assert.Equal(1000, window.AudioMsPerSecond, 1);
		Assert.Equal(125, window.TransfersPerSecond, 1);
		Assert.Equal(8, window.MaxGapMs);
		Assert.Equal(0, window.Dropouts);
		Assert.Equal(1, window.Starts);
	}

	[Fact]
	public void Meter_APauseOverTheGapLimit_IsADropout_ALongOneANewStart()
	{
		IsoAudioMeter meter = new(BytesPerMs, dropoutGapMs: 50, streamStopMs: 1000);
		meter.Record(0, BytesPerMs);
		meter.Record(10, BytesPerMs);
		Assert.Null(meter.Record(60, BytesPerMs)); // exactly 50: not over the limit
		Assert.Equal(80.0, meter.Record(140, BytesPerMs));
		Assert.Null(meter.Record(1500, BytesPerMs)); // the game closed and reopened its stream

		IsoAudioWindow window = meter.TakeWindow(2000);
		Assert.Equal(1, window.Dropouts);
		Assert.Equal(2, window.Starts);
		Assert.Equal(80, window.MaxGapMs);
		Assert.Equal(1, meter.TotalDropouts);
		Assert.Equal(5, meter.TotalTransfers);
	}

	[Fact]
	public void Meter_WindowsStartOverEachTime()
	{
		IsoAudioMeter meter = new(BytesPerMs);
		Assert.False(meter.TakeWindow(0).Active);
		meter.Record(100, BytesPerMs * 4);
		IsoAudioWindow first = meter.TakeWindow(600);
		IsoAudioWindow second = meter.TakeWindow(1600);

		Assert.Equal(0.6, first.Seconds, 3);
		Assert.Equal(4, first.AudioMs);
		Assert.False(second.Active);
		Assert.Equal(1.0, second.Seconds, 3);
		Assert.Equal(4L * BytesPerMs, meter.TotalBytes);
	}

	private static byte[] UsbOutput(Action<byte[]> fill)
	{
		byte[] report = new byte[UsbReports.OutputSize];
		report[0] = UsbReports.OutputId;
		fill(report);
		return report;
	}

	[Fact]
	public void OutputReport_ShowsTriggerEffectsAndRumble()
	{
		byte[] report = UsbOutput(r =>
		{
			r[1 + OutputBlock.Flag0] = OutputBlock.RightTriggerEffect | OutputBlock.CompatibleVibration;
			r[1 + OutputBlock.RightTrigger] = 0x21;
			r[1 + OutputBlock.MotorLeft] = 200;
			r[1 + OutputBlock.MotorRight] = 10;
			r[1 + OutputBlock.Flag1] = OutputBlock.LightbarControl;
		});

		OutputReportInfo info = OutputReportInfo.Parse(report)!.Value;

		Assert.True(info.AnyTrigger);
		Assert.True(info.RightTrigger);
		Assert.False(info.LeftTrigger);
		Assert.Equal(0x21, info.RightMode);
		Assert.Equal("R2 effect 0x21, rumble 200/10, lightbar", info.Describe());
	}

	[Fact]
	public void OutputReport_OtherReportsAreNotParsed()
	{
		Assert.Null(OutputReportInfo.Parse([0x05, 1, 2]));
		Assert.Null(OutputReportInfo.Parse(new byte[10]));
		Assert.Equal("no flags", OutputReportInfo.Parse(UsbOutput(_ => { }))!.Value.Describe());
	}

	[Fact]
	public void LogMask_HidesAddressesAndTheUserName()
	{
		string line = "hidraw: hid-uniq:02:00:00:00:00:0a opened, log /home/someone/spike.log, cwd /mnt/someone/x";

		string masked = LogMask.Mask(line, "someone");

		Assert.DoesNotContain("02:00:00:00:00:0a", masked);
		Assert.DoesNotContain("someone", masked);
		Assert.Contains("/home/<user>/spike.log", masked);
		Assert.Contains("/mnt/<user>/x", masked);
	}

	[Fact]
	public void LogMask_UnderSudo_HidesTheSudoUserToo()
	{
		string masked = LogMask.Mask("log /home/someone/a.log, cwd /srv/someone/b, user /x/root/y", "root", "someone");
		Assert.DoesNotContain("someone", masked);
		Assert.Contains("/x/root/y", masked);
	}

	[Fact]
	public void LogMask_LeavesOrdinaryTextAlone()
	{
		Assert.Equal("state: Running, input 250 Hz", LogMask.Mask("state: Running, input 250 Hz", "someone"));
		Assert.Equal("user root at /root/x", LogMask.Mask("user root at /root/x", "root"));
	}
}
