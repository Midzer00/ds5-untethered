// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Kopru.Core.Games;
using Kopru.Core.Steam;

namespace Kopru.Core.Tests;

// The shipped Steam PlayStation support list (playstation-support.json) and the local reader of Steam's appinfo.vdf, on
// made-up data only.
public class PlayStationSupportTests
{
	[Fact]
	public void Parse_TwoLists_DualSenseWinsWhenAnIdIsInBoth()
	{
		PlayStationSupport list = PlayStationSupport.Parse("""
			{ "generated": "2026-11-17", "source": "x", "ds": [10, 20, 30], "ds4": [30, 40, "50", -1] }
			""");
		Assert.Equal("2026-11-17", list.Generated);
		Assert.Equal(PlayStationPad.DualSense, list.For(10));
		Assert.Equal(PlayStationPad.DualSense, list.For(30));
		Assert.Equal(PlayStationPad.DualShock4, list.For(40));
		Assert.Null(list.For(50));
		Assert.Null(list.For(99));
		Assert.Equal(3, list.DualSenseCount);
		Assert.Equal(1, list.DualShock4Count);
		Assert.ThrowsAny<System.Text.Json.JsonException>(() => PlayStationSupport.Parse("{ nope"));
		Assert.Throws<System.Text.Json.JsonException>(() => PlayStationSupport.Parse("[]"));
		Assert.Equal(0, PlayStationSupport.Parse("{}").Count);
	}

	[Fact]
	public void Format_RoundTrips_SortedAndDeduplicated()
	{
		string text = PlayStationSupport.Format("2026-11-17", "test", [30, 10, 10, 20], [20, 5]);
		PlayStationSupport list = PlayStationSupport.Parse(text);
		Assert.Equal(PlayStationPad.DualSense, list.For(20));
		Assert.Equal(PlayStationPad.DualShock4, list.For(5));
		Assert.Equal(4, list.Count);
		Assert.True(text.IndexOf("10", StringComparison.Ordinal) < text.IndexOf("30", StringComparison.Ordinal));
		Assert.Contains("\"generated\": \"2026-11-17\"", text);
	}

	[Fact]
	public void With_AddsOnlyWhatTheListDoesNotKnow()
	{
		var shipped = new PlayStationSupport([new(1, PlayStationPad.DualShock4)]);
		PlayStationSupport merged = shipped.With(new Dictionary<long, PlayStationPad> { [1] = PlayStationPad.DualSense, [2] = PlayStationPad.DualSense });
		Assert.Equal(PlayStationPad.DualShock4, merged.For(1));
		Assert.Equal(PlayStationPad.DualSense, merged.For(2));
		Assert.Same(shipped, shipped.With(null));
	}

	[Fact]
	public void Embedded_LoadsWithBothKinds()
	{
		PlayStationSupport list = PlayStationSupport.LoadEmbedded();
		Assert.NotNull(list.Generated);
		Assert.True(list.DualSenseCount > 1000, $"ds {list.DualSenseCount}");
		Assert.True(list.DualShock4Count > 1000, $"ds4 {list.DualShock4Count}");
		Assert.Same(list, PlayStationSupport.LoadEmbedded());
	}

	// --- appinfo.vdf ---

