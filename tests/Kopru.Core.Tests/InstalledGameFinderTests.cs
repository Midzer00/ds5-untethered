// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Kopru.Core.Games;
using Kopru.Core.Steam;

namespace Kopru.Core.Tests;

// A made-up file system for the finder: folders and small text files; listings of Denied folders throw.
internal sealed class FakeInstallFiles : IInstallFileSystem
{
	private readonly HashSet<string> dirs = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);

	public List<string> Drives { get; } = [@"C:\"];

	public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);

	public static string Norm(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

	public FakeInstallFiles Dir(string path)
	{
		for (string? p = Norm(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
		{
			dirs.Add(p);
		}
		return this;
	}

	public FakeInstallFiles File(string path, string text = "")
	{
		files[path] = text;
		return Dir(Path.GetDirectoryName(path)!);
	}

	public void Remove(string path)
	{
		string root = Norm(path);
		dirs.RemoveWhere(d => d.Equals(root, StringComparison.OrdinalIgnoreCase) || d.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
		foreach (string f in files.Keys.Where(f => f.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
		{
			files.Remove(f);
		}
	}

	public IEnumerable<string> FixedDrives() => Drives;

	public bool DirectoryExists(string path) => dirs.Contains(Norm(path));

	public IEnumerable<string> Directories(string path)
	{
		Check(path);
		return dirs.Where(d => string.Equals(Path.GetDirectoryName(d), Norm(path), StringComparison.OrdinalIgnoreCase)).ToList();
	}

	public IEnumerable<string> Files(string path, string pattern)
	{
		Check(path);
		var match = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);
		return files.Keys.Where(f => string.Equals(Path.GetDirectoryName(f), Norm(path), StringComparison.OrdinalIgnoreCase) && match.IsMatch(Path.GetFileName(f))).ToList();
	}

	public string ReadAllText(string path)
	{
		Check(Path.GetDirectoryName(path)!);
		return files.TryGetValue(path, out string? text) ? text : throw new FileNotFoundException(path);
	}

	private void Check(string path)
	{
		if (Denied.Contains(Norm(path)))
		{
			throw new UnauthorizedAccessException(path);
		}
	}
}

// Steam's files read through the same fake.
internal sealed class FakeInstallSteam(FakeInstallFiles files, string? steamPath) : ISteamEnvironment
{
	public string? SteamPath => steamPath;
	public bool IsSteamRunning => false;
	public bool FileExists(string path) => files.Files(Path.GetDirectoryName(path)!, Path.GetFileName(path)).Any();
	public string ReadAllText(string path) => files.ReadAllText(path);
	public IEnumerable<string> GetDirectories(string path) => files.Directories(path);
	public DateTime GetLastWriteTimeUtc(string path) => DateTime.UnixEpoch;
	public IEnumerable<string> GetFiles(string path, string pattern) => files.Files(path, pattern);
}

internal sealed class FakeUninstall : IUninstallEntries
{
	public List<UninstallEntry> Entries { get; } = [];

	public IEnumerable<UninstallEntry> Read() => Entries;
}

// Finding the supported games installed on this PC: each source with made-up files (found, not found, near-miss names,
// unreadable or missing folders, removed folders), the order of the sources and the summary log line.
public class InstalledGameFinderTests
{
	private const string Batman = @"C:\Games\LEGO Batman - Legacy of the Dark Knight";
	private const string EpicDir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

	internal static readonly GameCatalog Catalog = new(
	[
		new CatalogGame("Lego Batman: Legacy of the Dark Knight", [], 2215200, true, true),
		new CatalogGame("Returnal", ["Returnal-Win64-Shipping.exe"], 1649240, true, true),
		new CatalogGame("Cyberpunk 2077", ["Cyberpunk2077.exe"], 1091500, true, true),
		new CatalogGame("Stellar Blade", ["SB.exe"], null, true, true),
		new CatalogGame("Alan Wake 2", ["AlanWake2.exe"], null, true, true),
		new CatalogGame("Luto", [], null, true, false),
	]);

	private readonly FakeInstallFiles files = new();
	private readonly FakeUninstall registry = new();
	private readonly List<string> logged = [];

	private InstalledGameFinder Finder(string? steamPath = null) =>
		new(files, registry, new SteamLibrary(new FakeInstallSteam(files, steamPath)), EpicDir, logged.Add);

	private static Dictionary<string, InstalledGame> ByName(InstalledScan scan) => scan.Games.ToDictionary(g => g.Game.Name);

	private static string Manifest(long id, string dir) => $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n";

	[Fact]
	public void Steam_FindsTheAppIdsInEveryLibrary()
	{
		files.Drives.Add(@"D:\");
		files.File(@"C:\Steam\steamapps\libraryfolders.vdf", "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}\n");
		files.File(@"C:\Steam\steamapps\appmanifest_1091500.acf", Manifest(1091500, "Cyberpunk 2077"));
		files.File(@"D:\SteamLibrary\steamapps\appmanifest_1649240.acf", Manifest(1649240, "Returnal"));
		files.File(@"D:\SteamLibrary\steamapps\appmanifest_70.acf", Manifest(70, "Not In The List"));
		files.File(@"D:\SteamLibrary\steamapps\appmanifest_5.acf", "{ broken");

		Dictionary<string, InstalledGame> found = ByName(Finder(@"C:\Steam").Scan(Catalog));

		Assert.Equal(new[] { "Cyberpunk 2077", "Returnal" }, found.Keys.Order());
		Assert.Equal(InstalledSource.Steam, found["Returnal"].Source);
		Assert.Equal(@"D:\SteamLibrary\steamapps\common\Returnal", found["Returnal"].Folder);
	}

	[Fact]
	public void Steam_NotInstalled_FindsNothing()
	{
		Assert.Empty(Finder(null).Scan(Catalog).Games);
		Assert.Empty(Finder(@"C:\Steam").Scan(Catalog).Games);
	}

	[Fact]
	public void Epic_MatchesByExe_ThenByWholeName()
	{
		files.Dir(@"C:\Epic\AlanWake2").Dir(@"C:\Epic\StellarBlade");
		files.File(EpicDir + @"\A.item", """{ "DisplayName": "Alan Wake II", "InstallLocation": "C:\\Epic\\AlanWake2", "LaunchExecutable": "AlanWake2.exe" }""");
		files.File(EpicDir + @"\B.item", """{ "DisplayName": "Stellar Blade", "InstallLocation": "C:\\Epic\\StellarBlade", "LaunchExecutable": "Binaries/Win64/Other.exe" }""");
		// Near miss, uninstalled folder, broken file.
		files.File(EpicDir + @"\C.item", """{ "DisplayName": "Cyberpunk 2077 Demo", "InstallLocation": "C:\\Epic\\StellarBlade", "LaunchExecutable": "x.exe" }""");
		files.File(EpicDir + @"\D.item", """{ "DisplayName": "Returnal", "InstallLocation": "C:\\Epic\\Gone", "LaunchExecutable": "Returnal-Win64-Shipping.exe" }""");
		files.File(EpicDir + @"\E.item", "{ nope");

		Dictionary<string, InstalledGame> found = ByName(Finder().Scan(Catalog));

		Assert.Equal(new[] { "Alan Wake 2", "Stellar Blade" }, found.Keys.Order());
		Assert.Equal(InstalledSource.Epic, found["Alan Wake 2"].Source);
		Assert.Equal(@"C:\Epic\AlanWake2", found["Alan Wake 2"].Folder);
	}

	[Fact]
	public void Epic_UnreadableManifestFolder_IsSkippedQuietly()
	{
		files.Dir(EpicDir);
		files.Denied.Add(EpicDir);
		Assert.Empty(Finder().Scan(Catalog).Games);
	}

	[Fact]
	public void Registry_MatchesTheWholeNormalisedName()
	{
		files.Dir(@"D:\GOG Games\Cyberpunk 2077");
		registry.Entries.Add(new UninstallEntry("Cyberpunk 2077®", @"D:\GOG Games\Cyberpunk 2077"));
		registry.Entries.Add(new UninstallEntry("Stellar Blade Soundtrack", @"D:\GOG Games\Cyberpunk 2077"));
		registry.Entries.Add(new UninstallEntry("Returnal", @"D:\GOG Games\Removed"));
		registry.Entries.Add(new UninstallEntry("Luto", null));
		registry.Entries.Add(new UninstallEntry(null, null));

		InstalledGame only = Assert.Single(Finder().Scan(Catalog).Games);

		Assert.Equal("Cyberpunk 2077", only.Game.Name);
		Assert.Equal(InstalledSource.Registry, only.Source);
	}

	[Fact]
	public void Folders_OneLevelInsideTheGameRoots_ByNameOrExe()
	{
		files.Drives.AddRange([@"D:\", @"E:\"]);
		files.Dir(Batman);
		files.File(@"D:\Oyunlar\SB Folder\SB.exe");
		files.File(@"E:\games\RT\Binaries\Win64\Returnal-Win64-Shipping.exe");
		// Near miss, not a game root, two levels deep, an unreadable game folder.
		files.Dir(@"C:\Games\Cyberpunk 2077 Mod Tools");
		files.Dir(@"C:\Other\Cyberpunk 2077");
		files.File(@"C:\Games\Pack\Inner\AlanWake2.exe");
		files.Dir(@"C:\Games\Locked");
		files.Denied.Add(@"C:\Games\Locked");

		Dictionary<string, InstalledGame> found = ByName(Finder().Scan(Catalog));

		Assert.Equal(new[] { "Lego Batman: Legacy of the Dark Knight", "Returnal", "Stellar Blade" }, found.Keys.Order());
		Assert.Equal(InstalledSource.Folder, found["Lego Batman: Legacy of the Dark Knight"].Source);
		Assert.Equal(Batman, found["Lego Batman: Legacy of the Dark Knight"].Folder);
		Assert.Equal(@"E:\games\RT", found["Returnal"].Folder);
	}

	[Fact]
	public void Folders_UnreadableDrive_IsSkippedQuietly()
	{
		files.Drives.Add(@"D:\");
		files.Dir(Batman);
		files.Dir(@"D:\Games\Stellar Blade");
		files.Denied.Add(@"D:\");
		Assert.Equal("Lego Batman: Legacy of the Dark Knight", Assert.Single(Finder().Scan(Catalog).Games).Game.Name);
	}

	[Fact]
	public void AddedFolder_IsSearched_AndRemovingItDropsTheGame()
	{
		files.Dir(@"F:\Stuff\Stellar Blade");
		files.Dir(@"F:\Alan Wake 2");
		InstalledGameFinder finder = Finder();

		Assert.Empty(finder.Scan(Catalog).Games);
		// The added folder's sub-folders, and the added folder itself.
		Assert.Equal(new[] { "Alan Wake 2", "Stellar Blade" }, ByName(finder.Scan(Catalog, [@"F:\Stuff", @"F:\Alan Wake 2"])).Keys.Order());
		Assert.Equal("Alan Wake 2", Assert.Single(finder.Scan(Catalog, [@"F:\Alan Wake 2"]).Games).Game.Name);
		// A folder that no longer exists is ignored.
		files.Remove(@"F:\Alan Wake 2");
		Assert.Empty(finder.Scan(Catalog, [@"F:\Alan Wake 2", @"G:\Missing"]).Games);
	}

	[Fact]
	public void SeenGames_CountWhileTheirFolderExists()
	{
		files.Dir(@"H:\Somewhere\RT");
		InstalledGameFinder finder = Finder();
		SeenGame[] seen = [new("Returnal", @"H:\Somewhere\RT"), new("Not In The List", @"H:\Somewhere\RT")];

		InstalledGame only = Assert.Single(finder.Scan(Catalog, [], seen).Games);
		Assert.Equal(InstalledSource.Seen, only.Source);
		Assert.Equal(@"H:\Somewhere\RT", only.Folder);

		files.Remove(@"H:\Somewhere\RT");
		Assert.Empty(finder.Scan(Catalog, [], seen).Games);
	}

	[Fact]
	public void FirstSourceWins_AndTheLogLineHasCountsButNoPaths()
	{
		files.File(@"C:\Steam\steamapps\appmanifest_2215200.acf", Manifest(2215200, "LEGO Batman"));
		files.Dir(Batman);
		files.Dir(@"C:\Games\Stellar Blade");

		InstalledScan scan = Finder(@"C:\Steam").Scan(Catalog, [], [new SeenGame("Stellar Blade", @"C:\Games\Stellar Blade")]);

		Assert.Equal(2, scan.Games.Count);
		Assert.Equal(InstalledSource.Steam, ByName(scan)["Lego Batman: Legacy of the Dark Knight"].Source);
		Assert.Equal(InstalledSource.Folder, ByName(scan)["Stellar Blade"].Source);
		string line = Assert.Single(logged);
		Assert.Matches(@"^installed games: 2 found \(steam 1, epic 0, registry 0, folders 1, seen 0\) in \d+ ms$", line);
		Assert.DoesNotContain("\\", line);
	}

	[Theory]
	[InlineData(@"C:\Games\LEGO Batman\LEGOBatmanLotDK\Binaries\Win64\LEGOBatmanLotDK-Win64-Shipping.exe", @"C:\Games\LEGO Batman")]
	[InlineData(@"C:\Games\LEGO Batman\LEGOBatmanLotDK.exe", @"C:\Games\LEGO Batman")]
	[InlineData(@"D:\RT\Binaries\Win64\Returnal-Win64-Shipping.exe", @"D:\RT")]
	[InlineData("Returnal.exe", null)]
	[InlineData(null, null)]
	public void InstallFolderOf_SkipsTheUnrealBinaries(string? exe, string? folder) => Assert.Equal(folder, InstalledGameFinder.InstallFolderOf(exe));

	[Fact]
	public async Task InstalledGames_KeepsTheLatestScan()
	{
		files.Dir(Batman);
		var games = new InstalledGames(Finder());
		Assert.False(games.HasScanned);
		InstalledScan scan = await games.ScanAsync(() => (Catalog, [], []));
		Assert.True(games.HasScanned);
		Assert.Same(scan, games.Latest);
		Assert.Single(scan.Games);
	}
}
