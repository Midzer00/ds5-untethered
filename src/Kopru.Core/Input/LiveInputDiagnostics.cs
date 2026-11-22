// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kopru.Core.Protocol;

namespace Kopru.Core.Input;

// What the read-only live stream (the Test page while the bridge is stopped) received, for app.log: report kinds counted
// per window, and single-sample stick outliers (a stick axis jumps more than SpikeThreshold from the sample before and
// is back within SpikeReturn of it within SpikeWindow samples). A line is written at most once per LogInterval and only
// while spikes happen; dropped microphone reports, failed CRCs and unknown reports alone give one line per stream (a pad
// that streams mic reports would otherwise fill the log every two seconds; later spike lines still count them). Cheap
// per report: a few counters and a small ring of recent samples; nothing is allocated unless a line is written.
// Nothing identifying is logged: input reports carry no address; only the report id, length, type byte and the first
// input bytes (sticks, triggers, sequence, buttons) of the spike report are shown.
public sealed class LiveInputDiagnostics
{
	public const int SpikeThreshold = 60;
	public const int SpikeReturn = 20;
	public const int SpikeWindow = 3;
	public static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(2);
	// Report bytes shown for a spike: id, type/sequence byte and the body's first 11 bytes (sticks .. buttons).
	public const int ShownBytes = 13;

	public enum Kind
	{
		Full,
		Simple,
		Mic,
		BadCrc,
		Other
	}

	private readonly Action<string> log;
	private readonly Func<long> clockMs;
	private readonly int[] counts = new int[5];
	private readonly Dictionary<int, int> otherIds = [];
	// Recent samples (sticks) and the shown bytes of each, newest last.
	private readonly (byte LX, byte LY, byte RX, byte RY)[] sticks = new (byte, byte, byte, byte)[SpikeWindow + 2];
	private readonly byte[][] shown = new byte[SpikeWindow + 2][];
	private readonly int[] lengths = new int[SpikeWindow + 2];
	private int filled;
	private int spikes;
	private string? firstSpike;
	private long windowStart;
	private bool oddReported;

	public LiveInputDiagnostics(Action<string>? log = null, Func<long>? clockMs = null)
	{
		this.log = log ?? Diagnostics.AppLog.Write;
		this.clockMs = clockMs ?? (() => Environment.TickCount64);
		windowStart = this.clockMs();
		for (int i = 0; i < shown.Length; i++)
		{
			shown[i] = new byte[ShownBytes];
		}
	}

	public int Spikes => spikes;

	// One report as read (before it is parsed or dropped).
	public void Count(Kind kind, ReadOnlySpan<byte> report)
	{
		counts[(int)kind]++;
		if (kind == Kind.Other && !report.IsEmpty && otherIds.Count < 8)
		{
			// Id and length in one key (id * 1000 + length).
			int key = report[0] * 1000 + Math.Min(report.Length, 999);
			otherIds[key] = otherIds.GetValueOrDefault(key) + 1;
		}
		Flush(false);
	}

	// A sample that went to the page, with the report it came from.
	public void Sample(InputSnapshot s, ReadOnlySpan<byte> report)
	{
		int n = sticks.Length;
		if (filled == n)
		{
			Array.Copy(sticks, 1, sticks, 0, n - 1);
			Array.Copy(lengths, 1, lengths, 0, n - 1);
			byte[] reuse = shown[0];
			Array.Copy(shown, 1, shown, 0, n - 1);
			shown[n - 1] = reuse;
			filled--;
		}
		sticks[filled] = (s.LeftX, s.LeftY, s.RightX, s.RightY);
		lengths[filled] = report.Length;
		Array.Clear(shown[filled]);
		report[..Math.Min(report.Length, ShownBytes)].CopyTo(shown[filled]);
		filled++;
		DetectSpike();
	}

	// A spike at sample j: far from sample j-1, and the newest sample is back near j-1 (j within the last SpikeWindow).
	private void DetectSpike()
	{
		int last = filled - 1;
		for (int j = Math.Max(1, last - SpikeWindow + 1); j < last; j++)
		{
			if (Axis(sticks[j - 1], sticks[j]) is { } axis && Near(sticks[j - 1], sticks[last]))
			{
				spikes++;
				firstSpike ??= Describe(axis, j);
				// Counted once: the spike sample is taken out of the history.
				sticks[j] = sticks[j - 1];
				return;
			}
		}
	}