	// A made-up appinfo.vdf: per app its categories (as "1" strings, one as an int32), plus other fields around them.
	private static byte[] AppInfo(uint magic, params (uint AppId, int[] Categories)[] apps)
	{
		bool table = magic == SteamAppInfo.Magic29;
		var strings = new List<string>();
		int Index(string key)
		{
			int i = strings.IndexOf(key);
			if (i < 0)
			{
				strings.Add(key);
				i = strings.Count - 1;
			}
			return i;
		}
		void Key(BinaryWriter w, string key)
		{
			if (table)
			{
				w.Write((uint)Index(key));
			}
			else
			{
				w.Write(Encoding.UTF8.GetBytes(key));
				w.Write((byte)0);
			}
		}
		void Text(BinaryWriter w, string value)
		{
			w.Write(Encoding.UTF8.GetBytes(value));
			w.Write((byte)0);
		}
		using var file = new MemoryStream();
		using var writer = new BinaryWriter(file);
		writer.Write(magic);
		writer.Write(1u);
		long tableAtField = file.Position;
		if (table)
		{
			writer.Write(0L);
		}
		foreach ((uint appId, int[] categories) in apps)
		{
			using var body = new MemoryStream();
			using var w = new BinaryWriter(body);
			w.Write(0x2u); // state
			w.Write(1700000000u); // last update
			w.Write(0UL); // token
			w.Write(new byte[20]);
			w.Write(42u); // change number
			if (magic >= SteamAppInfo.Magic28)
			{
				w.Write(new byte[20]);
			}
			w.Write((byte)0); Key(w, "appinfo");
			w.Write((byte)2); Key(w, "appid"); w.Write((int)appId);
			w.Write((byte)0); Key(w, "common");
			w.Write((byte)1); Key(w, "name"); Text(w, "Game " + appId);
			w.Write((byte)1); Key(w, "type"); Text(w, appId == 300 ? "Application" : "Game");
			w.Write((byte)7); Key(w, "token"); w.Write(5UL);
			w.Write((byte)3); Key(w, "ratio"); w.Write(1.5f);
			w.Write((byte)0); Key(w, "category");
			for (int i = 0; i < categories.Length; i++)
			{
				if (i == 1)
				{
					w.Write((byte)2); Key(w, "category_" + categories[i]); w.Write(1);
				}
				else
				{
					w.Write((byte)1); Key(w, "category_" + categories[i]); Text(w, "1");
				}
			}
			w.Write((byte)1); Key(w, "category_57x"); Text(w, "1");
			w.Write((byte)8); // category
			w.Write((byte)8); // common
			w.Write((byte)0); Key(w, "config");
			w.Write((byte)0); Key(w, "category"); // a "category" outside "common" counts for nothing
			w.Write((byte)1); Key(w, "category_57"); Text(w, "1");
			w.Write((byte)8);
			w.Write((byte)8); // config
			w.Write((byte)8); // appinfo
			w.Write((byte)8); // root
			w.Flush();
			writer.Write(appId);
			writer.Write((uint)body.Length);
			writer.Write(body.ToArray());
		}
		writer.Write(0u);
		if (table)
		{
			long at = file.Position;
			writer.Write((uint)strings.Count);
			foreach (string s in strings)
			{
				Text(writer, s);
			}
			file.Position = tableAtField;
			writer.Write(at);
		}
		writer.Flush();
		return file.ToArray();
	}

	private static readonly (uint, int[])[] Apps =
	[
		(100, [2, 57, 22]),
		(200, [55, 56]),
		(300, [1, 2]),
		(400, [58]),
		(500, [55, 58])
	];

	[Theory]
	[InlineData(SteamAppInfo.Magic27)]
	[InlineData(SteamAppInfo.Magic28)]
	[InlineData(SteamAppInfo.Magic29)]
	public void AppInfo_ReadsTheAskedAppsCategories_EveryVersion(uint magic)
	{
		using var stream = new MemoryStream(AppInfo(magic, Apps));
		Dictionary<long, PlayStationPad>? found = SteamAppInfo.ReadPlayStation(stream, [100, 200, 300, 500, 999]);
		Assert.NotNull(found);
		Assert.Equal(PlayStationPad.DualSense, found[100]);
		Assert.Equal(PlayStationPad.DualShock4, found[200]);
		Assert.False(found.ContainsKey(300));
		Assert.Equal(PlayStationPad.DualSense, found[500]);
		// Not asked for: not read.
		Assert.False(found.ContainsKey(400));
		Assert.False(found.ContainsKey(999));

		stream.Position = 0;
		Dictionary<long, HashSet<int>>? categories = SteamAppInfo.ReadCategories(stream, [100]);
		Assert.Equal([2, 22, 57], categories![100].Order());

		// The kind of app (common/type): software such as Wallpaper Engine is an "Application", not a game.
		stream.Position = 0;
		Dictionary<long, SteamAppDetails>? details = SteamAppInfo.ReadDetails(stream, [100, 300]);
		Assert.Equal(new SteamAppDetails(PlayStationPad.DualSense, "Game"), details![100]);
		Assert.True(details[100].IsGame);
		Assert.Equal(new SteamAppDetails(null, "Application"), details[300]);
		Assert.False(details[300].IsGame);
		Assert.True(SteamAppDetails.IsGameType("demo"));
		Assert.False(SteamAppDetails.IsGameType("Tool"));
		Assert.False(SteamAppDetails.IsGameType(null));
	}

	[Fact]
	public void AppInfo_AnyProblem_GivesNull()
	{
		byte[] good = AppInfo(SteamAppInfo.Magic29, Apps);
		// Cut short, unknown version, a broken entry size, a bad string table offset, an unknown field type.
		Assert.Null(SteamAppInfo.ReadPlayStation(new MemoryStream(good[..(good.Length / 2)]), [500]));
		byte[] magic = (byte[])good.Clone();
		magic[0] = 0x30;
		Assert.Null(SteamAppInfo.ReadPlayStation(new MemoryStream(magic), [100]));
		byte[] size = (byte[])good.Clone();
		BitConverter.GetBytes(uint.MaxValue).CopyTo(size, 16 + 4);
		Assert.Null(SteamAppInfo.ReadPlayStation(new MemoryStream(size), [100]));
		byte[] tableAt = (byte[])good.Clone();
		BitConverter.GetBytes(long.MaxValue).CopyTo(tableAt, 8);
		Assert.Null(SteamAppInfo.ReadPlayStation(new MemoryStream(tableAt), [100]));
		byte[] v27 = AppInfo(SteamAppInfo.Magic27, Apps);
		int headerEnd = 8 + 8 + 4 + 4 + 8 + 20 + 4;
		v27[headerEnd] = 0x42;
		Assert.Null(SteamAppInfo.ReadPlayStation(new MemoryStream(v27), [100]));
		Assert.Null(SteamAppInfo.ReadPlayStation(new MemoryStream([]), [100]));
		// Nothing asked: nothing read, even from garbage.
		Assert.Empty(SteamAppInfo.ReadPlayStation(new MemoryStream([1, 2, 3]), [])!);
	}

