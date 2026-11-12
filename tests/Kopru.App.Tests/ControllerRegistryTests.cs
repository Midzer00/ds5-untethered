// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Remembered controllers: the registry (seen, renamed, forgotten, bounded), its place in settings.json, and the names
// in every view model that shows a controller. All ids are fake.
public class ControllerRegistryTests
{
	private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

	private static ControllerEntry Entry(string id, int slot = 1, double battery = 78, bool edge = false, bool hasBattery = true) =>
		new(id, slot, edge ? "DualSense Edge" : "DualSense", edge, hasBattery, battery, false, BridgeState.Stopped);

	private static void Settle() => Dispatcher.UIThread.RunJobs();

	[Fact]
	public void Observe_RemembersJoins_AndStampsLeaves()
	{
		DateTimeOffset now = T0;
		ControllerRegistry r = new() { Clock = () => now };
		int changes = 0;
		r.Changed += () => changes++;

		r.Observe([Entry("fake-pad-a", battery: 64), Entry("fake-pad-b", 2, edge: true, hasBattery: false)]);
		Assert.Equal(1, changes);
		KnownController a = r.Find("fake-pad-a")!;
		Assert.Equal(ControllerModel.DualSense, a.Model);
		Assert.Equal(T0, a.FirstSeen);
		Assert.Equal(64, a.LastBattery);
		Assert.Equal(ControllerModel.DualSenseEdge, r.Find("fake-pad-b")!.Model);
		Assert.Null(r.Find("fake-pad-b")!.LastBattery);
		Assert.True(r.IsConnected("fake-pad-a"));

		// Nothing new: no change (the service raises StateChanged all the time).
		now = T0.AddMinutes(5);
		r.Observe([Entry("fake-pad-a", battery: 64), Entry("fake-pad-b", 2, edge: true, hasBattery: false)]);
		Assert.Equal(1, changes);

		// A battery reading is kept; b leaves and gets its last seen time.
		now = T0.AddMinutes(10);
		r.Observe([Entry("fake-pad-a", battery: 60)]);
		Assert.Equal(2, changes);
		Assert.Equal(60, r.Find("fake-pad-a")!.LastBattery);
		Assert.Equal(T0, r.Find("fake-pad-a")!.LastSeen);
		Assert.Equal(T0.AddMinutes(10), r.Find("fake-pad-b")!.LastSeen);
		Assert.False(r.IsConnected("fake-pad-b"));
		// Connected ones first.
		Assert.Equal(["fake-pad-a", "fake-pad-b"], r.Items.Select(i => i.Id));

		// a leaves (stamped), b comes back (stamped), first seen stays.
		now = T0.AddHours(2);
		r.Observe([Entry("fake-pad-b", 2, edge: true, battery: 30)]);
		Assert.Equal(T0.AddHours(2), r.Find("fake-pad-a")!.LastSeen);
		Assert.Equal(T0.AddHours(2), r.Find("fake-pad-b")!.LastSeen);
		Assert.Equal(T0, r.Find("fake-pad-b")!.FirstSeen);
		Assert.Equal(30, r.Find("fake-pad-b")!.LastBattery);
	}

	[Theory]
	[InlineData("  Ömer'in Kumandası  ", "Ömer'in Kumandası")]
	[InlineData("", null)]
	[InlineData("   ", null)]
	[InlineData(null, null)]
	[InlineData("DualSense", null)]
	[InlineData("Couch\tPad", "CouchPad")]
	[InlineData("A very long controller name that goes on", "A very long controller n")]
	public void Rename_TrimsLimitsAndFallsBackToTheModel(string? input, string? expected)
	{
		ControllerRegistry r = new() { Clock = () => T0 };
		r.Observe([Entry("fake-pad-a")]);
		r.Rename("fake-pad-a", "Something");
		r.Rename("fake-pad-a", input);
		Assert.Equal(expected, r.Find("fake-pad-a")!.Name);
		Assert.Equal(expected ?? "DualSense", r.NameOf("fake-pad-a", false));
		Assert.True((expected?.Length ?? 0) <= ControllerRegistry.MaxNameLength);
	}

	[Fact]
	public void Rename_UnknownOrSame_ChangesNothing()
	{
		ControllerRegistry r = new() { Clock = () => T0 };
		r.Observe([Entry("fake-pad-a")]);
		int changes = 0;
		r.Changed += () => changes++;
		Assert.False(r.Rename("fake-unknown", "X"));
		Assert.True(r.Rename("fake-pad-a", "Couch"));
		Assert.False(r.Rename("fake-pad-a", " Couch "));
		Assert.Equal(1, changes);
		Assert.Equal("DualSense Edge", r.NameOf("fake-unknown", true));
	}