	private static string? Axis((byte LX, byte LY, byte RX, byte RY) a, (byte LX, byte LY, byte RX, byte RY) b) =>
		Math.Abs(a.LX - b.LX) > SpikeThreshold ? "left X"
		: Math.Abs(a.LY - b.LY) > SpikeThreshold ? "left Y"
		: Math.Abs(a.RX - b.RX) > SpikeThreshold ? "right X"
		: Math.Abs(a.RY - b.RY) > SpikeThreshold ? "right Y"
		: null;

	private static bool Near((byte LX, byte LY, byte RX, byte RY) a, (byte LX, byte LY, byte RX, byte RY) b) =>
		Math.Abs(a.LX - b.LX) <= SpikeReturn && Math.Abs(a.LY - b.LY) <= SpikeReturn
		&& Math.Abs(a.RX - b.RX) <= SpikeReturn && Math.Abs(a.RY - b.RY) <= SpikeReturn;

	private string Describe(string axis, int j)
	{
		var b = new StringBuilder();
		(byte LX, byte LY, byte RX, byte RY) before = sticks[j - 1], at = sticks[j];
		b.Append(CultureInfo.InvariantCulture, $"{axis} spike (sticks {before.LX},{before.LY},{before.RX},{before.RY} -> {at.LX},{at.LY},{at.RX},{at.RY} and back) ");
		b.Append(CultureInfo.InvariantCulture, $"in report 0x{shown[j][0]:X2} len {lengths[j]} [");
		for (int i = 0; i < Math.Min(lengths[j], ShownBytes); i++)
		{
			b.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? " " : "")}{shown[j][i]:X2}");
		}
		return b.Append(']').ToString();
	}

	// Writes the window's line when it is due and something was off; `final` writes what is left (the stream ended).
	public void Flush(bool final)
	{
		long now = clockMs();
		if (!final && now - windowStart < LogInterval.TotalMilliseconds)
		{
			return;
		}
		bool odd = counts[(int)Kind.Mic] > 0 || counts[(int)Kind.BadCrc] > 0 || counts[(int)Kind.Other] > 0;
		if (spikes > 0 || (odd && !oddReported))
		{
			oddReported |= odd;
			double seconds = Math.Max((now - windowStart) / 1000.0, 0.001);
			var b = new StringBuilder("live input: ");
			b.Append(CultureInfo.InvariantCulture, $"last {seconds:F1} s: full {counts[0]}, simple {counts[1]}, mic dropped {counts[2]}, bad CRC {counts[3]}, other {counts[4]}");
			b.Append(CultureInfo.InvariantCulture, $" ({(counts.Sum() / seconds):F0} reports/s)");
			if (otherIds.Count > 0)
			{
				b.Append(" other ids ").Append(string.Join(", ", otherIds.Select(p => string.Create(CultureInfo.InvariantCulture,
					$"0x{p.Key / 1000:X2}/{p.Key % 1000}x{p.Value}"))));
			}
			if (spikes > 0)
			{
				b.Append(CultureInfo.InvariantCulture, $"; {spikes} stick spike(s), first: {firstSpike}");
			}
			log(b.ToString());
		}
		Array.Clear(counts);
		otherIds.Clear();
		spikes = 0;
		firstSpike = null;
		windowStart = now;
	}

	// The kind of a report read on a Bluetooth or USB link (before parsing).
	public static Kind KindOf(ReadOnlySpan<byte> report, bool bluetooth)
	{
		if (report.IsEmpty)
		{
			return Kind.Other;
		}
		if (bluetooth && Audio.MicPackets.IsMicReport(report))
		{
			return Kind.Mic;
		}
		if (report[0] == BluetoothReports.InputId)
		{
			return BluetoothReports.InputCrcValid(report) ? Kind.Full : Kind.BadCrc;
		}
		if (report[0] == UsbReports.InputId)
		{
			return bluetooth ? Kind.Simple : Kind.Full;
		}
		return Kind.Other;
	}
}
