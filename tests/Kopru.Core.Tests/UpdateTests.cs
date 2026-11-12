// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kopru.Core.Updates;

namespace Kopru.Core.Tests;

public sealed class UpdateTests
{
	private const string Prefix = UpdateEndpoints.OfficialDownloadPrefix;
	private static readonly UpdateEndpoints Endpoints = UpdateEndpoints.Official;
	private static readonly AppVersion Current = AppVersion.Parse("0.1.5")!;
	private const string Folder = @"X:\data\updates";

	private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

	private static byte[] Payload(int size) => Enumerable.Range(0, size).Select(i => (byte)(i * 7)).ToArray();

	public static string Manifest(string version = "0.1.6", string? setupUrl = null, string? sha = null, long size = 1000, string? page = null,
		string extra = "", string notes = """{ "tr": "Yeni özellik.", "en": "A new feature." }""") =>
		$$"""
		{
		  "version": "{{version}}",
		  "published": "2026-11-20",
		  "setup": { "url": "{{setupUrl ?? Prefix + "v" + version + "/DS5-Untethered-" + version + "-Setup.exe"}}", "sha256": "{{sha ?? new string('A', 64)}}", "size": {{size}} },
		  "zip": { "url": "{{Prefix}}v{{version}}/DS5-Untethered-{{version}}-win-x64.zip", "sha256": "{{new string('b', 64)}}", "size": 2000 },
		  "page": "{{page ?? "https://github.com/Midzer00/ds5-untethered/releases/tag/v" + version}}",
		  "notes": {{notes}}{{extra}}
		}
		""";

	// --- Versions ---

