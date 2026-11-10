// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

public sealed class GameCatalogUpdaterTests : IDisposable
{
	private readonly string folder = Path.Combine(Path.GetTempPath(), "kopru-gamelist-" + Guid.NewGuid().ToString("N"));

	private string SavePath => Path.Combine(folder, "supported-games.json");

	public void Dispose()
	{
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	// A list of count games named "Game 1".. with exe "game1.exe"..
	public static string ListJson(string updated, int count, int version = 1, string prefix = "Game")
	{
		IEnumerable<string> games = Enumerable.Range(1, count).Select(i =>
			$$"""{ "name": "{{prefix}} {{i}}", "exe": ["{{prefix.ToLowerInvariant()}}{{i}}.exe"], "haptics": true, "adaptiveTriggers": true }""");
		return $$"""{ "version": {{version}}, "updated": "{{updated}}", "games": [{{string.Join(",", games)}}] }""";
	}

	private GameCatalogUpdater Updater(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null) =>
		new(new HttpClient(new FakeHandler(respond)), SavePath, "https://example.test/supported-games.json", timeout);

	private GameCatalogUpdater Serving(string body) => Updater((_, _) => Task.FromResult(Ok(body)));

	private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

	private static GameCatalog Current => GameCatalog.Parse(ListJson("2026-11-09", 25));

	[Fact]
	public void Url_PointsAtTheEmbeddedFileOnTheDefaultBranch() =>
		Assert.Equal("https://raw.githubusercontent.com/Midzer00/ds5-untethered/main/src/Kopru.Core/Games/supported-games.json", GameListSource.Url);

	[Fact]
	public async Task Newer_IsSavedAndReportsAddedAndRemoved()
	{
		string json = ListJson("2026-11-16", 27).Replace("\"Game 1\"", "\"Brand New\"");
		CatalogUpdateResult result = await Serving(json).CheckAsync(Current);

		Assert.Equal(CatalogUpdateStatus.Updated, result.Status);
		Assert.Equal(27, result.Total);
		Assert.Equal(["Brand New", "Game 26", "Game 27"], result.Added.Order().ToArray());
		Assert.Equal(["Game 1"], result.Removed);
		Assert.Equal(json, File.ReadAllText(SavePath));
		Assert.False(File.Exists(SavePath + ".tmp"));
	}

	[Fact]
	public async Task SameDay_MoreGames_CountsAsNewer()
	{
		CatalogUpdateResult result = await Serving(ListJson("2026-11-09", 30)).CheckAsync(Current);
		Assert.Equal(CatalogUpdateStatus.Updated, result.Status);
		Assert.Equal(5, result.Added.Count);
	}

	[Theory]
	[InlineData("2026-11-09", 25)]
	[InlineData("2026-11-01", 40)]
	public async Task SameOrOlder_IsUpToDateAndNotSaved(string updated, int count)
	{
		GameCatalog current = Current;
		CatalogUpdateResult result = await Serving(ListJson(updated, count)).CheckAsync(current);
		Assert.Equal(CatalogUpdateStatus.UpToDate, result.Status);
		Assert.Same(current, result.Catalog);
		Assert.False(File.Exists(SavePath));
	}

	[Theory]
	[InlineData("{ nope", CatalogUpdateError.InvalidData)]
	[InlineData("<html>Rate limited</html>", CatalogUpdateError.InvalidData)]
	[InlineData("""{ "version": 1, "updated": "2026-12-01", "games": [] }""", CatalogUpdateError.InvalidData)]
	[InlineData("""{ "version": 1, "updated": "2026-12-01", "games": [ { "name": "", "exe": [] } ] }""", CatalogUpdateError.InvalidData)]
	public async Task Invalid_Fails(string body, CatalogUpdateError error)
	{
		CatalogUpdateResult result = await Serving(body).CheckAsync(Current);
		Assert.Equal(CatalogUpdateStatus.Failed, result.Status);
		Assert.Equal(error, result.Error);
		Assert.NotEmpty(result.Reason);
		Assert.False(File.Exists(SavePath));
	}

	[Fact]
	public async Task TooFewGames_Fails()
	{
		CatalogUpdateResult result = await Serving(ListJson("2026-12-01", GameCatalogUpdater.MinGames - 1)).CheckAsync(Current);
		Assert.Equal(CatalogUpdateError.InvalidData, result.Error);
	}

	[Fact]
	public async Task NewerSchema_Fails()
	{
		CatalogUpdateResult result = await Serving(ListJson("2026-12-01", 30, version: GameCatalog.MaxSchemaVersion + 1)).CheckAsync(Current);
		Assert.Equal(CatalogUpdateError.UnsupportedVersion, result.Error);
	}

	[Fact]
	public async Task OverSizeCap_Fails()
	{
		string huge = ListJson("2026-12-01", 30).Replace("\"games\"", "\"pad\": \"" + new string('x', GameCatalogUpdater.MaxBytes) + "\", \"games\"");
		CatalogUpdateResult result = await Serving(huge).CheckAsync(Current);
		Assert.Equal(CatalogUpdateError.TooLarge, result.Error);
		Assert.False(File.Exists(SavePath));
	}

	[Fact]
	public async Task Offline_Fails()
	{
		CatalogUpdateResult result = await Updater((_, _) => throw new HttpRequestException("no route")).CheckAsync(Current);
		Assert.Equal(CatalogUpdateError.Offline, result.Error);
		CatalogUpdateResult notFound = await Updater((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))).CheckAsync(Current);
		Assert.Equal(CatalogUpdateError.Offline, notFound.Error);
	}

