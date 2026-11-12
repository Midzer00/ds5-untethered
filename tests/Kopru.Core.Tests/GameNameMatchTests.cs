// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;

namespace Kopru.Core.Tests;

// Unreal Engine exe pairs and the name fallback for list games without exe names (folder, version info, window title).
public class GameNameMatchTests
{
	private const string GameFolder = @"C:\Games\LEGO Batman - Legacy of the Dark Knight";
	private const string Launcher = GameFolder + @"\LEGOBatmanLotDK.exe";
	private const string Shipping = GameFolder + @"\LEGOBatmanLotDK\Binaries\Win64\LEGOBatmanLotDK-Win64-Shipping.exe";

	private static readonly GameCatalog Catalog = new(
	[
		new CatalogGame("Lego Batman: Legacy of the Dark Knight", [], 2215200, true, true),
		new CatalogGame("Returnal", ["Returnal-Win64-Shipping.exe"], 1649240, true, true),
		new CatalogGame("Stellar Blade", ["SB.exe"], null, true, true),
		new CatalogGame("Luto", [], null, true, false),
	]);

	[Theory]
	[InlineData("Returnal.exe", "Returnal")]
	[InlineData("returnal-win64-shipping.EXE", "Returnal")]
	[InlineData("SB-Win64-Shipping.exe", "Stellar Blade")]
	[InlineData("SB.exe", "Stellar Blade")]
	[InlineData("Returnal-Win64-Shipping2.exe", null)]
	[InlineData("-Win64-Shipping.exe", null)]
	[InlineData("SBX-Win64-Shipping.exe", null)]
	public void Catalog_MatchesUnrealPairsEitherWay(string exe, string? name) => Assert.Equal(name, Catalog.Find(exe)?.Name);

	[Theory]
	[InlineData("LEGO® Batman™: Legacy of the Dark Knight", "legobatmanlegacyofthedarkknight")]
	[InlineData("LEGO Batman - Legacy of the Dark Knight", "legobatmanlegacyofthedarkknight")]
	[InlineData(null, "")]
	public void NormalizeName_KeepsLettersAndDigits(string? text, string expected) => Assert.Equal(expected, GameCatalog.NormalizeName(text));

	[Theory]
	[InlineData("LEGO® Batman™: Legacy of the Dark Knight", true)]
	[InlineData("Lego Batman", false)]
	[InlineData("LEGO Batman: Legacy of the Dark Knight 2", false)]
	[InlineData("LEGO Batman: Legacy of the Dark Knight - YouTube", false)]
	[InlineData("Luto", false)]
	[InlineData("Returnal", false)]
	public void FindByName_NeedsTheWholeName_AndAGameWithoutExe(string text, bool found) =>
		Assert.Equal(found, Catalog.FindByName(text) != null);

	[Fact]
	public void Folders_InnermostFirst_WithoutTheDrive() =>
		Assert.Equal(["Win64", "Binaries", "LEGOBatmanLotDK", "LEGO Batman - Legacy of the Dark Knight", "Games"], GameWatcher.Folders(Shipping));

	[Fact]
	public void RealCatalog_RecognisesLegoBatmanByBothExeNames()
	{
		GameCatalog embedded = GameCatalog.LoadEmbedded();
		Assert.Equal("Lego Batman: Legacy of the Dark Knight", embedded.Find("LEGOBatmanLotDK.exe")?.Name);
		Assert.Equal("Lego Batman: Legacy of the Dark Knight", embedded.Find("LEGOBatmanLotDK-Win64-Shipping.exe")?.Name);

		var processes = new GameModeTests.FakeProcesses();
		var log = new List<string>();
		var watcher = new GameWatcher(embedded, processes, details: new FakeDetails(), log: log.Add);
		processes.Show("LEGOBatmanLotDK-Win64-Shipping.exe", Shipping);
		Assert.Equal("Lego Batman: Legacy of the Dark Knight", watcher.Poll()?.Name);
		processes.Show("LEGOBatmanLotDK.exe", Launcher);
		Assert.Equal("Lego Batman: Legacy of the Dark Knight", watcher.Poll()?.Name);
		Assert.Empty(log);
	}

	[Fact]
	public void Fallback_ByFolderName()
	{
		var processes = new GameModeTests.FakeProcesses();
		var log = new List<string>();
		var watcher = new GameWatcher(Catalog, processes, details: new FakeDetails(), log: log.Add);
		processes.Show("LEGOBatmanLotDK-Win64-Shipping.exe", Shipping);
		ForegroundGame? game = watcher.Poll();
		Assert.Equal("Lego Batman: Legacy of the Dark Knight", game?.Name);
		Assert.Equal("Lego Batman: Legacy of the Dark Knight", game?.Key);
		Assert.Equal(["game: Lego Batman: Legacy of the Dark Knight recognised by folder name (LEGOBatmanLotDK-Win64-Shipping.exe)"], log);
	}