	[Theory]
	[InlineData("0.1.6", "0.1.5", 1)]
	[InlineData("0.1.10", "0.1.9", 1)]
	[InlineData("1.0", "0.9.9", 1)]
	[InlineData("0.2", "0.2.0", 0)]
	[InlineData("0.1.5", "0.1.5.0", 0)]
	[InlineData("0.1.4", "0.1.5", -1)]
	[InlineData("2.0.0.1", "2.0.0", 1)]
	public void Versions_CompareNumerically(string a, string b, int sign) =>
		Assert.Equal(sign, Math.Sign(AppVersion.Parse(a)!.CompareTo(AppVersion.Parse(b)!)));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("v0.1.6")]
	[InlineData("0.1.6-beta")]
	[InlineData("1")]
	[InlineData("1.2.3.4.5")]
	[InlineData("1..2")]
	[InlineData("1.-2.3")]
	[InlineData("99999999999.0")]
	[InlineData(" 1 .2")]
	public void Versions_RejectGarbage(string? text) => Assert.Null(AppVersion.Parse(text));

	[Fact]
	public void Version_PrintsThreeParts() => Assert.Equal("0.1.6", AppVersion.Parse("0.1.6.0")!.ToString());

	// --- Manifest ---

	[Fact]
	public void Manifest_Valid_IsRead()
	{
		UpdateManifest m = UpdateManifest.Parse(Manifest(sha: new string('c', 64), extra: ", \"minimumVersion\": \"0.1.0\""), Endpoints, out string? problem)!;
		Assert.Null(problem);
		Assert.Equal("0.1.6", m.Version.ToString());
		Assert.Equal("2026-11-20", m.Published);
		Assert.Equal(new string('C', 64), m.Setup.Sha256);
		Assert.Equal(1000, m.Setup.Size);
		Assert.Equal("DS5-Untethered-0.1.6-Setup.exe", m.Setup.FileName);
		Assert.Equal(2000, m.Zip!.Size);
		Assert.Equal("Yeni özellik.", m.Notes(Localization.Language.Turkish));
		Assert.Equal("A new feature.", m.Notes(Localization.Language.English));
		Assert.True(m.CanUpdateInPlace(Current));
		Assert.False(m.CanUpdateInPlace(AppVersion.Parse("0.0.9")!));
	}

	[Theory]
	[InlineData("not json at all")]
	[InlineData("[]")]
	[InlineData("{}")]
	[InlineData("""{ "version": "0.1.6" }""")]
	public void Manifest_Garbage_IsRejected(string json) => Assert.Null(UpdateManifest.Parse(json, Endpoints, out _));

	public static TheoryData<string, string> BadManifests => new()
	{
		{ "bad version", Manifest(version: "next") },
		{ "http", Manifest(setupUrl: "http://github.com/Midzer00/ds5-untethered/releases/download/v0.1.6/Setup.exe") },
		{ "other host", Manifest(setupUrl: "https://example.com/Midzer00/ds5-untethered/releases/download/v0.1.6/Setup.exe") },
		{ "other repository", Manifest(setupUrl: "https://github.com/someone/else/releases/download/v0.1.6/Setup.exe") },
		{ "look-alike host", Manifest(setupUrl: "https://github.com.example.net/Midzer00/ds5-untethered/releases/download/v0.1.6/Setup.exe") },
		{ "path escape", Manifest(setupUrl: Prefix + "../../../other/Setup.exe") },
		{ "user info", Manifest(setupUrl: "https://x@github.com/Midzer00/ds5-untethered/releases/download/v0.1.6/Setup.exe") },
		{ "query string", Manifest(setupUrl: Prefix + "v0.1.6/Setup.exe?x=1") },
		{ "short hash", Manifest(sha: "ABC") },
		{ "not hex", Manifest(sha: new string('z', 64)) },
		{ "zero size", Manifest(size: 0) },
		{ "too large", Manifest(size: UpdateManifest.MaxAssetBytes + 1) },
		{ "page elsewhere", Manifest(page: "https://example.com/download") },
		{ "bad minimum", Manifest(extra: ", \"minimumVersion\": \"soon\"") },
	};

	[Theory]
	[MemberData(nameof(BadManifests))]
	public void Manifest_WithABadField_IsRejected(string why, string json) =>
		Assert.True(UpdateManifest.Parse(json, Endpoints, out _) == null, why);

	[Fact]
	public void Manifest_MissingNotes_AreEmpty_AndNotesAreCleaned()
	{
		Assert.Equal(string.Empty, UpdateManifest.Parse(Manifest(notes: "null"), Endpoints, out _)!.NotesEn);
		string long_ = new('x', 600);
		UpdateManifest m = UpdateManifest.Parse(Manifest(notes: $$"""{ "tr": "  bir\r\n\n iki\u0007 ", "en": "{{long_}}" }"""), Endpoints, out _)!;
		Assert.Equal("bir\niki", m.NotesTr);
		Assert.True(m.NotesEn.Length <= UpdateManifest.MaxNotesChars + 1);
	}

	// The update.json committed at the repository root is a valid manifest of an existing release.
	[Fact]
	public void CommittedManifest_IsValid()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		Assert.NotNull(dir);
		UpdateManifest? m = UpdateManifest.Parse(File.ReadAllText(Path.Combine(dir!, UpdateEndpoints.ManifestPath)), Endpoints, out string? problem);
		Assert.True(m != null, problem);
		Assert.Equal($"DS5-Untethered-{m!.Version}-Setup.exe", m.Setup.FileName);
		Assert.Equal($"{Prefix}v{m.Version}/DS5-Untethered-{m.Version}-Setup.exe", m.Setup.Url);
		Assert.Equal($"DS5-Untethered-{m.Version}-win-x64.zip", m.Zip!.FileName);
	}

	[Fact]
	public void Endpoints_PointAtTheRepository()
	{
		Assert.Equal("https://raw.githubusercontent.com/Midzer00/ds5-untethered/main/update.json", UpdateEndpoints.OfficialManifestUrl);
		Assert.True(Endpoints.IsAllowedDownload(Prefix + "v0.1.6/DS5-Untethered-0.1.6-Setup.exe"));
		Assert.False(Endpoints.IsAllowedDownload(Prefix));
		Assert.False(Endpoints.IsAllowedDownload("http://127.0.0.1:8000/x.exe"));
	}

