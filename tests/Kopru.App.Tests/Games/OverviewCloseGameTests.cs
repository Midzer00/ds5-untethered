// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Games;

namespace Kopru.App.Tests;

// "Oyunu Kapat" in the Overview's header (GameReturn): only while a game is detected as running; the same two steps as the
// game panel's (a close request first, the question only when the game stayed open), and only the active game is touched.
public sealed class OverviewCloseGameTests
{
	private static (FakeControllerService Fake, MainWindowViewModel Vm) Open(bool running)
	{
		var fake = new FakeControllerService(animate: false);
		fake.Watcher.Exit("Cyberpunk2077.exe");
		var catalog = new GameCatalog([new CatalogGame("Örnek Oyun", ["ornek-oyun.exe"], 900001, true, true, "high")]);
		fake.Games.SetCatalog(catalog);
		fake.Watcher.Catalog = catalog;
		fake.Watcher.Exe = running ? "ornek-oyun.exe" : null;
		fake.Games.Tick();
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now })));
		vm.BackToGame.Delay = _ => Task.CompletedTask;
		return (fake, vm);
	}

	[AvaloniaFact]
	public void NoGame_NoButton_AndNothingIsTouched()
	{
		(FakeControllerService fake, MainWindowViewModel vm) = Open(running: false);
		Assert.False(vm.BackToGame.ShowsCloseGame);
		vm.BackToGame.CloseGameCommand.Execute(null);
		vm.BackToGame.ForceCloseCommand.Execute(null);
		Assert.Empty(fake.Watcher.Source.CloseRequests);
		Assert.Empty(fake.Watcher.Source.Ended);
	}

	[AvaloniaFact]
	public async Task GameLeavesAfterTheRequest_NothingIsEnded_AndTheButtonGoes()
	{
		(FakeControllerService fake, MainWindowViewModel vm) = Open(running: true);
		GameReturn back = vm.BackToGame;
		Assert.True(back.ShowsCloseGame);
		Assert.True(vm.Overview.ShowsPowerOff);
		fake.Watcher.Source.LeavesOnCloseRequest = true;
		await back.CloseGameCommand.ExecuteAsync(null);
		fake.Games.Tick();
		Assert.Single(fake.Watcher.Source.CloseRequests);
		Assert.Empty(fake.Watcher.Source.Ended);
		Assert.False(back.ConfirmsForceClose);
		Assert.False(back.ShowsCloseGame);
		Assert.False(back.IsGameRunning);
	}

	[AvaloniaFact]
	public async Task GameStays_AsksInTheButtonsPlace_AndEndsOnlyAfterTheConfirm()
	{
		(FakeControllerService fake, MainWindowViewModel vm) = Open(running: true);
		GameReturn back = vm.BackToGame;
		await back.CloseGameCommand.ExecuteAsync(null);
		Assert.True(back.ConfirmsForceClose);
		Assert.False(back.ShowsCloseGame);
		// Turn Off Controller steps aside while the question is shown.
		Assert.False(vm.Overview.ShowsPowerOff);
		Assert.Single(fake.Watcher.Source.CloseRequests);
		Assert.Empty(fake.Watcher.Source.Ended);

		// Cancel: nothing ended, the button is back.
		back.KeepGameCommand.Execute(null);
		Assert.False(back.ConfirmsForceClose);
		Assert.True(back.ShowsCloseGame);
		Assert.True(vm.Overview.ShowsPowerOff);
		Assert.Empty(fake.Watcher.Source.Ended);

		// Ask again, then force: that one process is ended, and the game is gone.
		await back.CloseGameCommand.ExecuteAsync(null);
		back.ForceCloseCommand.Execute(null);
		Assert.Single(fake.Watcher.Source.Ended);
		fake.Games.Tick();
		Assert.False(back.ConfirmsForceClose);
		Assert.False(back.ShowsCloseGame);
	}

	[AvaloniaFact]
	public async Task TheGameEndsWhileAsking_TheQuestionGoes()
	{
		(FakeControllerService fake, MainWindowViewModel vm) = Open(running: true);
		GameReturn back = vm.BackToGame;
		await back.CloseGameCommand.ExecuteAsync(null);
		Assert.True(back.ConfirmsForceClose);
		fake.Watcher.Exit("ornek-oyun.exe");
		fake.Games.Tick();
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.False(back.ConfirmsForceClose);
		Assert.Empty(fake.Watcher.Source.Ended);
	}
}
