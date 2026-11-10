// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.Tests;

public sealed class XboxEmulationAppTests : IDisposable
{
	private readonly string folder = Path.Combine(Path.GetTempPath(), "kopru-padtypes-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void PadTypes_RoundTripThroughSettingsFile()
	{
		JsonSettingsStore store = new(Path.Combine(folder, "settings.json"));
		GameMemory memory = GameMemory.Default with { PadTypes = new Dictionary<string, VirtualPadType> { ["exe:old.exe"] = VirtualPadType.Xbox360 } };
		store.Save(new AppSettings().WithGames(memory));

		AppSettings loaded = store.Load()!;
		Assert.Equal(VirtualPadType.Xbox360, loaded.GamePadTypes!["exe:old.exe"]);
		Assert.Equal(VirtualPadType.Xbox360, loaded.ToGameMemory(GameCatalog.Empty).PadTypes!["exe:old.exe"]);
		Assert.Contains("\"Xbox360\"", File.ReadAllText(store.Path));

		// Nothing chosen: nothing written, and older files load as DualSense everywhere.
		Assert.Null(new AppSettings().WithGames(GameMemory.Default).GamePadTypes);
		Assert.Null(new AppSettings().ToGameMemory(GameCatalog.Empty).PadTypes);
	}

	[Fact]
	public void PadTypeToast_NamesTheMode()
	{
		Assert.Equal(Text.PadModeXboxTitle(), NotificationCenter.PadTypeNotice(nameof(VirtualPadType.Xbox360)).Title);
		Assert.Equal(Text.PadModeDualSenseTitle(), NotificationCenter.PadTypeNotice(nameof(VirtualPadType.DualSense)).Title);
		Assert.Equal("Xbox 360 Modu", Text.PadModeXboxTitle(Language.Turkish));
		Assert.Equal("Xbox 360 Mode", Text.PadModeXboxTitle(Language.English));
	}
}