#if DEBUG
	// The rehearsal override (DEBUG builds only): a local test server may be plain http, anything else must be https.
	[Fact]
	public void RehearsalOverride_AllowsOnlyLoopbackHttp()
	{
		Assert.Same(UpdateEndpoints.Official, UpdateEndpoints.FromOverrides(null, " "));
		UpdateEndpoints local = UpdateEndpoints.FromOverrides("http://127.0.0.1:8000/update.json", "http://127.0.0.1:8000/");
		Assert.Equal("http://127.0.0.1:8000/update.json", local.ManifestUrl);
		Assert.True(local.IsAllowedDownload("http://127.0.0.1:8000/DS5-Untethered-0.1.6-Setup.exe"));
		Assert.False(local.IsAllowedDownload(Prefix + "v0.1.6/x.exe"));
		UpdateEndpoints remote = UpdateEndpoints.FromOverrides(null, "http://example.com/");
		Assert.False(remote.IsAllowedDownload("http://example.com/x.exe"));
	}
#endif

	// --- Schedule ---

	private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Schedule_FirstStart_Weekly_SwitchOff_AndRetryAfterFailure()
	{
		Assert.True(UpdateSchedule.IsDue(true, null, null, Now));
		Assert.False(UpdateSchedule.IsDue(false, null, null, Now));
		Assert.False(UpdateSchedule.IsDue(true, Now.AddDays(-6), null, Now));
		Assert.True(UpdateSchedule.IsDue(true, Now.AddDays(-7), null, Now));
		Assert.True(UpdateSchedule.IsDue(true, Now.AddDays(1), null, Now));
		Assert.False(UpdateSchedule.IsDue(true, null, Now.AddHours(-1), Now));
		Assert.True(UpdateSchedule.IsDue(true, null, Now - UpdateSchedule.RetryAfterFailure, Now));
		Assert.True(UpdateSchedule.StartDelay > TimeSpan.Zero);
	}

	// --- Check ---

	private static UpdateChecker Checker(Func<HttpRequestMessage, HttpResponseMessage> respond, List<HttpRequestMessage>? log = null, TimeSpan? timeout = null) =>
		new(Client(r =>
		{
			log?.Add(r);
			return Task.FromResult(respond(r));
		}), Endpoints, timeout);

	private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
	{
		var client = new HttpClient(new Handler((r, _) => respond(r)));
		client.DefaultRequestHeaders.UserAgent.ParseAdd("DS5Untethered/1.0");
		return client;
	}

	private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

	[Fact]
	public async Task Check_NewerManifest_IsAvailable_AndOnlyAPlainGetIsSent()
	{
		var log = new List<HttpRequestMessage>();
		UpdateCheckResult r = await Checker(_ => Ok(Manifest()), log).CheckAsync(Current);
		Assert.Equal(UpdateCheckStatus.Available, r.Status);
		Assert.Equal("0.1.6", r.Manifest!.Version.ToString());
		HttpRequestMessage request = Assert.Single(log);
		Assert.Equal(HttpMethod.Get, request.Method);
		Assert.Equal(UpdateEndpoints.OfficialManifestUrl, request.RequestUri!.AbsoluteUri);
		Assert.Empty(request.RequestUri.Query);
		Assert.Null(request.Content);
		Assert.Equal("DS5Untethered/1.0", Assert.Single(request.Headers.UserAgent).ToString());
	}

	[Theory]
	[InlineData("0.1.5")]
	[InlineData("0.1.4")]
	[InlineData("0.0.1")]
	public async Task Check_EqualOrOlder_IsUpToDate(string version) =>
		Assert.Equal(UpdateCheckStatus.UpToDate, (await Checker(_ => Ok(Manifest(version: version))).CheckAsync(Current)).Status);

	[Fact]
	public async Task Check_Failures_AreReportedNotThrown()
	{
		Assert.Equal(UpdateCheckError.NotFound, (await Checker(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).CheckAsync(Current)).Error);
		Assert.Equal(UpdateCheckError.Offline, (await Checker(_ => throw new HttpRequestException("no route")).CheckAsync(Current)).Error);
		Assert.Equal(UpdateCheckError.Offline, (await Checker(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)).CheckAsync(Current)).Error);
		Assert.Equal(UpdateCheckError.Invalid, (await Checker(_ => Ok("<html>oops</html>")).CheckAsync(Current)).Error);
		Assert.Equal(UpdateCheckError.Invalid, (await Checker(_ => Ok(new string(' ', UpdateManifest.MaxManifestBytes + 10))).CheckAsync(Current)).Error);
		var slow = new UpdateChecker(new HttpClient(new Handler(async (_, cancel) =>
		{
			await Task.Delay(TimeSpan.FromSeconds(30), cancel);
			return Ok(Manifest());
		})), Endpoints, TimeSpan.FromMilliseconds(100));
		Assert.Equal(UpdateCheckError.Timeout, (await slow.CheckAsync(Current)).Error);
	}

	// --- Download ---

	private static UpdateAsset Asset(byte[] data, string? sha = null, long? size = null) =>
		new(Prefix + "v0.1.6/DS5-Untethered-0.1.6-Setup.exe", sha ?? Hash(data), size ?? data.Length);

	private static UpdateDownloader Downloader(MemoryFiles files, Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, IInstallerSignatureCheck? signature = null) =>
		new(Client(respond), Endpoints, Folder, files, signature);

	// announced: -1 = the real length, null = none (a stream of unknown length), else that length.
	private static Func<HttpRequestMessage, Task<HttpResponseMessage>> Serve(byte[] data, long? announced = -1) => _ =>
	{
		HttpContent content = announced == null ? new StreamContent(new Unseekable(data)) : new ByteArrayContent(data);
		if (announced is long n && n != -1)
		{
			content.Headers.ContentLength = n;
		}
		return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
	};

	private static readonly AppVersion Next = AppVersion.Parse("0.1.6")!;

	[Fact]
	public async Task Download_Verified_ReportsProgress_AndLeavesOnlyTheInstaller()
	{
		byte[] data = Payload(300_000);
		var files = new MemoryFiles();
		var reported = new List<double>();
		DownloadResult r = await Downloader(files, Serve(data)).DownloadAsync(Asset(data), Next, new Inline(reported.Add));
		Assert.Equal(DownloadStatus.Verified, r.Status);
		Assert.Equal(Path.Combine(Folder, "DS5-Untethered-0.1.6-Setup.exe"), r.Path);
		Assert.Equal(data, files.Data[r.Path!]);
		Assert.Single(files.Data);
		Assert.Equal(100, reported[^1]);
		Assert.Equal(reported.Order(), reported);
	}

	[Fact]
	public async Task Download_SizeOrHashMismatch_IsRejectedAndDeleted()
	{
		byte[] data = Payload(5000);
		var files = new MemoryFiles();
		// Announced size differs from the manifest.
		Assert.Equal(DownloadStatus.Rejected, (await Downloader(files, Serve(data, 4000)).DownloadAsync(Asset(data, size: 5000), Next)).Status);
		// No announced size; more bytes than the manifest says.
		Assert.Equal(DownloadStatus.Rejected, (await Downloader(files, Serve(data, null)).DownloadAsync(Asset(data, size: 4000), Next)).Status);
		// Right size, wrong hash.
		Assert.Equal(DownloadStatus.Rejected, (await Downloader(files, Serve(data)).DownloadAsync(Asset(data, sha: new string('0', 64)), Next)).Status);
		Assert.Empty(files.Data);
	}

	[Fact]
	public async Task Download_Interrupted_IsRejected_AndAConnectionErrorIsOffline()
	{
		byte[] data = Payload(5000);
		var files = new MemoryFiles();
		// The stream ends early (no length announced).
		Assert.Equal(DownloadStatus.Rejected, (await Downloader(files, Serve(data[..3000], null)).DownloadAsync(Asset(data), Next)).Status);
		// The connection breaks in the middle.
		DownloadResult broken = await Downloader(files, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StreamContent(new BreakingStream(data[..2000]))
		})).DownloadAsync(Asset(data), Next);
		Assert.Equal(DownloadStatus.Offline, broken.Status);
		Assert.Empty(files.Data);
	}

	[Fact]
	public async Task Download_404_IsNotYetAvailable_AndOtherAddressesAreRefused()
	{
		var files = new MemoryFiles();
		byte[] data = Payload(10);
		Assert.Equal(DownloadStatus.NotYetAvailable,
			(await Downloader(files, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))).DownloadAsync(Asset(data), Next)).Status);
		int requests = 0;
		UpdateDownloader d = Downloader(files, r =>
		{
			requests++;
			return Serve(data)(r);
		});
		Assert.Equal(DownloadStatus.Rejected, (await d.DownloadAsync(Asset(data) with { Url = "https://example.com/Setup.exe" }, Next)).Status);
		Assert.Equal(DownloadStatus.Rejected, (await d.DownloadAsync(Asset(data) with { Size = UpdateManifest.MaxAssetBytes + 1 }, Next)).Status);
		Assert.Equal(0, requests);
	}

	[Fact]
	public async Task Download_SignatureSeam_CanReject()
	{
		byte[] data = Payload(100);
		var files = new MemoryFiles();
		DownloadResult r = await Downloader(files, Serve(data), new RejectAll()).DownloadAsync(Asset(data), Next);
		Assert.Equal(DownloadStatus.Rejected, r.Status);
		Assert.Empty(files.Data);
		Assert.Null(UnsignedBuilds.Instance.Check("any.exe"));
	}

	[Fact]
	public async Task Verify_ChangedFile_IsDeleted_AndCleanUpRemovesOldInstallers()
	{
		byte[] data = Payload(100);
		var files = new MemoryFiles();
		UpdateDownloader d = Downloader(files, Serve(data));
		string path = (await d.DownloadAsync(Asset(data), Next)).Path!;
		Assert.True(await d.VerifyAsync(path, Asset(data)));
		files.Data[path][0] ^= 0xFF;
		Assert.False(await d.VerifyAsync(path, Asset(data)));
		Assert.False(files.Exists(path));

		files.Data[Path.Combine(Folder, "DS5-Untethered-0.1.5-Setup.exe")] = [1];
		files.Data[Path.Combine(Folder, "DS5-Untethered-0.1.6-Setup.exe.part")] = [1];
		files.Data[Path.Combine(Folder, "install.log")] = [1];
		Assert.Equal(2, d.CleanUp());
		Assert.Equal([Path.Combine(Folder, "install.log")], files.Data.Keys);
	}

	// --- Install ---

	[Fact]
	public void InstallerArguments_AreSilent_WithTheUpdateSwitch()
	{
		IReadOnlyList<string> args = UpdateInstaller.Arguments("tr", @"X:\data\updates\install.log");
		Assert.Equal(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/NOCANCEL", "/UPDATE=1", "/LANG=tr", @"/LOG=X:\data\updates\install.log"], args);
		Assert.Contains("/LANG=en", UpdateInstaller.Arguments("de", "x"));
	}

	[Fact]
	public async Task Install_StopsTheBridge_HandsOver_StartsTheInstaller_ThenExits()
	{
		byte[] data = Payload(100);
		var files = new MemoryFiles();
		UpdateDownloader d = Downloader(files, Serve(data));
		string path = (await d.DownloadAsync(Asset(data), Next)).Path!;
		var steps = new List<string>();
		var launcher = new RecordingLauncher(steps);
		var installer = new UpdateInstaller(d, launcher, () =>
		{
			steps.Add("stop bridge");
			return Task.CompletedTask;
		}, () =>
		{
			steps.Add("hand over");
			return () => steps.Add("take back");
		}, () => steps.Add("exit"));

		Assert.Equal(InstallOutcome.Started, await installer.InstallAsync(path, Asset(data), "en"));
		Assert.Equal(["stop bridge", "hand over", "start " + path, "exit"], steps);
		Assert.Contains("/UPDATE=1", launcher.Arguments!);
		Assert.Contains("/LOG=" + Path.Combine(Folder, "install.log"), launcher.Arguments!);
	}

	[Fact]
	public async Task Install_ChangedFile_StartsNothing_AndALaunchFailureTakesTheLockBack()
	{
		byte[] data = Payload(100);
		var files = new MemoryFiles();
		UpdateDownloader d = Downloader(files, Serve(data));
		string path = (await d.DownloadAsync(Asset(data), Next)).Path!;
		var steps = new List<string>();
		UpdateInstaller Installer(IProcessLauncher l) => new(d, l, () =>
		{
			steps.Add("stop bridge");
			return Task.CompletedTask;
		}, () => () => steps.Add("take back"), () => steps.Add("exit"));

		Assert.Equal(InstallOutcome.LaunchFailed, await Installer(new FailingLauncher()).InstallAsync(path, Asset(data), "tr"));
		Assert.Equal(["stop bridge", "take back"], steps);

		steps.Clear();
		files.Data[path][5] ^= 1;
		Assert.Equal(InstallOutcome.Rejected, await Installer(new RecordingLauncher(steps)).InstallAsync(path, Asset(data), "tr"));
		Assert.Empty(steps);
		Assert.False(files.Exists(path));
	}

	[Theory]
	[InlineData(@"C:\Apps\DS5 Untethered\", true, true)]
	[InlineData(@"C:\Apps\DS5 Untethered", true, true)]
	[InlineData(@"C:\Apps\DS5 Untethered", false, false)]
	[InlineData(@"C:\Other\DS5 Untethered", true, false)]
	[InlineData(null, true, false)]
	public void InstallKind_NeedsTheUninstallEntryForThisFolder_AndTheUninstaller(string? registered, bool uninstaller, bool installed)
	{
		const string exeFolder = @"C:\Apps\DS5 Untethered\";
		var kind = new InnoInstallKind(exeFolder, () => registered, p => uninstaller && p == Path.Combine(exeFolder, InnoInstallKind.Uninstaller));
		Assert.Equal(installed, kind.IsInstalled);
	}

	[Fact]
	public void InstallKind_UnreadableRegistry_IsAZipCopy() =>
		Assert.False(new InnoInstallKind(@"C:\x", () => throw new System.Security.SecurityException(), _ => true).IsInstalled);

	// --- Settings backup ---

	private const string Settings = @"X:\data\settings.json";

	[Fact]
	public void Backup_FirstStartOfANewVersion_CopiesTheSettings_AndKeepsTheNewestThree()
	{
		var files = new MemoryFiles();
		files.Write(Settings, """{ "Language": "tr", "LastRunVersion": "0.1.5" }""");
		Assert.Equal("settings.json.0.1.5.bak", SettingsBackup.Run(files, Settings, AppVersion.Parse("0.1.6")!));
		Assert.Equal(files.Data[Settings], files.Data[Settings + ".0.1.5.bak"]);
		foreach (string v in new[] { "0.1.6", "0.1.7", "0.1.8" })
		{
			files.Write(Settings, $$"""{ "LastRunVersion": "{{v}}" }""");
			SettingsBackup.Run(files, Settings, AppVersion.Parse("0.2.0")!);
		}
		Assert.Equal(["settings.json.0.1.6.bak", "settings.json.0.1.7.bak", "settings.json.0.1.8.bak"],
			files.Data.Keys.Where(k => k.EndsWith(".bak", StringComparison.Ordinal)).Select(Path.GetFileName).Order());
	}

	[Fact]
	public void Backup_SameVersion_NoFile_OrAnOlderFile()
	{
		var files = new MemoryFiles();
		Assert.Null(SettingsBackup.Run(files, Settings, Current));
		files.Write(Settings, """{ "LastRunVersion": "0.1.5" }""");
		Assert.Null(SettingsBackup.Run(files, Settings, Current));
		Assert.Single(files.Data);
		// A file from before LastRunVersion existed (or an unreadable one).
		files.Write(Settings, """{ "Language": "en" }""");
		Assert.Equal("settings.json.older.bak", SettingsBackup.Run(files, Settings, Current));
		files.Write(Settings, "{ broken");
		Assert.Equal("settings.json.older.bak", SettingsBackup.Run(files, Settings, Current));
	}

	// --- Toast and confirmation ---

	[Fact]
	public void Toast_OncePerVersion_AndNotWhilePlayingOrWithNotificationsOff()
	{
		AppVersion v = AppVersion.Parse("0.1.6")!;
		Assert.Equal(UpdateToastDecision.Show, UpdatePolicy.Toast(v, null, true, false));
		Assert.Equal(UpdateToastDecision.Show, UpdatePolicy.Toast(v, "0.1.5", true, false));
		Assert.Equal(UpdateToastDecision.AlreadyShown, UpdatePolicy.Toast(v, "0.1.6", true, false));
		Assert.Equal(UpdateToastDecision.AlreadyShown, UpdatePolicy.Toast(v, "0.1.7", false, true));
		Assert.Equal(UpdateToastDecision.NotificationsOff, UpdatePolicy.Toast(v, null, false, false));
		Assert.Equal(UpdateToastDecision.Playing, UpdatePolicy.Toast(v, null, true, true));
		Assert.True(UpdatePolicy.IsPlaying(true, false, false));
		Assert.True(UpdatePolicy.IsPlaying(false, true, false));
		Assert.True(UpdatePolicy.IsPlaying(false, false, true));
		Assert.False(UpdatePolicy.IsPlaying(false, false, false));
		Assert.True(UpdatePolicy.NeedsConfirmation(true));
		Assert.False(UpdatePolicy.NeedsConfirmation(false));
	}

	// --- Fakes ---

	private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
	}

	private sealed class Inline(Action<double> report) : IProgress<double>
	{
		public void Report(double value) => report(value);
	}

	private sealed class RejectAll : IInstallerSignatureCheck
	{
		public string? Check(string path) => "not signed by the expected publisher";
	}

	private sealed class RecordingLauncher(List<string> steps) : IProcessLauncher
	{
		public IReadOnlyList<string>? Arguments { get; private set; }

		public void Start(string path, IReadOnlyList<string> arguments)
		{
			steps.Add("start " + path);
			Arguments = arguments;
		}
	}

	private sealed class FailingLauncher : IProcessLauncher
	{
		public void Start(string path, IReadOnlyList<string> arguments) => throw new System.ComponentModel.Win32Exception(5);
	}

	private sealed class Unseekable(byte[] data) : MemoryStream(data)
	{
		public override bool CanSeek => false;
	}

	// Gives `data`, then fails like a dropped connection (length unknown).
	private sealed class BreakingStream(byte[] data) : MemoryStream(data)
	{
		public override bool CanSeek => false;

		public override int Read(byte[] buffer, int offset, int count)
		{
			int n = base.Read(buffer, offset, count);
			return n > 0 ? n : throw new IOException("connection reset", new System.Net.Sockets.SocketException(10054));
		}

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			int n = await base.ReadAsync(buffer, cancellationToken);
			return n > 0 ? n : throw new IOException("connection reset", new System.Net.Sockets.SocketException(10054));
		}
	}
}