	[Fact]
	public void Fallback_ByProductName_FileDescription_AndWindowTitle()
	{
		const string Path = @"D:\Apps\Bin\Game.exe";
		var details = new FakeDetails();
		var processes = new GameModeTests.FakeProcesses();
		var log = new List<string>();
		var watcher = new GameWatcher(Catalog, processes, details: details, log: log.Add);

		details.Versions[Path] = ("LEGO® Batman™: Legacy of the Dark Knight", null);
		processes.Show("Game.exe", Path);
		Assert.NotNull(watcher.Poll());
		Assert.Contains("recognised by product name (Game.exe)", log[^1]);

		details.Versions[Path] = (null, "Lego Batman Legacy of the Dark Knight");
		watcher.Catalog = Catalog;
		Assert.NotNull(watcher.Poll());
		Assert.Contains("recognised by file description (Game.exe)", log[^1]);

		details.Versions.Clear();
		details.Titles["Game.exe"] = "LEGO® Batman™: Legacy of the Dark Knight";
		watcher.Catalog = Catalog;
		Assert.NotNull(watcher.Poll());
		Assert.Contains("recognised by window title (Game.exe)", log[^1]);
		Assert.All(log, line => Assert.DoesNotContain(Path, line));
	}

	[Theory]
	[InlineData("notes.exe", "LEGO Batman: Legacy of the Dark Knight - Notes")]
	[InlineData("player.exe", "Lego Batman")]
	[InlineData("vivaldi.exe", "LEGO Batman: Legacy of the Dark Knight trailer - YouTube - Vivaldi")]
	[InlineData("chrome.exe", "LEGO® Batman™: Legacy of the Dark Knight")]
	public void Fallback_NoFalsePositiveForSimilarTitles(string exe, string title)
	{
		var details = new FakeDetails();
		details.Titles[exe] = title;
		var processes = new GameModeTests.FakeProcesses();
		var watcher = new GameWatcher(Catalog, processes, details: details, log: _ => { });
		processes.Show(exe, @"C:\Program Files\Some App\" + exe);
		Assert.Null(watcher.Poll());
	}

	[Fact]
	public void Fallback_CachesVersionInfo_AndRereadsOnlyTheTitle()
	{
		const string Path = @"D:\Apps\Bin\Game.exe";
		var details = new FakeDetails();
		var processes = new GameModeTests.FakeProcesses();
		var log = new List<string>();
		var watcher = new GameWatcher(Catalog, processes, details: details, log: log.Add);
		details.Titles["Game.exe"] = "Loading";
		processes.Show("Game.exe", Path);
		for (int i = 0; i < 5; i++)
		{
			Assert.Null(watcher.Poll());
		}
		Assert.Equal(1, details.VersionReads);
		Assert.Equal(5, details.TitleReads);

		// The game sets its title after the window opened: recognised on the next poll, logged once, then cached.
		details.Titles["Game.exe"] = "LEGO® Batman™: Legacy of the Dark Knight";
		Assert.NotNull(watcher.Poll());
		int titles = details.TitleReads;
		for (int i = 0; i < 5; i++)
		{
			Assert.NotNull(watcher.Poll());
		}
		Assert.Equal(titles, details.TitleReads);
		Assert.Equal(1, details.VersionReads);
		Assert.Single(log);

		// Known games and ignored apps never read details.
		processes.Show("Returnal.exe", @"D:\Games\Returnal.exe");
		processes.Show("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe");
		Assert.NotNull(watcher.Recognize(new ProcessInfo(1, "Returnal.exe")));
		Assert.Null(watcher.Recognize(new ProcessInfo(2, "chrome.exe")));
		Assert.Equal(1, details.VersionReads);
	}

	// Window titles by exe name, version info by path; counts the reads.
	private sealed class FakeDetails : IProcessDetails
	{
		public Dictionary<string, string> Titles { get; } = new(StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, (string?, string?)> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);
		public int TitleReads { get; private set; }
		public int VersionReads { get; private set; }

		public string? WindowTitle(ProcessInfo process)
		{
			TitleReads++;
			return Titles.TryGetValue(process.Exe, out string? title) ? title : null;
		}

		public (string? Product, string? Description) VersionInfo(string path)
		{
			VersionReads++;
			return Versions.TryGetValue(path, out (string?, string?) info) ? info : (null, null);
		}
	}
}