	[Fact]
	public async Task Slow_TimesOut()
	{
		GameCatalogUpdater updater = Updater(async (_, cancel) =>
		{
			await Task.Delay(TimeSpan.FromSeconds(30), cancel);
			return Ok("{}");
		}, TimeSpan.FromMilliseconds(100));
		CatalogUpdateResult result = await updater.CheckAsync(Current);
		Assert.Equal(CatalogUpdateError.Timeout, result.Error);
	}

	[Fact]
	public void SaveAtomically_ReplacesTheFileAndLeavesNoTemp()
	{
		GameCatalogUpdater.SaveAtomically(SavePath, "old");
		GameCatalogUpdater.SaveAtomically(SavePath, "new");
		Assert.Equal("new", File.ReadAllText(SavePath));
		Assert.Single(Directory.GetFiles(folder));
	}

	[Fact]
	public async Task SaveFailure_Fails()
	{
		Directory.CreateDirectory(SavePath); // a folder where the file should go
		CatalogUpdateResult result = await Serving(ListJson("2026-12-01", 30)).CheckAsync(Current);
		Assert.Equal(CatalogUpdateError.SaveFailed, result.Error);
	}

	[Fact]
	public void Load_UsesTheNewerOfEmbeddedAndDownloaded()
	{
		GameCatalog embedded = GameCatalog.LoadEmbedded();
		Assert.Null(GameCatalog.TryLoadFile(SavePath));
		Assert.Equal(embedded.Games.Count, GameCatalog.Load(SavePath).Games.Count);

		GameCatalogUpdater.SaveAtomically(SavePath, ListJson("2099-01-01", 21, prefix: "Future"));
		GameCatalog newer = GameCatalog.Load(SavePath);
		Assert.Equal(21, newer.Games.Count);
		Assert.NotNull(newer.Find("future1.exe"));

		GameCatalogUpdater.SaveAtomically(SavePath, ListJson("2000-01-01", 21, prefix: "Past"));
		Assert.Equal(embedded.Games.Count, GameCatalog.Load(SavePath).Games.Count);
	}

	[Theory]
	[InlineData("{ broken")]
	[InlineData("""{ "version": 1, "updated": "2099-01-01", "games": [] }""")]
	[InlineData("""{ "version": 99, "updated": "2099-01-01", "games": [] }""")]
	public void Load_BrokenDownload_FallsBackToEmbedded(string content)
	{
		GameCatalogUpdater.SaveAtomically(SavePath, content);
		Assert.Equal(GameCatalog.LoadEmbedded().Games.Count, GameCatalog.Load(SavePath).Games.Count);
	}

	[Fact]
	public void EmbeddedList_PassesValidation()
	{
		GameCatalog embedded = GameCatalog.LoadEmbedded();
		Assert.Null(GameCatalogUpdater.Validate(embedded));
		Assert.True(embedded.Games.Count >= GameCatalogUpdater.MinGames);
	}

	[Fact]
	public void AutoCheck_RunsAtMostOnceAWeek()
	{
		var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
		Assert.True(GameCatalogUpdater.ShouldAutoCheck(true, null, now));
		Assert.False(GameCatalogUpdater.ShouldAutoCheck(false, null, now));
		Assert.False(GameCatalogUpdater.ShouldAutoCheck(true, now.AddDays(-6), now));
		Assert.True(GameCatalogUpdater.ShouldAutoCheck(true, now.AddDays(-7), now));
		// A clock that went backwards does not block the check forever.
		Assert.True(GameCatalogUpdater.ShouldAutoCheck(true, now.AddDays(3), now));
	}

	[Fact]
	public void Controller_SwapsCatalogLive_AndRecognisesTheNewGames()
	{
		GameCatalog old = GameCatalog.Parse(ListJson("2026-11-09", 21));
		var processes = new GameModeTests.FakeProcesses();
		processes.Show("newgame1.exe");
		var watcher = new GameWatcher(old, processes);
		var games = new GameModeController(old, watcher, new RecordingBridge());
		var asked = new List<ForegroundGame>();
		games.AskRequested += asked.Add;
		games.Tick();
		Assert.Null(games.State.Name);

		int changed = 0;
		games.CatalogChanged += () => changed++;
		GameCatalog updated = GameCatalog.Parse(ListJson("2026-11-16", 21, prefix: "NewGame"));
		games.SetCatalog(updated);

		Assert.Equal(1, changed);
		Assert.Same(updated, games.Catalog);
		Assert.Same(updated, watcher.Catalog);
		Assert.Contains(games.KnownGames(), g => g.Name == "NewGame 21");
		games.Tick();
		Assert.Equal("NewGame 1", games.State.Name);
		Assert.Equal("NewGame 1", Assert.Single(asked).Name);
	}

	private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			respond(request, cancellationToken);
	}

	private sealed class RecordingBridge : IGameBridge
	{
		public void ApplyProfile(BridgeProfile profile)
		{
		}

		public GameSignals TakeGameSignals() => default;
	}
}
