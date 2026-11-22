// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;
using Kopru.Core.Steam;
using Xunit;

namespace Kopru.Core.Tests;

public sealed class SteamInputTests : IDisposable
{
	private sealed class FakeSteam(string? path) : ISteamEnvironment
	{
		public string? SteamPath { get; } = path;
		public bool IsSteamRunning { get; set; }
		public bool FileExists(string p) => File.Exists(p);
		public string ReadAllText(string p) => File.ReadAllText(p);
		public IEnumerable<string> GetDirectories(string p) => Directory.Exists(p) ? Directory.GetDirectories(p) : [];
		public DateTime GetLastWriteTimeUtc(string p) => File.GetLastWriteTimeUtc(p);
	}

	private const string ConfigFixture = """
		"InstallConfigStore"
		{
			"Software"
			{
				"Valve"
				{
					"Steam"
					{
						"SteamController_PSSupport"		"{0}"
						"BaseInstallFolder_1"		"D:\\SteamLibrary"
					}
				}
			}
		}
		""";

	private const string LocalFixture = """
		// user config
		"UserLocalConfigStore"
		{
			"Apps"
			{
				"1091500"
				{
					"UseSteamControllerConfig"		"{0}"
				}
			}
		}
		""";

	private readonly string _dir = Path.Combine(Path.GetTempPath(), "kopru-steam-" + Guid.NewGuid().ToString("N"));

	public SteamInputTests()
	{
		Directory.CreateDirectory(Path.Combine(_dir, "config"));
		File.WriteAllText(Path.Combine(_dir, "steam.exe"), "");
	}

	public void Dispose() => Directory.Delete(_dir, true);

	private void WriteConfig(string global) =>
		File.WriteAllText(Path.Combine(_dir, "config", "config.vdf"), ConfigFixture.Replace("{0}", global));

	private void WriteLocal(string perApp)
	{
		string dir = Path.Combine(_dir, "userdata", "12345", "config");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "localconfig.vdf"), LocalFixture.Replace("{0}", perApp));
	}

	private SteamInputCheck Check(bool running = false) => new(new FakeSteam(_dir) { IsSteamRunning = running });

	[Fact]
	public void Parser_reads_nested_keys_escapes_and_comments()
	{
		VdfNode root = Vdf.Parse("""
			// comment
			"a" { "B" "x\\y" "q" "say \"hi\"" sub { k v } "c" "1" [$WIN32] }
			""");
		Assert.Equal(@"x\y", root.GetValue("a", "b"));
		Assert.Equal("say \"hi\"", root.GetValue("A", "q"));
		Assert.Equal("v", root.GetValue("a", "sub", "k"));
		Assert.Equal("1", root.GetValue("a", "c"));
		Assert.Null(root.GetValue("a", "missing"));
		Assert.Throws<VdfFormatException>(() => Vdf.Parse("\"a\" { \"b\" \"c\""));
		Assert.Throws<VdfFormatException>(() => Vdf.Parse("\"a\" \"unterminated"));
	}

	[Fact]
	public void Not_installed_without_steam_path()
	{
		Assert.Equal(SteamInputStatus.NotInstalled, new SteamInputCheck(new FakeSteam(null)).Check().Status);
		Assert.Equal(SteamInputStatus.NotInstalled, new SteamInputCheck(new FakeSteam(Path.Combine(_dir, "nope"))).Check().Status);
	}

	[Theory]
	[InlineData("0", SteamInputStatus.Off)]
	[InlineData("1", SteamInputStatus.OnGlobally)]
	[InlineData("2", SteamInputStatus.OnGlobally)]
	public void Global_setting_is_read(string value, SteamInputStatus expected)
	{
		WriteConfig(value);
		Assert.Equal(expected, Check().Check("1091500").Status);
	}

	[Fact]
	public void Missing_or_broken_config_is_unknown()
	{
		Assert.Equal(SteamInputStatus.Unknown, Check().Check().Status);
		File.WriteAllText(Path.Combine(_dir, "config", "config.vdf"), "\"InstallConfigStore\" {");
		Assert.Equal(SteamInputStatus.Unknown, Check().Check().Status);
	}

	[Theory]
	[InlineData("0", "2", SteamInputStatus.Off)]
	[InlineData("2", "0", SteamInputStatus.OnForApp)]
	[InlineData("1", "2", SteamInputStatus.OnGlobally)]
	public void Per_app_override_wins(string perApp, string global, SteamInputStatus expected)
	{
		WriteConfig(global);
		WriteLocal(perApp);
		Assert.Equal(expected, Check().Check("1091500").Status);
		Assert.Equal(global == "0" ? SteamInputStatus.Off : SteamInputStatus.OnGlobally, Check().Check("999").Status);
	}

	[Fact]
	public void Reports_running_and_localized_advice()
	{
		WriteConfig("2");
		SteamInputResult result = Check(running: true).Check();
		Assert.True(result.SteamRunning);
		Assert.False(string.IsNullOrWhiteSpace(result.Advice));
		foreach (SteamInputStatus s in Enum.GetValues<SteamInputStatus>())
		{
			Assert.NotEqual(Text.SteamInputAdvice(s, Language.Turkish), Text.SteamInputAdvice(s, Language.English));
		}
	}
}
