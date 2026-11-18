// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.Tests;

public sealed class DualShock4AppTests : IDisposable
{
	private readonly string folder = Path.Combine(Path.GetTempPath(), "kopru-ds4-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void DualShock4Choice_RoundTripsThroughSettingsFile()
	{
		JsonSettingsStore store = new(Path.Combine(folder, "settings.json"));
		GameMemory memory = GameMemory.Default with
		{
			PadTypes = new Dictionary<string, VirtualPadType> { ["exe:old.exe"] = VirtualPadType.DualShock4, ["exe:x.exe"] = VirtualPadType.Xbox360 }
		};
		store.Save(new AppSettings().WithGames(memory));

		AppSettings loaded = store.Load()!;
		Assert.Equal(VirtualPadType.DualShock4, loaded.GamePadTypes!["exe:old.exe"]);
		Assert.Equal(VirtualPadType.Xbox360, loaded.GamePadTypes!["exe:x.exe"]);
		Assert.Equal(VirtualPadType.DualShock4, loaded.ToGameMemory(GameCatalog.Empty).PadTypes!["exe:old.exe"]);
		Assert.Contains("\"DualShock4\"", File.ReadAllText(store.Path));
	}

	[Fact]
	public void GamePicker_OffersDualShock4_InOrder_AndSelectsIt()
	{
		using FakeControllerService fake = new(animate: false);
		GamePadTypePicker picker = new(fake.Games, "exe:old.exe");
		Assert.Equal([null, VirtualPadType.DualSense, VirtualPadType.DualShock4, VirtualPadType.XboxSeries, VirtualPadType.Xbox360], picker.Options.Select(o => o.Type));
		Assert.Equal("DualShock 4", picker.Options[2].Name);
		// Automatic, then every pad type exactly once.
		Assert.Equal(Enum.GetValues<VirtualPadType>().Order(), picker.Options.Skip(1).Select(o => o.Type!.Value).Order());
		Assert.Null(picker.Selected!.Type);

		picker.Selected = picker.Options[2];
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.PadTypeFor("exe:old.exe"));
		Assert.Equal(VirtualPadType.DualShock4, picker.Selected!.Type);
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.SnapshotMemory().PadTypes!["exe:old.exe"]);
	}

	[Fact]
	public void GamePicker_TipExplainsDualShock4()
	{
		using FakeControllerService fake = new(animate: false);
		GamePadTypePicker picker = new(fake.Games, "exe:old.exe");
		Assert.Equal(Text.PadTypeGameHint() + "\n" + Text.PadTypeAutoLine(), picker.Tip);
		picker.Selected = picker.Options[2];
		Assert.Equal(Text.PadTypeGameHint() + "\n" + Text.PadTypeDualShock4Line(), picker.Tip);
		Assert.Equal(Text.PadTypeDualShock4Line(), picker.Options[2].Line);
		Assert.Contains("PS4", Text.PadTypeDualShock4Line(Language.Turkish));
		Assert.Contains("PS4", Text.PadTypeDualShock4Line(Language.English));
	}

	[Fact]
	public void PadTypeToast_NamesDualShock4Mode()
	{
		AppNotification notice = NotificationCenter.PadTypeNotice(VirtualPadType.DualShock4, Kopru.Core.Games.PadTypeSource.Game, gameRunning: true);
		Assert.Equal(Text.PadModeDualShock4Title(), notice.Title);
		Assert.Equal(Text.PadSwapRestartHint(), notice.Body);
		Assert.Equal("DualShock 4 Modu", Text.PadModeDualShock4Title(Language.Turkish));
		Assert.Equal("DualShock 4 Mode", Text.PadModeDualShock4Title(Language.English));
		Assert.Equal(Text.PadModeDualSenseTitle(), NotificationCenter.PadTypeTitle((VirtualPadType)99));
	}
}
