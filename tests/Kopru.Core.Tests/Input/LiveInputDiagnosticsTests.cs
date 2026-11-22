// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Tests;

// The live stream's diagnostics: a line only while something is off, at most once per interval, with the spike's report.
public class LiveInputDiagnosticsTests
{
	private readonly List<string> lines = [];
	private long now;

	private LiveInputDiagnostics Diagnostics() => new(lines.Add, () => now);

	private static byte[] Report(byte lx, byte ly = 128, byte rx = 128, byte ry = 128)
	{
		byte[] r = new byte[78];
		r[0] = 0x31;
		r[1] = 0x01;
		r[2] = lx;
		r[3] = ly;
		r[4] = rx;
		r[5] = ry;
		return r;
	}

	private static void Feed(LiveInputDiagnostics d, byte[] report)
	{
		d.Count(LiveInputDiagnostics.Kind.Full, report);
		d.Sample(new InputSnapshot(report[2], report[3], report[4], report[5], 0, 0, PadButtons.None, default, default, 0, 0, 0, 0, 0, 0, null, true),
			report);
	}

	[Fact]
	public void CleanInput_WritesNothing()
	{
		LiveInputDiagnostics d = Diagnostics();
		for (int i = 0; i < 2000; i++)
		{
			now = i;
			Feed(d, Report((byte)(127 + i % 3)));
		}
		d.Flush(final: true);
		Assert.Empty(lines);
	}

	[Fact]
	public void SingleSampleSpike_IsLoggedOncePerInterval_WithItsReportBytes()
	{
		LiveInputDiagnostics d = Diagnostics();
		Feed(d, Report(127));
		Feed(d, Report(128));
		Feed(d, Report(3, 250));
		Feed(d, Report(127));
		Assert.Equal(1, d.Spikes);
		Assert.Empty(lines); // not due yet

		Feed(d, Report(2));
		Feed(d, Report(128));
		now = (long)LiveInputDiagnostics.LogInterval.TotalMilliseconds;
		Feed(d, Report(127));

		string line = Assert.Single(lines);
		Assert.Contains("2 stick spike(s)", line);
		Assert.Contains("left X spike (sticks 128,128,128,128 -> 3,250,128,128 and back) in report 0x31 len 78 [31 01 03 FA 80 80", line);

		// The next window is clean: nothing more.
		for (int i = 0; i < 10; i++)
		{
			Feed(d, Report(127));
		}
		now *= 2;
		d.Flush(final: true);
		Assert.Single(lines);
	}

	[Fact]
	public void AMoveThatStays_IsNotASpike()
	{
		LiveInputDiagnostics d = Diagnostics();
		Feed(d, Report(128));
		Feed(d, Report(0));
		Feed(d, Report(0));
		Feed(d, Report(0));
		Feed(d, Report(0));
		d.Flush(final: true);
		Assert.Equal(0, d.Spikes);
		Assert.Empty(lines);
	}

	[Fact]
	public void Kinds_AreTold_AndCounted()
	{
		byte[] opus = new byte[Kopru.Core.Audio.MicPackets.MaxPayload];
		opus[0] = Kopru.Core.Audio.MicPackets.Marker;
		byte[] mic = Kopru.Core.Audio.MicPackets.Build(opus);
		Assert.Equal(LiveInputDiagnostics.Kind.Mic, LiveInputDiagnostics.KindOf(mic, bluetooth: true));
		Assert.Equal(LiveInputDiagnostics.Kind.BadCrc, LiveInputDiagnostics.KindOf(Report(1), bluetooth: true));
		Assert.Equal(LiveInputDiagnostics.Kind.Simple, LiveInputDiagnostics.KindOf([0x01, 1, 2, 3, 4, 8, 0, 0, 0, 0], bluetooth: true));
		Assert.Equal(LiveInputDiagnostics.Kind.Other, LiveInputDiagnostics.KindOf([0x05, 1], bluetooth: true));

		LiveInputDiagnostics d = Diagnostics();
		d.Count(LiveInputDiagnostics.Kind.Mic, mic);
		d.Count(LiveInputDiagnostics.Kind.Other, [0x05, 1]);
		d.Flush(final: true);
		string line = Assert.Single(lines);
		Assert.Contains("mic dropped 1", line);
		Assert.Contains("other ids 0x05/2x1", line);

		// A pad that keeps streaming mic reports: told once per stream, not every interval.
		for (int i = 1; i <= 5; i++)
		{
			now = i * (long)LiveInputDiagnostics.LogInterval.TotalMilliseconds;
			d.Count(LiveInputDiagnostics.Kind.Mic, mic);
		}
		d.Flush(final: true);
		Assert.Single(lines);
	}
}
