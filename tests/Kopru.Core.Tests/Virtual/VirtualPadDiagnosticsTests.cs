// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Games;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// The virtual pads' request diagnostics (PadRequestLog), the DualShock 4's feature answers over Submit and its audio
// interfaces, and the rule that Automatic never picks the DualShock 4.
public class VirtualPadDiagnosticsTests
{
	private static SubmitCommand Command(uint seq, uint direction, uint endpoint, byte[]? setup = null, byte[]? data = null, IsoPacket[]? packets = null) =>
		new(seq, 0, direction, endpoint, 0, data?.Length ?? 64, 0, packets?.Length ?? -1, 1, setup ?? new byte[8], data ?? [], packets ?? []);

	private static SubmitResult Run(IUsbIpDevice pad, byte[] setup, byte[]? data = null)
	{
		SubmitResult? result = null;
		pad.Submit(Command(1, (uint)(setup[0] >> 7), 0, setup, data), r => result = r);
		return result!;
	}

	// The lines are written on the log's background writer.
	private static void Flush() => Assert.True(PadRequestLog.Flush(TimeSpan.FromSeconds(5)));

	private static byte[] FeatureSetup(byte id, int length) =>
		[0xA1, 0x01, id, 0x03, VirtualDualShock4.HidInterface, 0, (byte)length, (byte)(length >> 8)];

	[Fact]
	public void Ds4_EveryDeclaredFeature_IsAnsweredOverSubmit_WithItsDeclaredLength_AsWindowsAsksIt()
	{
		ConcurrentQueue<string> lines = new();
		using VirtualDualShock4 pad = new() { Log = lines.Enqueue };
		foreach ((byte id, _, _, int count) in DualShock4Reports.Features)
		{
			// Windows asks GET_REPORT with wLength = the declared size with the id.
			SubmitResult r = Run(pad, FeatureSetup(id, count + 1));
			Assert.Equal(0, r.Status);
			Assert.Equal(count + 1, r.Data.Length);
			Assert.Equal(id, r.Data[0]);
		}
		Flush();
		Assert.Equal(DualShock4Reports.Features.Count, lines.Count);
		Assert.All(lines, l => Assert.Matches(@"^virtual DS4 1-1: GET_REPORT feature 0x[0-9A-F]{2} \[A1 01 03[0-9A-F]{2} 0003 len \d+\] -> answered \d+$", l));
		Assert.Contains("virtual DS4 1-1: GET_REPORT feature 0x12 [A1 01 0312 0003 len 16] -> answered 16", lines);
		Assert.Contains("virtual DS4 1-1: GET_REPORT feature 0xA3 [A1 01 03A3 0003 len 49] -> answered 49", lines);
	}

	[Fact]
	public void Ds4_Log_OncePerDistinctRequest_StallsAndStandardRequestsNamed_NoReportContents()
	{
		List<string> lines = [];
		using VirtualDualShock4 pad = new(VirtualDualSense.BusIdFor(2)) { Log = lines.Add };
		Run(pad, [0x80, 6, 0, 1, 0, 0, 18, 0]);
		Run(pad, [0x80, 6, 0, 1, 0, 0, 18, 0]);
		Run(pad, [0x81, 6, 0, 0x22, 3, 0, 0x3B, 0x02]);
		Run(pad, FeatureSetup(0x12, 16));
		Run(pad, FeatureSetup(0x12, 16));
		Run(pad, FeatureSetup(0x03, 48));
		Run(pad, [0x21, 0x0A, 0, 0, 3, 0, 0, 0]);
		Run(pad, [0x01, 0x0B, 1, 0, 1, 0, 0, 0]);
		Run(pad, [0x22, 0x01, 0, 1, 0x01, 0, 3, 0], [0x00, 0x7D, 0x00]);
		Flush();
		Assert.Equal(
		[
			"virtual DS4 1-2: GET_DESCRIPTOR device [80 06 0100 0000 len 18] -> answered 18",
			"virtual DS4 1-2: GET_DESCRIPTOR HID report [81 06 2200 0003 len 571] -> answered 507",
			"virtual DS4 1-2: GET_REPORT feature 0x12 [A1 01 0312 0003 len 16] -> answered 16",
			"virtual DS4 1-2: GET_REPORT feature 0x03 [A1 01 0303 0003 len 48] -> stalled",
			"virtual DS4 1-2: SET_IDLE [21 0A 0000 0003 len 0] -> accepted",
			"virtual DS4 1-2: SET_INTERFACE 1 alt 1 [01 0B 0001 0001 len 0] -> accepted",
			"virtual DS4 1-2: audio SET_CUR endpoint 0x01 [22 01 0100 0001 len 3] -> accepted"
		], lines);
		// The pairing report carries the pad's (made-up) address: never in the log.
		string address = Convert.ToHexString(pad.Address);
		Assert.All(lines, l => Assert.DoesNotContain(address, l.Replace(" ", string.Empty, StringComparison.Ordinal)));
		Assert.Equal(1, pad.AlternateSetting(1));
	}

