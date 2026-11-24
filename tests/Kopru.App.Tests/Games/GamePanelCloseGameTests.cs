// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Games;

namespace Kopru.App.Tests;

// "Oyunu Kapat": the running game is asked to close first; only when it is still there afterwards does the header ask
// whether to end it. Only the detected active game is ever touched (the fake process source records every request).
public sealed class GamePanelCloseGameTests
{
	private static (FakeControllerService Fake, MainWindowViewModel Vm, GamePanelViewModel Panel, GameItemViewModel Game) Open(string name)
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now })));
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		GameItemViewModel game = vm.SupportedGames.Games.Single(g => g.Name == name);
		vm.SupportedGames.OpenGameCommand.Execute(game);
		return (fake, vm, vm.SupportedGames.Panel, game);
	}

	[AvaloniaFact]
	public async Task GameLeavesAfterTheRequest_NothingIsEnded()
	{
		GamePanelViewModel.CloseWait = TimeSpan.FromMilliseconds(20);
		(FakeControllerService fake, _, GamePanelViewModel panel, GameItemViewModel game) = Open("Cyberpunk 2077");
		Assert.True(game.IsRunning);
		Assert.True(panel.ShowsCloseGame);
		fake.Watcher.Source.LeavesOnCloseRequest = true;
		await panel.CloseGameCommand.ExecuteAsync(null);
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		Assert.Single(fake.Watcher.Source.CloseRequests);
		Assert.Empty(fake.Watcher.Source.Ended);
		Assert.False(panel.ConfirmsForceClose);
		Assert.False(panel.ShowsCloseGame);
	}

	[AvaloniaFact]
	public async Task GameStays_AsksFirst_AndEndsOnlyAfterTheConfirm()
	{
		GamePanelViewModel.CloseWait = TimeSpan.FromMilliseconds(20);
		(FakeControllerService fake, _, GamePanelViewModel panel, _) = Open("Cyberpunk 2077");
		await panel.CloseGameCommand.ExecuteAsync(null);
		Assert.True(panel.ConfirmsForceClose);
		Assert.Single(fake.Watcher.Source.CloseRequests);
		Assert.Empty(fake.Watcher.Source.Ended);

		// Cancel: nothing ended, the button is back.
		panel.KeepGameCommand.Execute(null);
		Assert.False(panel.ConfirmsForceClose);
		Assert.True(panel.ShowsCloseGame);
		Assert.Empty(fake.Watcher.Source.Ended);

		// Ask again, then force: that one process is ended.
		await panel.CloseGameCommand.ExecuteAsync(null);
		panel.ForceCloseCommand.Execute(null);
		Assert.Single(fake.Watcher.Source.Ended);
		Assert.False(panel.ConfirmsForceClose);
	}

	[AvaloniaFact]
	public void AGameThatDoesNotRun_HasNoCloseGame_AndNothingIsTouched()
	{
		(FakeControllerService fake, _, GamePanelViewModel panel, GameItemViewModel game) = Open("Returnal");
		Assert.False(game.IsRunning);
		Assert.False(panel.ShowsCloseGame);
		panel.CloseGameCommand.Execute(null);
		panel.ForceCloseCommand.Execute(null);
		Assert.Empty(fake.Watcher.Source.CloseRequests);
		Assert.Empty(fake.Watcher.Source.Ended);
	}

	[Fact]
	public void TheController_RefusesOtherKeys_AndEndingWithoutAnEarlierRequest()
	{
		var fake = new FakeControllerService(animate: false);
		fake.Games.Tick();
		string key = fake.Games.ActiveGame!.Key;
		Assert.False(fake.Games.RequestCloseActiveGame("Some Other Game"));
		Assert.False(fake.Games.EndActiveGame(key));
		Assert.False(fake.Games.EndActiveGame("Some Other Game"));
		Assert.Empty(fake.Watcher.Source.CloseRequests);
		Assert.Empty(fake.Watcher.Source.Ended);
		Assert.True(fake.Games.RequestCloseActiveGame(key));
		Assert.False(fake.Games.EndActiveGame("Some Other Game"));
		Assert.True(fake.Games.EndActiveGame(key));
		Assert.Single(fake.Watcher.Source.Ended);
	}
}
