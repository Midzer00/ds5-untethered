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
		Assert.Equal([VirtualPadType.DualSense, VirtualPadType.Xbox360, VirtualPadType.DualShock4], picker.Options.Select(o => o.Type));
		Assert.Equal("DualShock 4", picker.Options[2].Name);
		// Every pad type is offered exactly once.
		Assert.Equal(Enum.GetValues<VirtualPadType>().Order(), picker.Options.Select(o => o.Type).Order());
		Assert.Equal(VirtualPadType.DualSense, picker.Selected!.Type);

		picker.Selected = picker.Options[2];
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.PadTypeFor("exe:old.exe"));
		Assert.Equal(VirtualPadType.DualShock4, picker.Selected!.Type);
		Assert.Equal(VirtualPadType.DualShock4, fake.Games.SnapshotMemory().PadTypes!["exe:old.exe"]);
	}

	[Fact]
	public void GamePicker_TipExplainsDualShock4()
	{
		using FakeControllerService fake = new(animate: false);
		string tip = new GamePadTypePicker(fake.Games, "exe:old.exe").Tip;
		Assert.Equal(Text.PadTypeTip() + " " + Text.PadTypeDualShock4Tip(), tip);
		Assert.Contains("DualShock 4", Text.PadTypeDualShock4Tip(Language.Turkish));
		Assert.Contains("PS4", Text.PadTypeDualShock4Tip(Language.Turkish));
		Assert.Contains("DualShock 4", Text.PadTypeDualShock4Tip(Language.English));
	}

	[Fact]
	public void PadTypeToast_NamesDualShock4Mode()
	{
		AppNotification notice = NotificationCenter.PadTypeNotice(nameof(VirtualPadType.DualShock4));
		Assert.Equal(Text.PadModeDualShock4Title(), notice.Title);
		Assert.Equal(Text.PadModeDualShock4Body(), notice.Body);
		Assert.Equal("DualShock 4 Modu", Text.PadModeDualShock4Title(Language.Turkish));
		Assert.Equal("DualShock 4 Mode", Text.PadModeDualShock4Title(Language.English));
		Assert.Equal(Text.PadModeDualSenseTitle(), NotificationCenter.PadTypeNotice("nonsense").Title);
	}
}