	[Fact]
	public void Ds4_SessionSummary_CountsPollsInputsOutputsAndStalls_OnRelease()
	{
		List<string> lines = [];
		using VirtualDualShock4 pad = new() { Log = lines.Add };
		pad.Submit(Command(1, UsbIpCodes.DirIn, VirtualDualShock4.InEndpoint), _ => { });
		pad.Submit(Command(2, UsbIpCodes.DirIn, VirtualDualShock4.InEndpoint), _ => { });
		pad.PushInput(new byte[64]);
		byte[] output = new byte[32];
		output[0] = 0x05;
		pad.Submit(Command(3, UsbIpCodes.DirOut, VirtualDualShock4.OutEndpoint, data: output), _ => { });
		Run(pad, [0x21, 0x09, 0x05, 0x02, 3, 0, 32, 0], output);
		Run(pad, FeatureSetup(0x77, 64));
		pad.Released();
		pad.Released();

		Flush();
		Assert.Equal("virtual DS4 1-1: first IN poll on endpoint 4", lines[0]);
		Assert.Equal("virtual DS4 1-1: first output report 0x05, 32 bytes (interrupt OUT)", lines[1]);
		Assert.Equal("virtual DS4 1-1: SET_REPORT output 0x05 [21 09 0205 0003 len 32] -> accepted", lines[2]);
		Assert.Equal("virtual DS4 1-1: GET_REPORT feature 0x77 [A1 01 0377 0003 len 64] -> stalled", lines[3]);
		Assert.Matches(@"^virtual DS4 1-1: session end \(host released\): \d+\.\d s, IN polls 2 \(\d+/s\), input reports 1, output reports 2 " +
			@"\(interrupt 1, SET_REPORT 1\), control requests 2 \(stalled 1, distinct 2\)$", lines[4]);
		// A second release with nothing new in between logs nothing.
		Assert.Equal(5, lines.Count);
	}

	[Fact]
	public void RequestLog_SummaryAfter10Seconds_WithThePollingRate_AndCapsDistinctLines()
	{
		List<string> lines = [];
		long now = 0;
		PadRequestLog log = new("DS4", "1-1", 3, () => lines.Add, () => now);
		for (int i = 0; i < 2500; i++)
		{
			log.InPoll(4);
			log.InputDelivered();
			now += 4;
		}
		log.InPoll(4);
		Flush();
		Assert.Equal(
		[
			"virtual DS4 1-1: first IN poll on endpoint 4",
			"virtual DS4 1-1: summary after 10 s: 10.0 s, IN polls 2501 (250/s), input reports 2500, output reports 0 " +
				"(interrupt 0, SET_REPORT 0), control requests 0 (stalled 0, distinct 0)"
		], lines);

		lines.Clear();
		for (int i = 0; i < PadRequestLog.MaxDistinct + 10; i++)
		{
			log.Control([0xA1, 0x01, (byte)i, 0x03, 3, 0, 64, 0], SubmitResult.Stall());
		}
		Flush();
		Assert.Equal(PadRequestLog.MaxDistinct + 1, lines.Count);
		Assert.Equal($"virtual DS4 1-1: more than {PadRequestLog.MaxDistinct} distinct control requests, further ones are only counted", lines[^1]);
		log.End("disposed");
		Flush();
		Assert.EndsWith($"control requests {PadRequestLog.MaxDistinct + 10} (stalled {PadRequestLog.MaxDistinct + 10}, distinct {PadRequestLog.MaxDistinct})", lines[^1]);
	}

	[Fact]
	public void Logging_NeverRunsOnTheSubmitPath_TheTransferCompletesFirst()
	{
		using ManualResetEventSlim release = new();
		using ManualResetEventSlim writing = new();
		List<string> events = [];
		void Sink(string line)
		{
			writing.Set();
			release.Wait(5000);
			lock (events)
			{
				events.Add("logged");
			}
		}
		using VirtualDualShock4 pad = new() { Log = Sink };
		// A slow sink (the app log's file lock) holds the writer; transfers still complete at once.
		pad.Submit(Command(1, 1, 0, [0x80, 6, 0, 1, 0, 0, 18, 0]), _ => { lock (events) { events.Add("completed 1"); } });
		Assert.True(writing.Wait(5000));
		pad.Submit(Command(2, 1, 0, [0x80, 6, 0, 2, 0, 0, 255, 0]), _ => { lock (events) { events.Add("completed 2"); } });
		byte[] output = new byte[32];
		output[0] = 0x05;
		pad.Submit(Command(3, UsbIpCodes.DirOut, VirtualDualShock4.OutEndpoint, data: output), _ => { lock (events) { events.Add("completed 3"); } });
		lock (events)
		{
			Assert.Equal(["completed 1", "completed 2", "completed 3"], events);
		}
		release.Set();
		Flush();
		lock (events)
		{
			Assert.Equal(3, events.Count(e => e == "logged"));
		}
	}