// In-memory files for the updater tests; every write is stamped one second after the previous one.
public sealed class MemoryFiles : IUpdateFiles
{
	public readonly Dictionary<string, byte[]> Data = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, DateTime> _times = new(StringComparer.OrdinalIgnoreCase);
	private DateTime _clock = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

	public void Write(string path, string text) => Stamp(path, Encoding.UTF8.GetBytes(text));

	private void Stamp(string path, byte[] data)
	{
		Data[path] = data;
		_clock = _clock.AddSeconds(1);
		_times[path] = _clock;
	}

	public bool Exists(string path) => Data.ContainsKey(path);
	public void CreateDirectory(string path) { }
	public Stream Create(string path) => new Sink(this, path);
	public Stream OpenRead(string path) => Data.TryGetValue(path, out byte[]? d) ? new MemoryStream(d, false) : throw new FileNotFoundException(path);
	public void Delete(string path) => Data.Remove(path);

	public void Move(string from, string to)
	{
		Stamp(to, Data[from]);
		Data.Remove(from);
	}

	public void Copy(string from, string to) => Stamp(to, (byte[])Data[from].Clone());

	public IEnumerable<string> List(string folder, string pattern)
	{
		string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
		return Data.Keys.Where(k => string.Equals(Path.GetDirectoryName(k), folder, StringComparison.OrdinalIgnoreCase)
			&& System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(k), regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
	}

	public DateTime LastWriteUtc(string path) => _times[path];

	private sealed class Sink(MemoryFiles owner, string path) : MemoryStream
	{
		private bool _closed;

		protected override void Dispose(bool disposing)
		{
			if (disposing && !_closed)
			{
				_closed = true;
				owner.Stamp(path, ToArray());
			}
			base.Dispose(disposing);
		}
	}
}
