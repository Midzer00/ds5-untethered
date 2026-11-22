// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// The Xbox Series pad type: the Series identity with the Share button, a GIP device id that stays the same per controller,
// the Windows version gate and the start check that falls back to Xbox 360.
public class XboxSeriesPadTests
{
	private static byte[] Ds(Action<byte[]>? change = null)
	{
		byte[] r = new byte[UsbReports.InputSize];
		r[0] = UsbReports.InputId;
		r[1] = r[2] = r[3] = r[4] = 0x80;
		r[8] = 0x08;
		change?.Invoke(r);
		return r;
	}

	// ---- Windows version ----

	[Theory]
	[InlineData(10, 0, 19043, false)]
	[InlineData(10, 0, 19044, true)]
	[InlineData(10, 0, 19045, true)]
	[InlineData(10, 0, 22631, true)]
	[InlineData(10, 0, 26200, true)]
	[InlineData(6, 3, 9600, false)]
	public void Windows10_21H2_OrLater_IsNeeded(int major, int minor, int build, bool ok) =>
		Assert.Equal(ok, XboxSeriesSupport.OsSupports(new Version(major, minor, build)));

	// ---- device id ----

	private static readonly byte[] Salt = Enumerable.Range(1, XboxSeriesStore.SaltSize).Select(i => (byte)i).ToArray();

	private const ulong Address = 0xA1B2C3D4E5F6;

	[Fact]
	public void DeviceId_IsStablePerController_AndHasTheSpecPrefix()
	{
		string seed = GipDeviceIds.Seed(Address, @"\\?\hid#x");
		ulong a = GipDeviceIds.For(Salt, seed);
		Assert.Equal(a, GipDeviceIds.For(Salt, GipDeviceIds.Seed(Address, @"\\?\hid#other-path")));
		Assert.Equal(GipDeviceIds.Prefix, a & 0xFFFFFFFF_00000000UL);
		Assert.NotEqual(0u, (uint)a);
		Assert.NotEqual(a, GipDeviceIds.For(Salt, GipDeviceIds.Seed(0xA1B2C3D4E5F7, @"\\?\hid#x")));
		Assert.NotEqual(a, GipDeviceIds.For(Salt, seed, 1));
		Assert.Equal(GipDeviceIds.For(Salt, seed, 1), GipDeviceIds.For(Salt, seed, 1));
	}

	[Fact]
	public void DeviceId_DoesNotCarryTheBluetoothAddress()
	{
		ulong id = GipDeviceIds.For(Salt, GipDeviceIds.Seed(Address, "p"));
		string hex = id.ToString("X16");
		Assert.DoesNotContain("C3D4E5F6", hex);
		Assert.DoesNotContain("A1B2C3D4", hex);
		Assert.NotEqual(unchecked((uint)Address), (uint)id);
	}

	[Fact]
	public void DeviceId_WithoutAnAddress_FollowsTheHidPath()
	{
		Assert.Equal(GipDeviceIds.For(Salt, GipDeviceIds.Seed(null, @"\\?\HID#ABC")), GipDeviceIds.For(Salt, GipDeviceIds.Seed(null, @"\\?\hid#abc")));
		Assert.NotEqual(GipDeviceIds.For(Salt, GipDeviceIds.Seed(null, "a")), GipDeviceIds.For(Salt, GipDeviceIds.Seed(null, "b")));
	}

	[Fact]
	public void DeviceId_DependsOnTheInstallSalt_SoTheAddressAloneDoesNotGiveIt()
	{
		string seed = GipDeviceIds.Seed(Address, "p");
		byte[] other = (byte[])Salt.Clone();
		other[0] ^= 1;
		Assert.NotEqual(GipDeviceIds.For(Salt, seed), GipDeviceIds.For(other, seed));
		Assert.NotEqual(GipDeviceIds.Tag(Salt, seed), GipDeviceIds.Tag(other, seed));
		// The tag is 12 hex digits and not part of the id.
		string tag = GipDeviceIds.Tag(Salt, seed);
		Assert.Matches("^[0-9a-f]{12}$", tag);
		Assert.DoesNotContain(tag[..6], GipDeviceIds.For(Salt, seed).ToString("x16"));
	}