	[Fact]
	public void DualSense_LogsItsRequestsTheSameWay()
	{
		List<string> lines = [];
		byte[] device = [18, 1, 0x00, 0x02, 0, 0, 0, 64, 0x4C, 0x05, 0xE6, 0x0C, 0x00, 0x01, 1, 2, 0, 1];
		byte[] configuration =
		[
			9, 2, 34, 0, 1, 1, 0, 0xC0, 0xFA,
			9, 4, 3, 0, 2, 3, 0, 0, 0,
			9, 0x21, 0x11, 0x01, 0, 1, 0x22, 0x08, 0x00,
			7, 5, 0x84, 3, 64, 0, 6,
		];
		using VirtualDualSense pad = new(DeviceIdentity.Create(device, configuration, [0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "Sony Interactive Entertainment" }, new Dictionary<byte, byte[]> { [0x20] = [0x20, 1, 2] })) { Log = lines.Add };
		Run(pad, [0xA1, 0x01, 0x20, 0x03, 3, 0, 64, 0]);
		Run(pad, [0xA1, 0x01, 0x22, 0x03, 3, 0, 64, 0]);
		pad.Submit(Command(2, UsbIpCodes.DirIn, VirtualDualSense.HidInEndpoint), _ => { });
		pad.Released();
		Flush();
		Assert.Equal("virtual DualSense 1-1: GET_REPORT feature 0x20 [A1 01 0320 0003 len 64] -> answered 3", lines[0]);
		Assert.Equal("virtual DualSense 1-1: GET_REPORT feature 0x22 [A1 01 0322 0003 len 64] -> stalled", lines[1]);
		Assert.Equal("virtual DualSense 1-1: first IN poll on endpoint 4", lines[2]);
		Assert.StartsWith("virtual DualSense 1-1: session end (host released): ", lines[3]);
	}

	[Fact]
	public void Ds4_Audio_SpeakerAndMicTransfersCompleteOnTheFrameClock_MicIsSilent()
	{
		using VirtualDualShock4 pad = new() { Log = _ => { } };
		Assert.Equal(0, Run(pad, [0x01, 0x0B, 1, 0, 2, 0, 0, 0]).Status);
		Assert.Equal(1, pad.AlternateSetting(2));
		Assert.Equal(new byte[] { 1 }, Run(pad, [0x81, 0x0A, 0, 0, 2, 0, 1, 0]).Data);

		// The sampling rates never set are the endpoints' own; volume range as on the DualSense.
		Assert.Equal(new byte[] { 0x00, 0x7D, 0x00 }, Run(pad, [0xA2, 0x81, 0, 1, 0x01, 0, 3, 0]).Data);
		Assert.Equal(new byte[] { 0x80, 0x3E, 0x00 }, Run(pad, [0xA2, 0x81, 0, 1, 0x82, 0, 3, 0]).Data);
		Assert.Equal(new byte[] { 0x00, 0xA0 }, Run(pad, [0xA1, 0x82, 0, 2, 0, 2, 2, 0]).Data);

		ManualResetEventSlim speaker = new(), mic = new();
		SubmitResult? micResult = null;
		IsoPacket[] speakerPackets = Enumerable.Range(0, 4).Select(i => new IsoPacket(i * 128, 128, 0, 0)).ToArray();
		pad.Submit(Command(5, UsbIpCodes.DirOut, VirtualDualShock4.AudioOutEndpoint, data: new byte[512], packets: speakerPackets), r =>
		{
			Assert.Equal(512, r.ActualLength);
			speaker.Set();
		});
		IsoPacket[] micPackets = Enumerable.Range(0, 4).Select(i => new IsoPacket(i * 34, 34, 0, 0)).ToArray();
		pad.Submit(Command(6, UsbIpCodes.DirIn, VirtualDualShock4.MicInEndpoint, packets: micPackets), r =>
		{
			micResult = r;
			mic.Set();
		});
		Assert.True(speaker.Wait(3000));
		Assert.True(mic.Wait(3000));
		Assert.Equal(4 * VirtualDualShock4.MicBytesPerFrame, micResult!.Data.Length);
		Assert.All(micResult.Data, b => Assert.Equal(0, b));
		Assert.All(micResult.Packets!, p => Assert.Equal(VirtualDualShock4.MicBytesPerFrame, p.ActualLength));
	}

	[Fact]
	public void Automatic_NeverPicksTheDualShock4()
	{
		GameCatalog catalog = new([new CatalogGame("Old Game", ["old.exe"], null, false, true)]);
		var processes = new GameModeTests.FakeProcesses();
		var games = new GameModeController(catalog, new GameWatcher(catalog, processes), null);
		Assert.Null(games.ForcedPadType);
		Assert.Equal(VirtualPadType.DualSense, games.CurrentPadType);
		processes.Show("old.exe");
		games.Tick();
		string key = games.ActiveGame!.Key;
		Assert.Null(games.PadChoiceFor(key));
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor(key));
		Assert.Equal(VirtualPadType.DualSense, games.CurrentPadType);
		// The default type is for games we know nothing about: a listed game keeps DualSense.
		games.SetForcedPadType(VirtualPadType.Xbox360);
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor(key));
		games.SetForcedPadType(null);
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor(key));
	}
}
