// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

public sealed class GameListUpdateTests : IDisposable
{
	private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
	private readonly string folder = Path.Combine(Path.GetTempPath(), "kopru-gamelist-app-" + Guid.NewGuid().ToString("N"));
	private int requests;

	public void Dispose()
	{
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	// The sample catalog's games, 20 base games (a list needs at least 20) and count new ones.
	private static string NewerList(int count, string updated = "2099-01-01")
	{
		count += 20;
		IEnumerable<string> sample = FakeControllerService.SampleCatalog.Games.Select(g =>
			$$"""{ "name": "{{g.Name}}", "exe": ["{{g.Exe.FirstOrDefault()}}"], "haptics": true, "adaptiveTriggers": true }""");
		IEnumerable<string> added = Enumerable.Range(1, count).Select(i =>
			$$"""{ "name": "{{(i > 20 ? "New Game " + (i - 20) : "Base " + i)}}", "exe": ["newgame{{i}}.exe"], "haptics": true, "adaptiveTriggers": false }""");
		return $$"""{ "version": 1, "updated": "{{updated}}", "games": [{{string.Join(",", sample.Concat(added))}}] }""";
	}

	private GameCatalogUpdater Updater(string body) => new(new HttpClient(new Handler(() =>
	{
		requests++;
		return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
	})), Path.Combine(folder, "supported-games.json"), "https://example.test/list.json");

	// The fake's list with the 20 base games, as if an earlier update had brought them.
	private static FakeControllerService Fake()
	{
		var fake = new FakeControllerService(animate: false);
		fake.Games.SetCatalog(GameCatalog.Parse(NewerList(0, "2098-01-01")));
		return fake;
	}

	private static MainWindowViewModel Window(FakeControllerService fake, GameCatalogUpdater updater, MemoryStore? store = null) =>
		new(fake, new AppEnvironment(Store: store, GameList: updater, Now: () => Now));

	[AvaloniaFact]
	public async Task AutoCheck_AddsGames_SwapsTheCatalogAndShowsOneToast()
	{
		var fake = Fake();
		int before = fake.Games.KnownGames().Count;
		var raised = new List<AppNotification>();
		MainWindowViewModel vm = Window(fake, Updater(NewerList(25)));
		vm.Notifications.Raised += raised.Add;
		await vm.AutoUpdateTask;

		Assert.Equal(1, requests);
		Assert.Equal(before + 25, fake.Games.KnownGames().Count);
		Assert.Contains(vm.SupportedGames.Games, g => g.Name == "New Game 7");
		Assert.Equal(Now, vm.Settings.GameListCheckedAt);
		// The toast went out while the window was built (before this test could subscribe): the same type is now in its cooldown.
		Assert.False(vm.Notifications.Post(NotificationCenter.GameListUpdated(25)));
		Assert.Empty(raised);
	}

	[AvaloniaFact]
	public async Task AutoCheck_RaisesTheToastOnlyWhenGamesWereAdded()
	{
		var fake = Fake();
		var sample = new MainWindowViewModel(fake);
		int toasts = 0;
		var vm = new SupportedGamesViewModel(fake, sample.Settings, Updater(NewerList(3)), () => Now);
		vm.ListUpdatedAutomatically += added =>
		{
			toasts++;
			Assert.Equal(3, added);
		};
		await vm.AutoUpdateAsync();
		Assert.Equal(1, toasts);

		// A week later the same list is up to date: no toast.
		var later = new SupportedGamesViewModel(fake, sample.Settings, Updater(NewerList(3)), () => Now.AddDays(8));
		later.ListUpdatedAutomatically += _ => toasts++;
		await later.AutoUpdateAsync();
		Assert.Equal(2, requests);
		Assert.Equal(1, toasts);
		Assert.Equal(Text.NotifyGameListBody(3), NotificationCenter.GameListUpdated(3).Body);
	}

	[AvaloniaFact]
	public async Task AutoCheck_IsGatedByTheSettingAndTheWeek()
	{
		var fake = Fake();
		var store = new MemoryStore(new AppSettings { GameListCheckedAt = Now.AddDays(-2) });
		MainWindowViewModel recent = Window(fake, Updater(NewerList(5)), store);
		await recent.AutoUpdateTask;
		Assert.Equal(0, requests);

		var off = new MemoryStore(new AppSettings { AutoUpdateGameList = false });
		MainWindowViewModel disabled = Window(Fake(), Updater(NewerList(5)), off);
		await disabled.AutoUpdateTask;
		Assert.Equal(0, requests);

		var old = new MemoryStore(new AppSettings { GameListCheckedAt = Now.AddDays(-7) });
		MainWindowViewModel due = Window(Fake(), Updater(NewerList(5)), old);
		await due.AutoUpdateTask;
		Assert.Equal(1, requests);
	}

	[AvaloniaFact]
	public async Task Button_ShowsProgressThenTheOutcome()
	{
		var fake = Fake();
		var settings = new MainWindowViewModel(fake).Settings;
		settings.AutoUpdateGameList = false;
		var vm = new SupportedGamesViewModel(fake, settings, Updater(NewerList(2)), () => Now);
		Assert.True(vm.CanUpdateList);
		Assert.Equal(Text.UpdateGameList(), vm.UpdateListText);
		var busy = new List<bool>();
		vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SupportedGamesViewModel.IsUpdatingList))
			{
				busy.Add(vm.IsUpdatingList);
			}
		};

		await vm.UpdateListCommand.ExecuteAsync(null);
		Assert.Equal([true, false], busy);
		Assert.Contains(Text.GameListAdded(2), vm.ListStatusText);
		Assert.StartsWith(Text.GameListInfo(fake.Games.Catalog.Games.Count, "2099-01-01"), vm.ListStatusText);

		await vm.UpdateListCommand.ExecuteAsync(null);
		Assert.Contains(Text.GameListUpToDate(), vm.ListStatusText);
		Assert.False(vm.HasListError);

		var broken = new SupportedGamesViewModel(fake, settings, Updater("{ nope"), () => Now);
		await broken.UpdateListCommand.ExecuteAsync(null);
		Assert.True(broken.HasListError);
		Assert.Equal(Text.GameListFailed(Text.GameListInvalid()), broken.ListStatusText);
	}

	[AvaloniaFact]
	public void NoUpdater_HidesTheButton()
	{
		var vm = new MainWindowViewModel(Fake());
		Assert.False(vm.SupportedGames.CanUpdateList);
		Assert.True(vm.AutoUpdateTask.IsCompleted);
	}

	[Fact]
	public void GameListInfo_FormatsTheDatePerLanguage()
	{
		Assert.Equal("72 oyun · güncellendi 9 Kas 2026", Text.GameListInfo(72, "2026-11-09", Language.Turkish));
		Assert.Equal("72 games · updated 9 Nov 2026", Text.GameListInfo(72, "2026-11-09", Language.English));
		Assert.Equal("72 games", Text.GameListInfo(72, "", Language.English));
	}

	private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(respond());
	}

	private sealed class MemoryStore(AppSettings? saved) : ISettingsStore
	{
		public AppSettings? Load() => saved;

		public void Save(AppSettings settings) => saved = settings;
	}
}