	[Fact]
	public void TwoInstalls_GiveTheSameControllerOtherIds_AndOneInstallKeepsItsIds()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-series-store-" + Guid.NewGuid().ToString("N"));
		try
		{
			string seed = GipDeviceIds.Seed(Address, "p");
			XboxSeriesStore first = new(Path.Combine(dir, "a", XboxSeriesStore.FileName));
			XboxSeriesStore second = new(Path.Combine(dir, "b", XboxSeriesStore.FileName));
			Assert.NotEqual(first.DeviceId(seed, 0), second.DeviceId(seed, 0));
			Assert.NotEqual(first.Tag(seed), second.Tag(seed));

			// The same install after a restart: the same salt, ids and saved state.
			first.Attaching(first.Tag(seed), 0);
			XboxSeriesStore again = new(Path.Combine(dir, "a", XboxSeriesStore.FileName));
			Assert.Equal(first.DeviceId(seed, 0), again.DeviceId(seed, 0));
			Assert.Equal(first.DeviceId(seed, 1), again.DeviceId(seed, 1));
			Assert.NotNull(again.Last(first.Tag(seed)));

			// The file holds neither the address nor the ids (in any case or byte order).
			string text = File.ReadAllText(Path.Combine(dir, "a", XboxSeriesStore.FileName));
			foreach (string needle in Needles(Address, first.DeviceId(seed, 0), first.DeviceId(seed, 1)))
			{
				Assert.DoesNotContain(needle, text, StringComparison.OrdinalIgnoreCase);
			}

			// A file that can't be read starts over with a new salt.
			File.WriteAllText(Path.Combine(dir, "a", XboxSeriesStore.FileName), "{ not json");
			XboxSeriesStore broken = new(Path.Combine(dir, "a", XboxSeriesStore.FileName));
			Assert.NotEqual(first.DeviceId(seed, 0), broken.DeviceId(seed, 0));
			Assert.Null(broken.Last(first.Tag(seed)));
		}
		finally
		{
			try
			{
				Directory.Delete(dir, true);
			}
			catch (IOException)
			{
			}
		}
	}

	// Text forms of the address and the device ids that must not be written anywhere.
	internal static IEnumerable<string> Needles(ulong address, params ulong[] ids)
	{
		yield return address.ToString("X12");
		yield return address.ToString("X12")[4..];
		yield return Kopru.Core.Hid.AddressText.Format(address);
		yield return string.Join(":", Enumerable.Range(0, 6).Select(i => ((byte)(address >> (8 * i))).ToString("X2")));
		foreach (ulong id in ids)
		{
			yield return VirtualXboxOne.SerialFor(id);
			yield return id.ToString("X16");
			yield return ((uint)id).ToString("X8");
			yield return Convert.ToHexString(BitConverter.GetBytes((uint)id));
		}
	}

	[Theory]
	[InlineData(null, false, 5, 0)]
	[InlineData(0, true, 5, 0)]
	[InlineData(0, false, 5, 1)]
	[InlineData(1, false, 5, 0)]
	[InlineData(1, true, 5, 0)]
	[InlineData(0, false, 120, 0)]
	public void QuickReattach_AfterAnUncleanEnd_UsesTheOtherId(int? previous, bool clean, int secondsAgo, int expected) =>
		Assert.Equal(expected, GipDeviceIds.Variant(previous, clean, TimeSpan.FromSeconds(secondsAgo)));

	[Fact]
	public void SavedState_APadThatNeverEnded_CountsAsAnUncleanEndJustNow()
	{
		DateTimeOffset now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
		Assert.Equal(0, GipDeviceIds.Variant(null, now));
		// Crashed or killed with variant 0 plugged in: the next start uses variant 1, however long ago that was.
		Assert.Equal(1, GipDeviceIds.Variant(new XboxSeriesPadRecord(0, false, null), now));
		Assert.Equal(0, GipDeviceIds.Variant(new XboxSeriesPadRecord(1, false, null), now));
		Assert.Equal(0, GipDeviceIds.Variant(new XboxSeriesPadRecord(0, true, now.AddSeconds(-5)), now));
		Assert.Equal(1, GipDeviceIds.Variant(new XboxSeriesPadRecord(0, false, now.AddSeconds(-5)), now));
		Assert.Equal(0, GipDeviceIds.Variant(new XboxSeriesPadRecord(0, false, now.AddMinutes(-5)), now));

		XboxSeriesStore store = new(salt: Salt);
		store.Attaching("t", 1);
		store.Started("t", 1);
		Assert.True(store.Last("t")!.HasStarted(1));
		Assert.False(store.Last("t")!.HasStarted(0));
		store.Ended("t", 1, true, now);
		Assert.Equal(new XboxSeriesPadRecord(1, true, now, 2), store.Last("t"));
		store.Attaching("t", 0);
		Assert.Equal(new XboxSeriesPadRecord(0, false, null, 2), store.Last("t"));
	}

	[Fact]
	public void Pad_UsesTheGivenDeviceId_InItsSerial()
	{
		ulong id = GipDeviceIds.For(Salt, "bt:000000000001");
		using VirtualXboxOne pad = new(identity: GipIdentity.XboxSeries, autoTick: false, deviceId: id);
		Assert.Equal(id, pad.DeviceId);
		SubmitResult serial = pad.Control([0x80, 6, 3, 3, 0x09, 0x04, 255, 0], []);
		Assert.Equal(VirtualXboxOne.SerialFor(id), System.Text.Encoding.Unicode.GetString(serial.Data.AsSpan(2)));
	}

	// ---- Share button ----

	[Fact]
	public void SeriesIdentity_IsTheSeriesPad_WithShare()
	{
		Assert.Equal((0x045E, 0x0B12), (GipIdentity.XboxSeries.VendorId, GipIdentity.XboxSeries.ProductId));
		Assert.True(GipIdentity.XboxSeries.Share);
		Assert.Equal(32, GipIdentity.XboxSeries.InputSize);
		Assert.False(GipIdentity.XboxOneS.Share);
		Assert.Equal(14, GipIdentity.Generic.InputSize);
		// Another firmware build than the pad without Share: Windows never reuses metadata cached for that one.
		Assert.NotEqual(GipIdentity.Generic.Build, GipIdentity.XboxSeries.Build);
	}

	[Fact]
	public void Metadata_WithShare_DeclaresTheConsoleFunctionMap_AndThe32ByteReport()
	{
		byte[] plain = GipMetadata.Gamepad(securityOptOut: true);
		byte[] share = GipMetadata.Gamepad(securityOptOut: true, share: true);
		Assert.Equal(plain.Length + 16, share.Length);
		byte[] guid = GipMetadata.ConsoleFunctionMap.ToByteArray();
		Assert.True(share.AsSpan().IndexOf(guid) >= 0);
		Assert.True(plain.AsSpan().IndexOf(guid) < 0);
		// The input message record: type 0x20, length 32.
		int input = share.AsSpan().IndexOf((byte[])[0x17, 0x00, Gip.Input]);
		Assert.True(input > 0);
		Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(share.AsSpan(input + 3)));
	}

	[Fact]
	public void CreateButton_IsView_AsOnXbox360_AndShareIsNeverPressed()
	{
		byte[] create = GipReports.FromDualSense(Ds(r => r[9] = 0x10), out _, share: true);
		Assert.Equal(32, create.Length);
		Assert.Equal(GipReports.View, create[0] & GipReports.View);
		Assert.All(create[GipReports.ShareByte..], b => Assert.Equal(0, b));

		byte[] touch = GipReports.FromDualSense(Ds(r => r[10] = 0x02), out _, share: true);
		Assert.Equal(GipReports.View, touch[0] & GipReports.View);
		Assert.Equal(0, touch[GipReports.ShareByte]);

		// Every button at once: still no Share.
		byte[] all = GipReports.FromDualSense(Ds(r => { r[8] = 0xF8; r[9] = 0xFF; r[10] = 0x07; }), out bool guide, share: true);
		Assert.True(guide);
		Assert.Equal(0, all[GipReports.ShareByte]);

		// The Xbox 360 pad maps Create the same way.
		Assert.Equal(XboxReports.Back, XboxReports.FromDualSense(Ds(r => r[9] = 0x10))[2] & XboxReports.Back);

		// Without Share (Xbox One identities) Create stays View.
		byte[] old = GipReports.FromDualSense(Ds(r => r[9] = 0x10), out _);
		Assert.Equal(14, old.Length);
		Assert.Equal(GipReports.View, old[0] & GipReports.View);
	}

	[Fact]
	public void SeriesPad_SendsThe32ByteReport_AndBuild2InHello()
	{
		GipDevice gip = new(GipIdentity.XboxSeries, 0x0000FFFB01020304UL);
		gip.Begin(0);
		byte[] hello = gip.NextPacket(0)!;
		Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(20)));
		gip.Receive([Gip.SetDeviceState, Gip.FlagSystem, 2, 1, Gip.StateStart], 1);
		while (gip.NextPacket(1) is { } p && p[0] != Gip.Input)
		{
		}
		gip.SetInput(GipReports.FromDualSense(Ds(r => r[9] = 0x10), out _, share: true), false);
		byte[] report = gip.NextPacket(2)!;
		Assert.Equal((Gip.Input, 32), (report[0], (int)report[3]));
		Assert.Equal(GipReports.View, report[4] & GipReports.View);
		Assert.Equal(0, report[4 + GipReports.ShareByte]);
	}

	// ---- started by Windows ----

	[Fact]
	public void HostStarted_OnlyAfterARealStart()
	{
		using VirtualXboxOne pad = new(identity: GipIdentity.XboxSeries, autoTick: false);
		Assert.False(pad.HostStarted);
		pad.Submit(new SubmitCommand(1, 0, UsbIpCodes.DirIn, 1, 0, 64, 0, 0, 0, new byte[8], [], []), _ => { });
		Assert.False(pad.HostStarted);
		byte[] start = [Gip.SetDeviceState, Gip.FlagSystem, 2, 1, Gip.StateStart];
		pad.Submit(new SubmitCommand(2, 0, UsbIpCodes.DirOut, 1, 0, start.Length, 0, 0, 0, new byte[8], start, []), _ => { });
		Assert.True(pad.HostStarted);
		Assert.Equal(GipDeviceState.Active, pad.State);
	}

	[Fact]
	public async Task EndedCleanly_WhenNoSessionWasRunning()
	{
		using VirtualXboxOne pad = new(identity: GipIdentity.XboxSeries, autoTick: false) { PowerOffDeliveryMs = 0, PowerOffSettleMs = 0 };
		Assert.False(pad.EndedCleanly);
		await pad.BeforeUnplugAsync();
		Assert.True(pad.EndedCleanly);
	}

	[Fact]
	public void PadTypes_XboxPadsHaveNoLightOrAudio()
	{
		Assert.True(VirtualPadType.XboxSeries.IsXbox());
		Assert.True(VirtualPadType.Xbox360.IsXbox());
		Assert.False(VirtualPadType.DualShock4.IsXbox());
		Assert.False(VirtualPadType.XboxSeries.HasLight());
		Assert.True(VirtualPadType.DualSense.HasAudio());
	}

	// ---- the bridge host ----

	// The test controller's HID path (RemovableLink "a"); without an address the device id follows it.
	private static readonly string LinkPath = new RemovableLink(new ConcurrentQueue<string>(), "a").Device.Path;

	// A controller that answers feature 0x09 (pairing info) with its Bluetooth address.
	private sealed class AddressLink(RemovableLink inner, ulong address) : IHidLink
	{
		public HidDevice Device => inner.Device;

		public int Read(Span<byte> buffer) => inner.Read(buffer);

		public void Write(ReadOnlySpan<byte> report) => inner.Write(report);

		public byte[] GetFeature(byte reportId)
		{
			if (reportId != PairingInfo.ReportId)
			{
				return inner.GetFeature(reportId);
			}
			byte[] report = new byte[20];
			report[0] = PairingInfo.ReportId;
			for (int i = 0; i < 6; i++)
			{
				report[PairingInfo.AddressOffset + i] = (byte)(address >> (8 * i));
			}
			return report;
		}

		public void CancelRead() => inner.CancelRead();

		public void Dispose() => inner.Dispose();
	}

	// A store where Windows already started the test controller's pad (variant 0), which then ended cleanly.
	private static XboxSeriesStore StartedBefore()
	{
		XboxSeriesStore store = new();
		string tag = store.Tag(GipDeviceIds.Seed(null, LinkPath));
		store.Started(tag, 0);
		store.Ended(tag, 0, true, DateTimeOffset.UnixEpoch);
		return store;
	}

	private static async Task<(BridgeHost Host, ManualTime Time, FakeAttacher Attacher, ConcurrentQueue<VirtualPadType> Changed, ConcurrentQueue<string> Failed)>
		StartAsync(VirtualPadType type, bool osSupported = true, XboxSeriesStore? store = null, Func<TimeSpan>? awake = null, IHidLink? link = null)
	{
		ConcurrentQueue<string> log = new();
		QueueControllers controllers = new();
		FakeAttacher attacher = new(log);
		DeviceIdentity identity = DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
			[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "Sony Interactive Entertainment" }, new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] });
		ManualTime time = new();
		BridgeHost host = new(controllers, new FakeIdentity(identity), attacher, time)
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			IdleMinutes = 0,
			XboxSeriesOsSupported = osSupported,
			SeriesStore = store ?? new XboxSeriesStore()
		};
		if (awake != null)
		{
			host.AwakeTime = awake;
		}
		host.UsePad(type);
		ConcurrentQueue<VirtualPadType> changed = new();
		ConcurrentQueue<string> failed = new();
		host.PadTypeChanged += changed.Enqueue;
		host.XboxSeriesFailed += failed.Enqueue;
		controllers.Links.Enqueue(link ?? new RemovableLink(log, "a"));
		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));
		return (host, time, attacher, changed, failed);
	}

	[Fact]
	public async Task NotStartedByWindowsInTime_FallsBackToXbox360()
	{
		(BridgeHost host, ManualTime time, _, ConcurrentQueue<VirtualPadType> changed, ConcurrentQueue<string> failed) =
			await StartAsync(VirtualPadType.XboxSeries, store: StartedBefore());
		await using (host)
		{
			// Just under the limit: nothing happens.
			time.Advance(XboxSeriesSupport.StartTimeout - TimeSpan.FromSeconds(1));
			await Task.Delay(100);
			Assert.Empty(failed);
			Assert.Empty(changed);

			time.Advance(TimeSpan.FromSeconds(2));
			Assert.True(SpinWait.SpinUntil(() => changed.Contains(VirtualPadType.Xbox360), 5000));
			Assert.Contains("did not start the pad", Assert.Single(failed));
			// It stays on Xbox 360 although Xbox Series is still the wanted type.
			Assert.Equal(VirtualPadType.XboxSeries, host.PadType);
			await Task.Delay(100);
			Assert.Equal([VirtualPadType.Xbox360], changed);
		}
	}

	[Fact]
	public async Task FirstStartOfADeviceId_GetsTheLongerLimit_ForTheDeviceInstall()
	{
		(BridgeHost host, ManualTime time, _, ConcurrentQueue<VirtualPadType> changed, ConcurrentQueue<string> failed) = await StartAsync(VirtualPadType.XboxSeries);
		await using (host)
		{
			// Past the normal limit: Windows may still be installing the device.
			time.Advance(XboxSeriesSupport.StartTimeout + TimeSpan.FromSeconds(5));
			await Task.Delay(150);
			Assert.Empty(failed);
			Assert.Empty(changed);

			time.Advance(XboxSeriesSupport.FirstStartTimeout - XboxSeriesSupport.StartTimeout);
			Assert.True(SpinWait.SpinUntil(() => changed.Contains(VirtualPadType.Xbox360), 5000));
			Assert.Contains("first start of this device id", Assert.Single(failed));
		}
	}

	[Fact]
	public async Task SleepRightAfterAttach_DoesNotCountAgainstTheLimit()
	{
		object gate = new();
		TimeSpan awake = TimeSpan.FromHours(3);
		(BridgeHost host, ManualTime time, _, ConcurrentQueue<VirtualPadType> changed, ConcurrentQueue<string> failed) =
			await StartAsync(VirtualPadType.XboxSeries, store: StartedBefore(), awake: () =>
			{
				lock (gate)
				{
					return awake;
				}
			});
		await using (host)
		{
			// The PC sleeps for ten minutes: the wall clock moves on, the awake time does not.
			time.Advance(TimeSpan.FromMinutes(10));
			await Task.Delay(150);
			Assert.Empty(failed);
			Assert.Empty(changed);

			lock (gate)
			{
				awake += XboxSeriesSupport.StartTimeout + TimeSpan.FromSeconds(1);
			}
			Assert.True(SpinWait.SpinUntil(() => changed.Contains(VirtualPadType.Xbox360), 5000));
			Assert.Single(failed);
		}
	}

	[Fact]
	public void AwakeClock_MovesForward()
	{
		TimeSpan a = AwakeClock.Now;
		Thread.Sleep(30);
		TimeSpan b = AwakeClock.Now;
		Assert.True(b > a, $"{a} -> {b}");
		Assert.True(a > TimeSpan.Zero);
	}

	[Fact]
	public async Task AfterACrashOrKill_TheNextStartUsesTheOtherDeviceId()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-series-crash-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, XboxSeriesStore.FileName);
		ConcurrentQueue<string> lines = new();
		Action<string> sink = lines.Enqueue;
		Kopru.Core.Diagnostics.AppLog.Written += sink;
		try
		{
			// The previous run plugged in variant 0 and was killed: the pad never ended.
			XboxSeriesStore previous = new(path);
			string tag = previous.Tag(GipDeviceIds.Seed(null, LinkPath));
			previous.Attaching(tag, 0);

			XboxSeriesStore restarted = new(path);
			(BridgeHost host, _, _, _, _) = await StartAsync(VirtualPadType.XboxSeries, store: restarted);
			await using (host)
			{
				Assert.Equal(new XboxSeriesPadRecord(1, false, null), restarted.Last(tag));
				Assert.Contains(lines, l => l.Contains($"controller {tag[..6]} variant 1") && l.Contains("never ended"));
				// Saved at once: another kill now would make the start after it use variant 0.
				Assert.Equal(new XboxSeriesPadRecord(1, false, null), new XboxSeriesStore(path).Last(tag));
			}
			// A stop unplugs the pad: its end is saved.
			Assert.NotNull(new XboxSeriesStore(path).Last(tag)!.EndedAt);
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= sink;
			try
			{
				Directory.Delete(dir, true);
			}
			catch (IOException)
			{
			}
		}
	}

	[Fact]
	public async Task Log_NamesTheControllerByASaltedTag_NeverByItsAddressOrDeviceId()
	{
		const ulong address = 0x7E3A51C9D24F;
		XboxSeriesStore store = new();
		string seed = GipDeviceIds.Seed(address, string.Empty);
		string tag = store.Tag(seed);
		ConcurrentQueue<string> lines = new();
		Action<string> sink = lines.Enqueue;
		Kopru.Core.Diagnostics.AppLog.Written += sink;
		try
		{
			RemovableLink inner = new(new ConcurrentQueue<string>(), "address");
			(BridgeHost host, ManualTime time, _, _, ConcurrentQueue<string> failed) =
				await StartAsync(VirtualPadType.XboxSeries, store: store, link: new AddressLink(inner, address));
			await using (host)
			{
				Assert.True(SpinWait.SpinUntil(() => lines.Any(l => l.Contains($"controller {tag[..6]} variant 0")), 5000));
				time.Advance(XboxSeriesSupport.FirstStartTimeout + TimeSpan.FromSeconds(1));
				Assert.True(SpinWait.SpinUntil(() => !failed.IsEmpty, 5000));
			}
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= sink;
		}
		string all = string.Join("\n", lines);
		foreach (string needle in Needles(address, store.DeviceId(seed, 0), store.DeviceId(seed, 1)))
		{
			Assert.DoesNotContain(needle, all, StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public async Task OldWindows_GetsXbox360_WithoutTryingXboxSeries()
	{
		(BridgeHost host, _, _, ConcurrentQueue<VirtualPadType> changed, _) = await StartAsync(VirtualPadType.XboxSeries, osSupported: false);
		await using (host)
		{
			await Task.Delay(100);
			Assert.Empty(changed);
			host.UsePad(VirtualPadType.DualShock4);
			Assert.True(SpinWait.SpinUntil(() => changed.Count == 1, 5000));
			host.UsePad(VirtualPadType.XboxSeries);
			Assert.True(SpinWait.SpinUntil(() => changed.Count == 2, 5000));
			Assert.Equal([VirtualPadType.DualShock4, VirtualPadType.Xbox360], changed);
		}
	}
}