	[Fact]
	public void ReadLocal_MissingSteamOrFile_IsEmpty_AndAProblemIsLoggedOnce()
	{
		Assert.Empty(SteamAppInfo.ReadLocal(null, [1]));
		string steam = Path.Combine(Path.GetTempPath(), "kopru-appinfo-" + Guid.NewGuid().ToString("N"));
		try
		{
			Assert.Empty(SteamAppInfo.ReadLocal(steam, [1]));
			Directory.CreateDirectory(Path.Combine(steam, "appcache"));
			File.WriteAllBytes(Path.Combine(steam, "appcache", "appinfo.vdf"), AppInfo(SteamAppInfo.Magic28, Apps));
			Assert.Equal(PlayStationPad.DualShock4, SteamAppInfo.ReadLocal(steam, [200])[200]);
			// Kept while the file stays the same: asked again, it is not read again; an app not read yet is.
			int reads = SteamAppInfo.LocalReads;
			Assert.Equal(PlayStationPad.DualShock4, SteamAppInfo.ReadLocal(steam, [200])[200]);
			Assert.Empty(SteamAppInfo.ReadLocal(steam, [300]));
			Assert.Equal("Application", SteamAppInfo.ReadLocalDetails(steam, [300, 200])[300].Type);
			Assert.Equal(reads + 1, SteamAppInfo.LocalReads);
			File.WriteAllBytes(Path.Combine(steam, "appcache", "appinfo.vdf"), [9, 9, 9, 9, 9]);
			var logged = new List<string>();
			Assert.Empty(SteamAppInfo.ReadLocal(steam, [200], logged.Add));
			Assert.Empty(SteamAppInfo.ReadLocal(steam, [200], logged.Add));
			Assert.True(logged.Count <= 1);
			Assert.All(logged, l => Assert.DoesNotContain(steam, l));
		}
		finally
		{
			Directory.Delete(steam, true);
		}
	}

	// The installed scan gives found Steam games the shipped list does not know what the local cache says; a failing read
	// changes nothing.
	[Fact]
	public void Scan_FillsTheLocalSupport_OnlyForUnknownApps()
	{
		var files = new FakeInstallFiles();
		files.File(@"C:\Steam\steamapps\appmanifest_9201.acf", "\"AppState\"\n{\n\t\"appid\"\t\t\"9201\"\n\t\"name\"\t\t\"Local One\"\n\t\"installdir\"\t\t\"LocalOne\"\n}\n");
		files.Dir(@"C:\Steam\steamapps\common\LocalOne");
		files.File(@"C:\Steam\steamapps\appmanifest_9202.acf", "\"AppState\"\n{\n\t\"appid\"\t\t\"9202\"\n\t\"name\"\t\t\"Wallpaper Maker\"\n\t\"installdir\"\t\t\"Wallpaper\"\n}\n");
		files.Dir(@"C:\Steam\steamapps\common\Wallpaper");
		List<IReadOnlyCollection<long>> asked = [];
		var finder = new InstalledGameFinder(files, null, new SteamLibrary(new FakeInstallSteam(files, @"C:\Steam")), null, _ => { },
			ids =>
			{
				asked.Add(ids);
				return new Dictionary<long, SteamAppDetails>
				{
					[9201] = new(PlayStationPad.DualShock4, "Game"),
					[9202] = new(null, "Application")
				};
			});
		InstalledScan scan = finder.Scan(GameCatalog.Empty);
		// Software Steam lists as an "Application" is left out as not a game.
		FoundGame local = Assert.Single(scan.Found);
		Assert.Equal((PlayStationPad.DualShock4, "Game", true), (local.PlayStation, local.SteamType, local.IsKnownGame));
		Assert.Equal(1, scan.NotGames);
		Assert.Equal([9201L, 9202L], Assert.Single(asked).Order());

		var failing = new InstalledGameFinder(files, null, new SteamLibrary(new FakeInstallSteam(files, @"C:\Steam")), null, _ => { },
			_ => throw new IOException("locked"));
		// Not read: both stay (as games Steam's cache was not read for), with nothing filled in.
		Assert.All(failing.Scan(GameCatalog.Empty).Found, g => Assert.Equal((null, null, false), (g.PlayStation, g.SteamType, g.IsKnownGame)));
	}
}
