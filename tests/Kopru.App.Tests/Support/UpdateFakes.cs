// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kopru.App.ViewModels;
using Kopru.Core.Setup;
using Kopru.Core.Updates;

namespace Kopru.App.Tests;

// In-app update services with nothing real behind them: the manifest and the installer come from a fake HTTP handler,
// files live in memory, the browser and the installer hand-over are recorded.
internal sealed class UpdateFakes
{
	public const string Version = "0.1.6";
	public static readonly byte[] Installer = Enumerable.Range(0, 4000).Select(i => (byte)(i * 13)).ToArray();

	public HttpStatusCode ManifestStatus { get; set; } = HttpStatusCode.OK;
	public string ManifestVersion { get; set; } = Version;
	public string NotesTr { get; set; } = "Kumanda hoparlörü artık daha temiz; oyun listesi güncellendi.";
	public string NotesEn { get; set; } = "Cleaner controller speaker sound; the game list was updated.";
	// The installer download: the file by default; set to answer otherwise (404, a stalled stream...).
	public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Download { get; set; }
	public bool Installed { get; set; } = true;
	public InstallOutcome Outcome { get; set; } = InstallOutcome.Started;
	public int ManifestRequests { get; private set; }
	public List<string> Opened { get; } = [];
	public List<(string Path, UpdateAsset Asset, string Language)> Installs { get; } = [];
	public MemoryUpdateFiles Files { get; } = new();

	public string ManifestJson() =>
		$$"""
		{
		  "version": "{{ManifestVersion}}",
		  "published": "2026-11-20",
		  "setup": { "url": "{{UpdateEndpoints.OfficialDownloadPrefix}}v{{ManifestVersion}}/DS5-Untethered-{{ManifestVersion}}-Setup.exe",
		             "sha256": "{{Convert.ToHexString(SHA256.HashData(Installer))}}", "size": {{Installer.Length}} },
		  "page": "https://github.com/Midzer00/ds5-untethered/releases/tag/v{{ManifestVersion}}",
		  "notes": { "tr": "{{NotesTr}}", "en": "{{NotesEn}}" }
		}
		""";

	public UpdateServices Services()
	{
		var http = new HttpClient(new Handler(async (request, cancel) =>
		{
			if (request.RequestUri!.AbsoluteUri == UpdateEndpoints.OfficialManifestUrl)
			{
				ManifestRequests++;
				return ManifestStatus == HttpStatusCode.OK
					? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ManifestJson(), Encoding.UTF8) }
					: new HttpResponseMessage(ManifestStatus);
			}
			return Download != null ? await Download(request, cancel) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) };
		}));
		var services = new UpdateServices(new UpdateChecker(http, UpdateEndpoints.Official),
			new UpdateDownloader(http, UpdateEndpoints.Official, @"X:\data\updates", Files), new Kind(this), new Browser(this), "0.1.5");
		services.Install = (path, asset, language) =>
		{
			Installs.Add((path, asset, language));
			return Task.FromResult(Outcome);
		};
		return services;
	}

	// A download that stops at `percent` until the test ends it.
	public TaskCompletionSource Stall(int percent)
	{
		var release = new TaskCompletionSource();
		Download = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StreamContent(new StallingStream(Installer[..(Installer.Length * percent / 100)], release.Task))
		});
		return release;
	}

	private sealed class Kind(UpdateFakes owner) : IInstallKind
	{
		public bool IsInstalled => owner.Installed;
	}

	private sealed class Browser(UpdateFakes owner) : IUrlLauncher
	{
		public void Open(string target) => owner.Opened.Add(target);
	}

	private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
	}

	// Gives its bytes, then waits for `release` and fails like a dropped connection.
	private sealed class StallingStream(byte[] data, Task release) : MemoryStream(data)
	{
		public override bool CanSeek => false;

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			int n = await base.ReadAsync(buffer, cancellationToken);
			if (n > 0)
			{
				return n;
			}
			await release;
			throw new IOException("connection reset", new System.Net.Sockets.SocketException(10054));
		}
	}
}

internal sealed class MemoryUpdateFiles : IUpdateFiles
{
	public Dictionary<string, byte[]> Data { get; } = new(StringComparer.OrdinalIgnoreCase);

	public bool Exists(string path) => Data.ContainsKey(path);
	public void CreateDirectory(string path) { }
	public Stream Create(string path) => new Sink(this, path);
	public Stream OpenRead(string path) => new MemoryStream(Data[path], false);
	public void Delete(string path) => Data.Remove(path);

	public void Move(string from, string to)
	{
		Data[to] = Data[from];
		Data.Remove(from);
	}

	public void Copy(string from, string to) => Data[to] = (byte[])Data[from].Clone();
	public IEnumerable<string> List(string folder, string pattern) => [];
	public DateTime LastWriteUtc(string path) => DateTime.UnixEpoch;

	private sealed class Sink(MemoryUpdateFiles owner, string path) : MemoryStream
	{
		private bool _closed;

		protected override void Dispose(bool disposing)
		{
			if (disposing && !_closed)
			{
				_closed = true;
				owner.Data[path] = ToArray();
			}
			base.Dispose(disposing);
		}
	}
}