	[Fact]
	public void Forget_NotWhileConnected()
	{
		ControllerRegistry r = new() { Clock = () => T0 };
		List<string> forgotten = [];
		r.Forgotten += forgotten.Add;
		r.Observe([Entry("fake-pad-a")]);
		Assert.False(r.Forget("fake-pad-a"));
		Assert.NotNull(r.Find("fake-pad-a"));

		r.Observe([]);
		Assert.True(r.Forget("fake-pad-a"));
		Assert.Null(r.Find("fake-pad-a"));
		Assert.Equal(["fake-pad-a"], forgotten);
		Assert.False(r.Forget("fake-pad-a"));
	}

	[Fact]
	public void Bounded_ForgetsTheOneSeenLongestAgo_NeverAConnectedOne()
	{
		DateTimeOffset now = T0;
		ControllerRegistry r = new() { Clock = () => now };
		List<string> forgotten = [];
		r.Forgotten += forgotten.Add;
		for (int i = 0; i < ControllerRegistry.MaxRemembered; i++)
		{
			now = T0.AddMinutes(i);
			r.Observe([Entry($"fake-pad-{i:00}")]);
		}
		r.Observe([]);
		Assert.Equal(ControllerRegistry.MaxRemembered, r.Items.Count);

		now = T0.AddDays(1);
		r.Observe([Entry("fake-pad-new")]);
		Assert.Equal(ControllerRegistry.MaxRemembered, r.Items.Count);
		Assert.Null(r.Find("fake-pad-00"));
		Assert.NotNull(r.Find("fake-pad-new"));
		Assert.Equal(["fake-pad-00"], forgotten);

		// Loading more than the bound from a file keeps the newest.
		List<KnownController> many = [.. Enumerable.Range(0, 20).Select(i => new KnownController($"fake-{i}", ControllerModel.DualSense, null, T0, T0.AddMinutes(i)))];
		ControllerRegistry loaded = new(many);
		Assert.Equal(ControllerRegistry.MaxRemembered, loaded.Items.Count);
		Assert.Null(loaded.Find("fake-0"));
		Assert.NotNull(loaded.Find("fake-19"));
	}

