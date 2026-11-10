// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Input;
using Kopru.Core.Profiles;

namespace Kopru.Core.Tests;

public class ProfileBookTests
{
	private static readonly ProfileValues Racing = new()
	{
		GameHapticsLevel = 200,
		TriggerStrength = 40,
		LightFromGame = false,
		RumbleLevel = 80,
		RumbleFeel = RumbleFeel.Punchy,
		FallbackTrigger = FallbackTrigger.Weapon,
		FallbackStrength = 70,
		Tuning = InputTuning.Default with { LeftStick = new StickTuning { InnerDeadzone = 12, Curve = ResponseCurve.Smooth } },
		LightbarColour = 0xFF0000,
		Brightness = 30,
		PlayerLeds = false
	};

	[Fact]
	public void NewBook_HasOnlyTheDefault_Active()
	{
		var book = new ProfileBook();
		ControllerProfile only = Assert.Single(book.Profiles);
		Assert.True(only.IsDefault);
		Assert.Equal(ProfileBook.DefaultId, book.ActiveId);
		Assert.Equal(1, book.IndexOf(ProfileBook.DefaultId));
	}

	[Fact]
	public void Create_Rename_Duplicate_Delete()
	{
		var book = new ProfileBook();
		int changes = 0;
		book.Changed += () => changes++;

		ControllerProfile a = book.Create("  Racing  ", Racing)!;
		Assert.Equal("Racing", a.Name);
		Assert.Null(book.Create("   ", Racing));
		Assert.Equal("racing (2)", book.Create("racing", Racing)!.Name);

		Assert.True(book.Rename(a.Id, "Shooters"));
		Assert.Equal("Shooters", book.Find(a.Id)!.Name);
		Assert.False(book.Rename(ProfileBook.DefaultId, "Mine"));
		Assert.False(book.Rename(a.Id, " "));

		ControllerProfile copy = book.Duplicate(a.Id, "Shooters Copy")!;
		Assert.NotEqual(a.Id, copy.Id);
		Assert.Equal(Racing, copy.Values);
		Assert.Equal(4, book.Profiles.Count);

		Assert.False(book.Delete(ProfileBook.DefaultId));
		book.Activate(a.Id);
		book.AssignGame("Returnal", a.Id);
		Assert.True(book.Delete(a.Id));
		Assert.Null(book.Find(a.Id));
		Assert.Equal(ProfileBook.DefaultId, book.ActiveId);
		Assert.Null(book.ProfileForGame("Returnal"));
		Assert.True(changes >= 6);
	}

	[Fact]
	public void Names_AreCappedInLength()
	{
		var book = new ProfileBook();
		Assert.Equal(ProfileBook.MaxNameLength, book.Create(new string('x', 100), Racing)!.Name.Length);
	}

	[Fact]
	public void Update_StoresValues_OnlyWhenChanged()
	{
		var book = new ProfileBook();
		int changes = 0;
		book.Changed += () => changes++;
		Assert.True(book.Update(ProfileBook.DefaultId, Racing));
		Assert.True(book.Update(ProfileBook.DefaultId, Racing));
		Assert.Equal(1, changes);
		Assert.Equal(Racing, book.Default.Values);
		Assert.False(book.Update("missing", Racing));
	}

	[Fact]
	public void Step_WrapsAround()
	{
		var book = new ProfileBook();
		string a = book.Create("A", Racing)!.Id;
		string b = book.Create("B", Racing)!.Id;
		Assert.Equal(a, book.Step(1));
		Assert.Equal(b, book.Step(-1));
		book.Activate(b);
		Assert.Equal(ProfileBook.DefaultId, book.Step(1));
		Assert.Equal(3, book.IndexOf(b));
	}

	[Fact]
	public void Games_UseTheirProfile_OthersTheDefault()
	{
		var book = new ProfileBook();
		string a = book.Create("A", Racing)!.Id;
		book.AssignGame("Returnal", a);
		Assert.Equal(a, book.ResolveForGame("returnal"));
		Assert.Equal(ProfileBook.DefaultId, book.ResolveForGame("Cyberpunk 2077"));
		Assert.Equal(ProfileBook.DefaultId, book.ResolveForGame(null));
		book.AssignGame("Returnal", ProfileBook.DefaultId);
		Assert.Null(book.ProfileForGame("Returnal"));
		book.AssignGame("Returnal", "unknown");
		Assert.Empty(book.GameProfiles);
	}

	[Fact]
	public void ExportImport_RoundTrips_AsANewProfile()
	{
		var book = new ProfileBook();
		ControllerProfile a = book.Create("Racing", Racing)!;
		string json = book.Export(a.Id);
		Assert.Contains("untethered-profile", json);

		var other = new ProfileBook();
		ControllerProfile imported = other.Import(json, "file")!;
		Assert.Equal("Racing", imported.Name);
		Assert.Equal(Racing, imported.Values);
		Assert.Equal(Racing.Tuning, imported.Values.Tuning);

		// Into the same book: a new id and a unique name.
		ControllerProfile again = book.Import(json, "file")!;
		Assert.NotEqual(a.Id, again.Id);
		Assert.Equal("Racing (2)", again.Name);
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("{}")]
	[InlineData("{\"format\":\"something-else\",\"version\":1,\"name\":\"x\",\"values\":{}}")]
	public void Import_RejectsOtherFiles(string json)
	{
		var book = new ProfileBook();
		Assert.Null(book.Import(json, "file"));
		Assert.Single(book.Profiles);
	}

	[Fact]
	public void Import_WithoutAName_UsesTheFileName()
	{
		var book = new ProfileBook();
		ControllerProfile p = book.Import("{\"format\":\"untethered-profile\",\"version\":1,\"name\":\"\",\"values\":{\"gameHapticsLevel\":120}}", "My file")!;
		Assert.Equal("My file", p.Name);
		Assert.Equal(120, p.Values.GameHapticsLevel);
		Assert.Equal(100, p.Values.TriggerStrength);
	}

	[Fact]
	public void Load_WithoutSavedProfiles_MakesTheLegacySettingsTheDefault()
	{
		ProfileBook book = ProfileBook.Load(null, null, null, Racing);
		Assert.Equal(Racing, Assert.Single(book.Profiles).Values);
		Assert.Equal(ProfileBook.DefaultId, book.ActiveId);
	}

	[Fact]
	public void Load_KeepsSavedProfiles_Active_AndValidGames()
	{
		ControllerProfile def = new(ProfileBook.DefaultId, "Default", new ProfileValues());
		ControllerProfile a = new("a1", "Racing", Racing);
		ProfileBook book = ProfileBook.Load([def, a, a with { Name = "dup" }], "a1",
			new Dictionary<string, string> { ["Returnal"] = "a1", ["Gone"] = "zzz" }, Racing);
		Assert.Equal(2, book.Profiles.Count);
		Assert.Equal(new ProfileValues(), book.Default.Values);
		Assert.Equal("a1", book.ActiveId);
		Assert.Equal("a1", book.ProfileForGame("Returnal"));
		Assert.Null(book.ProfileForGame("Gone"));

		// An unknown active id falls back to the default.
		Assert.Equal(ProfileBook.DefaultId, ProfileBook.Load([def, a], "zzz", null, Racing).ActiveId);
	}
}
