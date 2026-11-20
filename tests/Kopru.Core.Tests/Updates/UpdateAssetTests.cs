// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Kopru.Core.Updates;

namespace Kopru.Core.Tests;

// Each release carries the installer twice: DS5-Untethered-<v>-Setup.exe (linked from the README and the release notes)
// and the byte-identical DS5-Untethered-<v>-Update.exe, which only update.json names, so GitHub's per-asset download
// counts tell new downloads from in-app updates (docs/RELEASING.md, "Download counts").
[Trait("Category", "Windows")]
public sealed class UpdateAssetTests
{
	private const string Version = "9.9.9";
	private const string SetupName = "DS5-Untethered-" + Version + "-Setup.exe";
	private const string UpdateName = "DS5-Untethered-" + Version + "-Update.exe";
	private const string ZipName = "DS5-Untethered-" + Version + "-win-x64.zip";
	// The newest release before the Update.exe existed: what installed copies run today.
	private static readonly AppVersion Installed = AppVersion.Parse("0.1.8")!;

	private static string RepositoryRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		Assert.NotNull(dir);
		return dir!;
	}

	// A fresh folder under the temp folder (tools/test.ps1 gives every run its own), deleted by the caller.
	private static string TempFolder()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-update-asset-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	// What tools/publish.ps1 does after Inno Setup: a stand-in Setup.exe, its Update.exe copy, a zip, and then
	// tools/release/make-update-json.ps1 with the Update.exe as -Setup. Returns the manifest text and the installer bytes.
	private static (string Json, byte[] Setup) PublishManifestStep(string dir)
	{
		byte[] setup = new byte[256 * 1024 + 123];
		new Random(5).NextBytes(setup);
		File.WriteAllBytes(Path.Combine(dir, SetupName), setup);
		File.Copy(Path.Combine(dir, SetupName), Path.Combine(dir, UpdateName));
		File.WriteAllBytes(Path.Combine(dir, ZipName), Encoding.ASCII.GetBytes("zip stand-in"));
		string script = Path.Combine(RepositoryRoot(), "tools", "release", "make-update-json.ps1");
		string output = Path.Combine(dir, "update.json");
		var start = new ProcessStartInfo("powershell.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		// Started from PowerShell 7 (pwsh, e.g. the shell of a GitHub Actions step), Windows PowerShell would inherit pwsh's
		// PSModulePath, fail to load its own Microsoft.PowerShell.Utility and not know Get-FileHash. Without the variable it
		// builds its default module path, as when tools\publish.ps1 is run from Windows PowerShell.
		start.Environment.Remove("PSModulePath");
		foreach (string a in new[]
		{
			"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
			"-Version", Version, "-Setup", Path.Combine(dir, UpdateName), "-Zip", Path.Combine(dir, ZipName), "-Out", output,
			"-Notes", Path.Combine(dir, "no-notes.json"), "-Published", "2026-12-01"
		})
		{
			start.ArgumentList.Add(a);
		}
		using Process process = Process.Start(start)!;
		Task<string> stdout = process.StandardOutput.ReadToEndAsync();
		Task<string> stderr = process.StandardError.ReadToEndAsync();
		Assert.True(process.WaitForExit(120_000), "make-update-json.ps1 did not finish");
		Assert.True(process.ExitCode == 0, $"make-update-json.ps1 failed ({process.ExitCode}): {stdout.Result} {stderr.Result}");
		return (File.ReadAllText(output), setup);
	}

	[Fact]
	public void PublishManifestStep_PointsAtTheUpdateExe_WithTheSetupsSizeAndHash()
	{
		string dir = TempFolder();
		try
		{
			(string json, byte[] setup) = PublishManifestStep(dir);
			UpdateManifest? m = UpdateManifest.Parse(json, UpdateEndpoints.Official, out string? problem);
			Assert.True(m != null, problem);
			Assert.Equal(Version, m!.Version.ToString());
			Assert.EndsWith("-Update.exe", m.Setup.Url, StringComparison.Ordinal);
			Assert.Equal($"{UpdateEndpoints.OfficialDownloadPrefix}v{Version}/{UpdateName}", m.Setup.Url);
			Assert.Equal(UpdateName, m.Setup.FileName);
			Assert.Equal(setup.LongLength, m.Setup.Size);
			Assert.Equal(Convert.ToHexString(SHA256.HashData(setup)), m.Setup.Sha256);
			Assert.Equal($"{UpdateEndpoints.OfficialDownloadPrefix}v{Version}/{ZipName}", m.Zip!.Url);
			Assert.True(m.Version > Installed);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	// tools/publish.ps1 (which needs a full build and Inno Setup, so it is not run here) makes the copy and hands it to
	// the manifest step.
	[Fact]
	public void PublishScript_CopiesTheSetupToUpdateExe_AndPassesThatToTheManifest()
	{
		string publish = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "publish.ps1"));
		Assert.Contains("$update = Join-Path $artifacts \"DS5-Untethered-$version-Update.exe\"", publish);
		Assert.Contains("Copy-Item -Force $setup $update", publish);
		Assert.Contains("make-update-json.ps1') -Version $version -Setup $update -Zip $zip -Out $manifest", publish);
	}

	// The rehearsal: an installed copy (0.1.8) checks a manifest that announces a newer version whose installer is the
	// renamed Update.exe, served by a loopback server under a /releases/download/ path (the KOPRU_UPDATE_* overrides of
	// DEBUG builds, docs/RELEASING.md), downloads it and verifies size and SHA-256. No installer is started.
	[Fact]
	public async Task Rehearsal_InstalledCopy_UpdatesFromTheRenamedAsset()
	{
		string dir = TempFolder();
		try
		{
			(string json, byte[] setup) = PublishManifestStep(dir);
			using var server = new LoopbackServer();
			string prefix = server.Base + "Midzer00/ds5-untethered/releases/download/";
			string manifestUrl = server.Base + "update.json";
			string local = json.Replace(UpdateEndpoints.OfficialDownloadPrefix, prefix, StringComparison.Ordinal);
			server.Files["/update.json"] = Encoding.UTF8.GetBytes(local);
			server.Files[$"/Midzer00/ds5-untethered/releases/download/v{Version}/{UpdateName}"] = setup;
#if DEBUG
			// Exactly what UpdateEndpoints.Current() makes of KOPRU_UPDATE_MANIFEST_URL / KOPRU_UPDATE_DOWNLOAD_PREFIX (the
			// variables themselves are not set: other tests in this process read them).
			UpdateEndpoints endpoints = UpdateEndpoints.FromOverrides(manifestUrl, prefix);
#else
			var endpoints = new UpdateEndpoints(manifestUrl, prefix, UpdateEndpoints.OfficialPagePrefix, true);
#endif
			Assert.True(endpoints.AllowLoopbackHttp);
			using HttpClient http = UpdateDownloader.CreateHttpClient();

			UpdateCheckResult check = await new UpdateChecker(http, endpoints).CheckAsync(Installed);
			Assert.Equal(UpdateCheckStatus.Available, check.Status);
			UpdateManifest m = check.Manifest!;
			Assert.Equal(prefix + $"v{Version}/{UpdateName}", m.Setup.Url);

			string updates = Path.Combine(dir, "updates");
			var downloader = new UpdateDownloader(http, endpoints, updates);
			var progress = new List<double>();
			DownloadResult download = await downloader.DownloadAsync(m.Setup, m.Version, new Inline(progress.Add));
			Assert.True(download.Status == DownloadStatus.Verified, download.Reason);
			// Saved under the name the app gives it, whatever the asset is called.
			Assert.Equal(Path.Combine(updates, SetupName), download.Path);
			Assert.Equal(setup, File.ReadAllBytes(download.Path!));
			Assert.Equal(new[] { SetupName }, Directory.GetFiles(updates).Select(f => Path.GetFileName(f)).ToArray());
			Assert.Equal(100, progress[^1]);
			Assert.True(await downloader.VerifyAsync(download.Path!, m.Setup));

			Assert.Equal(new[] { "/update.json", $"/Midzer00/ds5-untethered/releases/download/v{Version}/{UpdateName}" }, server.Requests);

			// The same asset with one byte changed is refused and nothing stays behind.
			byte[] tampered = (byte[])setup.Clone();
			tampered[^1] ^= 1;
			server.Files[$"/Midzer00/ds5-untethered/releases/download/v{Version}/{UpdateName}"] = tampered;
			downloader.CleanUp();
			DownloadResult bad = await downloader.DownloadAsync(m.Setup, m.Version);
			Assert.Equal(DownloadStatus.Rejected, bad.Status);
			Assert.Empty(Directory.GetFiles(updates));
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	private sealed class Inline(Action<double> report) : IProgress<double>
	{
		public void Report(double value) => report(value);
	}

	// A minimal HTTP/1.1 server on 127.0.0.1 (a TcpListener, so no URL reservation or administrator rights are needed):
	// GET of a known path returns its bytes, anything else 404; one request per connection.
	private sealed class LoopbackServer : IDisposable
	{
		private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
		private readonly CancellationTokenSource _stop = new();
		private readonly Task _loop;
		private readonly List<string> _requests = [];

		public LoopbackServer()
		{
			_listener.Start();
			Base = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
			_loop = Task.Run(AcceptAsync);
		}

		public string Base { get; }

		public Dictionary<string, byte[]> Files { get; } = [];

		public IReadOnlyList<string> Requests
		{
			get
			{
				lock (_requests)
				{
					return _requests.ToList();
				}
			}
		}

		private async Task AcceptAsync()
		{
			while (!_stop.IsCancellationRequested)
			{
				TcpClient client;
				try
				{
					client = await _listener.AcceptTcpClientAsync(_stop.Token);
				}
				catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
				{
					return;
				}
				_ = Task.Run(() => ServeAsync(client));
			}
		}

		private async Task ServeAsync(TcpClient client)
		{
			using (client)
			{
				NetworkStream stream = client.GetStream();
				var head = new StringBuilder();
				byte[] one = new byte[1];
				while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && head.Length < 16 * 1024)
				{
					if (await stream.ReadAsync(one) == 0)
					{
						return;
					}
					head.Append((char)one[0]);
				}
				string[] line = head.ToString().Split("\r\n")[0].Split(' ');
				string path = line.Length > 1 ? line[1] : string.Empty;
				lock (_requests)
				{
					_requests.Add(path);
				}
				byte[]? body = line[0] == "GET" && Files.TryGetValue(path, out byte[]? data) ? data : null;
				string status = body != null ? "200 OK" : "404 Not Found";
				body ??= [];
				byte[] header = Encoding.ASCII.GetBytes(
					$"HTTP/1.1 {status}\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
				await stream.WriteAsync(header);
				await stream.WriteAsync(body);
				await stream.FlushAsync();
			}
		}

		public void Dispose()
		{
			_stop.Cancel();
			_listener.Stop();
			try
			{
				_loop.Wait(TimeSpan.FromSeconds(5));
			}
			catch (AggregateException)
			{
				// Stopped.
			}
			_stop.Dispose();
		}
	}
}