	[Fact]
	public void SettingsJson_RoundTrips_AndAnOlderFileHasNone()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-registry-" + Guid.NewGuid().ToString("N"));
		try
		{
			string path = Path.Combine(dir, "settings.json");
			Directory.CreateDirectory(dir);
			// An older settings file: no list yet.
			File.WriteAllText(path, """{ "GameHapticsLevel": 120, "ShortcutsEnabled": true }""");
			JsonSettingsStore store = new(path);
			AppSettings old = store.Load()!;
			Assert.Null(old.KnownControllers);
			Assert.Empty(new ControllerRegistry(old.KnownControllers).Items);

			old.KnownControllers =
			[
				new KnownController("fake-pad-a", ControllerModel.DualSenseEdge, "Couch Pad", T0, T0.AddHours(1), 55),
				new KnownController("fake-pad-b", ControllerModel.DualSense, null, T0, T0)
			];
			store.Save(old);
			string json = File.ReadAllText(path);
			Assert.Contains("\"DualSenseEdge\"", json);
			Assert.DoesNotContain("DisplayName", json);

			AppSettings back = store.Load()!;
			Assert.Equal(old.KnownControllers, back.KnownControllers);
			Assert.Equal(120, back.GameHapticsLevel);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[AvaloniaFact]
	public void SettingsState_RemembersFromTheService_SavesAndLoadsAgain()
	{
		FakeControllerService fake = new(animate: false);
		MemorySettingsStore store = new(new AppSettings());
		MainWindowViewModel vm = new(fake, new AppEnvironment(store));
		vm.Settings.KnownControllers.Clock = () => T0;
		fake.ControllerList.Add(Entry("fake-pad-a", battery: 70));
		fake.RaiseStateChanged();
		Settle();
		Assert.NotNull(vm.Settings.KnownControllers.Find("fake-pad-a"));
		Assert.True(vm.Settings.RenameController("fake-pad-a", "Couch Pad"));
		vm.Settings.SaveNow();
		KnownController saved = Assert.Single(store.Saved!.KnownControllers!);
		Assert.Equal("Couch Pad", saved.Name);
		Assert.Equal(70, saved.LastBattery);

		// Another start: the name is back before the controller connects.
		MainWindowViewModel again = new(new FakeControllerService(animate: false), new AppEnvironment(store));
		Assert.Equal("Couch Pad", again.Settings.ControllerName("fake-pad-a", false));
	}

	[AvaloniaFact]
	public void Forget_AlsoDropsTheControllersOwnProfile()
	{
		FakeControllerService fake = new(animate: false);
		MainWindowViewModel vm = new(fake);
		fake.ControllerList.Add(Entry("fake-pad-a"));
		fake.ControllerList.Add(Entry("fake-pad-b", 2));
		fake.RaiseStateChanged();
		Settle();
		var solo = vm.Settings.Profiles.Create("Solo", vm.Settings.CaptureValues())!;
		vm.Settings.AssignControllerProfile("fake-pad-b", solo.Id);

		KnownControllerRowViewModel row = vm.Settings.ControllersPanel.Rows.Single(r => r.Id == "fake-pad-b");
		Assert.False(row.CanForget);
		row.ForgetCommand.Execute(null);
		Assert.False(row.IsConfirming);

		fake.ControllerList.RemoveAt(1);
		fake.RaiseStateChanged();
		Settle();
		Assert.True(row.CanForget);
		Assert.True(row.HasProfile);
		row.ForgetCommand.Execute(null);
		Assert.True(row.IsConfirming);
		row.CancelForgetCommand.Execute(null);
		Assert.NotNull(vm.Settings.KnownControllers.Find("fake-pad-b"));

		row.ForgetCommand.Execute(null);
		row.ConfirmForgetCommand.Execute(null);
		Settle();
		Assert.Null(vm.Settings.KnownControllers.Find("fake-pad-b"));
		Assert.Null(vm.Settings.ControllerProfileId("fake-pad-b"));
		Assert.Null(fake.ControllerProfiles["fake-pad-b"]);
		Assert.DoesNotContain(vm.Settings.ControllersPanel.Rows, r => r.Id == "fake-pad-b");
	}

	[AvaloniaFact]
	public void Name_ShowsInTheSidebar_TheHero_TheList_TheChips_AndNotifications()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			FakeControllerService fake = new(animate: false);
			fake.ControllerList.Add(Entry("fake-pad-a"));
			MainWindowViewModel vm = new(fake);
			List<AppNotification> raised = [];
			vm.Notifications.Raised += raised.Add;
			Settle();
			Assert.Equal("DualSense", vm.Picker.SelectedName);

			// Renamed from the sidebar's pencil.
			ControllerNameViewModel editor = vm.Picker.NameEditor;
			Assert.True(editor.CanEdit);
			editor.EditCommand.Execute(null);
			Assert.True(editor.IsEditing);
			Assert.Equal("DualSense", editor.Draft);
			editor.Draft = "  Couch Pad ";
			editor.Save();
			Settle();
			Assert.False(editor.IsEditing);
			Assert.Equal("Couch Pad", vm.Picker.SelectedName);
			Assert.Equal("Couch Pad", vm.Overview.Picker.SelectedName);
			Assert.Equal("Couch Pad", vm.Settings.ControllersPanel.Rows.Single().NameEditor.Name);

			// Esc: nothing changes.
			editor.EditCommand.Execute(null);
			editor.Draft = "Other";
			editor.Cancel();
			Assert.Equal("Couch Pad", vm.Picker.SelectedName);

			// A second controller joins: the chips and the connect notification use the names.
			vm.Settings.KnownControllers.Observe([Entry("fake-pad-a"), Entry("fake-pad-b", 2, edge: true)]);
			vm.Settings.RenameController("fake-pad-b", "Guest");
			fake.ControllerList.Add(Entry("fake-pad-b", 2, edge: true));
			fake.RaiseStateChanged();
			Settle();
			Assert.Equal(["Couch Pad", "Guest"], vm.Picker.Items.Select(i => i.Name));
			Assert.Equal(["Couch Pad", "Guest"], vm.Overview.Picker.Items.Select(i => i.Name));
			Assert.Contains(raised, n => n.Body == Text.NotifyConnectedBody("Guest"));

			// Back to the model name when emptied in the list.
			ControllerNameViewModel listed = vm.Settings.ControllersPanel.Rows.Single(r => r.Id == "fake-pad-b").NameEditor;
			listed.EditCommand.Execute(null);
			listed.Draft = "   ";
			listed.Save();
			Settle();
			Assert.Equal("DualSense Edge", vm.Picker.Items[1].Name);

			// No controller: nothing to rename.
			fake.ControllerList.Clear();
			fake.IsConnected = false;
			fake.RaiseStateChanged();
			Settle();
			Assert.False(vm.Picker.NameEditor.CanEdit);
			Assert.Equal(Text.DeviceNoController(), vm.Picker.SelectedName);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public void Row_SaysConnectedOrWhenLastSeen_AndTheLastBattery()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			DateTimeOffset now = T0;
			FakeControllerService fake = new(animate: false);
			MainWindowViewModel vm = new(fake);
			vm.Settings.KnownControllers.Clock = () => now;
			vm.Settings.KnownControllers.Observe([Entry("fake-pad-a", battery: 42)]);
			Settle();
			KnownControllerRowViewModel row = vm.Settings.ControllersPanel.Rows.Single();
			Assert.Equal("Connected", row.StatusText);
			Assert.Equal("Last Battery: 42%", row.BatteryText);
			Assert.Equal("DualSense", row.ModelText);

			vm.Settings.KnownControllers.Observe([]);
			now = T0.AddHours(2).AddMinutes(10);
			Settle();
			Assert.Same(row, vm.Settings.ControllersPanel.Rows.Single());
			row.Update(vm.Settings.KnownControllers.Find("fake-pad-a")!, false);
			Assert.Equal("Last Seen: 2 h ago", row.StatusText);
			Assert.Equal("Son Görülme: 2 sa önce", Text.ControllerLastSeen(Text.AgoHours(2, Language.Turkish), Language.Turkish));
			Assert.Equal(Text.AgoJustNow(), KnownControllerRowViewModel.Ago(T0, T0.AddSeconds(20)));
			Assert.Equal(Text.AgoMinutes(5), KnownControllerRowViewModel.Ago(T0, T0.AddMinutes(5.5)));
			Assert.Equal(Text.AgoDays(3), KnownControllerRowViewModel.Ago(T0, T0.AddDays(3.2)));
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public void EmptyList_ShowsOneLine()
	{
		MainWindowViewModel vm = new(new FakeControllerService(animate: false));
		Assert.True(vm.Settings.ControllersPanel.IsEmpty);
		Assert.False(vm.Settings.ControllersPanel.HasRows);
		Assert.False(string.IsNullOrWhiteSpace(vm.Settings.ControllersPanel.EmptyText));
	}

	// Renders the Settings page's Controllers tab with two remembered controllers, one renamed and connected, and the
	// sidebar card with that name (the picture in docs/screens: SettingsFitTests, settings-controllers-*.png).
	[AvaloniaFact]
	public void RenderControllersSection()
	{
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				FakeControllerService fake = new(animate: false);
				MainWindowViewModel vm = new(fake);
				vm.Sticks.StopPreview();
				ControllerRegistry registry = vm.Settings.KnownControllers;
				DateTimeOffset now = T0;
				registry.Clock = () => now;
				registry.Observe([Entry("fake-pad-b", 2, battery: 41, edge: true)]);
				now = T0.AddHours(22);
				registry.Observe([]);
				now = T0.AddDays(1);
				registry.Observe([Entry("fake-pad-a", battery: 78)]);
				vm.Settings.RenameController("fake-pad-a", language == Language.Turkish ? "Salon Kumandası" : "Living Room Pad");
				now = T0.AddDays(1).AddHours(2);
				var racing = vm.Settings.Profiles.Create(language == Language.Turkish ? "Yarış" : "Racing", vm.Settings.CaptureValues())!;
				vm.Settings.AssignControllerProfile("fake-pad-b", racing.Id);
				fake.ControllerList.Add(Entry("fake-pad-a", battery: 78));
				fake.RaiseStateChanged();
				vm.OpenSettingsAt(SettingsViewModel.ControllersTab);
				var window = new Kopru.App.Views.MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				for (int i = 0; i < 4; i++)
				{
					Settle();
					Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				}
				Thread.Sleep(320);
				Settle();
				Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
				Assert.NotNull(frame);
				Assert.True(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Border>().Single(b => b.Name == "ControllersGroup").IsEffectivelyVisible);
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Kopru.sln not found");
	}

	[Fact]
	public async Task DiagnosticsExport_HasNoAddressFromARememberedController()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-diag-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(dir);
			string settings = Path.Combine(dir, "settings.json");
			// A device instance id with an address-shaped part (fake).
			const string id = @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\8&1A2B3C4D&0&00DEADBEEF01_C00000000";
			new JsonSettingsStore(settings).Save(new AppSettings
			{
				KnownControllers = [new KnownController(id, ControllerModel.DualSense, "Couch Pad", T0, T0, 50)],
				ControllerProfiles = new() { [id] = "fake-profile" }
			});
			string zip = Path.Combine(dir, "out.zip");
			await DiagnosticsExport.CreateZipAsync(zip, new DiagnosticsSources { SettingsPath = settings });
			using ZipArchive archive = ZipFile.OpenRead(zip);
			using StreamReader reader = new(archive.GetEntry("settings.json")!.Open());
			string text = await reader.ReadToEndAsync();
			Assert.Contains("KnownControllers", text);
			Assert.Contains("Couch Pad", text);
			Assert.DoesNotContain("00DEADBEEF01", text);
			Assert.DoesNotContain("DEADBEEF", text, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}
}
