// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using Kopru.Core.Games;
using Kopru.Core.Steam;

namespace Kopru.Core.Tests;

// Game pictures: Steam's library cache layouts, the disk cache, the download rules and the CDN client. Only generated
// bytes are used, never real artwork.
public sealed class GameImagesTests : IDisposable
{
	// A tiny "JPEG" (only the magic bytes matter here).
	private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];

	private readonly string folder = Path.Combine(Path.GetTempPath(), "kopru-images-" + Guid.NewGuid().ToString("N"));
	private DateTime utc = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
	private long ms = 1_000_000;

	public void Dispose()
	{
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	private string CacheDir => Path.Combine(folder, "cache");

	private GameImageCache NewCache(long maxBytes = GameImageCache.DefaultMaxBytes) => new(CacheDir, maxBytes: maxBytes, utcNow: () => utc);

	private GameImageResolver Resolver(FakeDownloader downloader, GameImageCache? cache = null, Func<long, string?>? local = null, bool on = true,
		bool online = true, Func<string, long?>? appIdForExe = null) =>
		new(local ?? (_ => null), cache ?? NewCache(), downloader, () => on, () => online, appIdForExe, exeExists: p => p.EndsWith(".exe", StringComparison.Ordinal),
			clockMs: () => ms);

	private string WriteFile(string path, byte[]? bytes = null)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, bytes ?? Jpeg);
		return path;
	}

	// ---- Steam's library cache ----

	private sealed class FolderSteam(string? steamPath) : ISteamEnvironment
	{
		public string? SteamPath => steamPath;
		public bool IsSteamRunning => false;
		public bool FileExists(string path) => File.Exists(path);
		public string ReadAllText(string path) => File.ReadAllText(path);
		public IEnumerable<string> GetDirectories(string path) => Directory.Exists(path) ? Directory.GetDirectories(path) : [];
		public DateTime GetLastWriteTimeUtc(string path) => Directory.GetLastWriteTimeUtc(path);
	}

	private string LibraryCache => Path.Combine(folder, "Steam", "appcache", "librarycache");

	[Fact]
	public void SteamArtwork_FindsEachKnownLayout()
	{
		var artwork = new SteamArtwork(new FolderSteam(Path.Combine(folder, "Steam")));
		string current = WriteFile(Path.Combine(LibraryCache, "10", "header.jpg"));
		string library = WriteFile(Path.Combine(LibraryCache, "20", "library_header.jpg"));
		string old = WriteFile(Path.Combine(LibraryCache, "30", new string('a', 40), "header.jpg"));
		string newer = WriteFile(Path.Combine(LibraryCache, "30", new string('b', 40), "library_header.jpg"));
		Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(old)!, utc.AddDays(-2));
		Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(newer)!, utc);
		string legacy = WriteFile(Path.Combine(LibraryCache, "40_header.jpg"));
		// Other shapes are not header pictures.
		WriteFile(Path.Combine(LibraryCache, "50", "library_600x900.jpg"));
		WriteFile(Path.Combine(LibraryCache, "50", new string('c', 40) + ".jpg"));

		Assert.Equal(current, artwork.Find(10));
		Assert.Equal(library, artwork.Find(20));
		Assert.Equal(newer, artwork.Find(30));
		Assert.Equal(legacy, artwork.Find(40));
		Assert.Null(artwork.Find(50));
		Assert.Null(artwork.Find(60));
		Assert.Null(new SteamArtwork(new FolderSteam(null)).Find(10));
	}

	// ---- Disk cache ----

	[Fact]
	public void Cache_HitMissAndExpiry()
	{
		GameImageCache cache = NewCache();
		Assert.Equal(default, cache.Lookup(7));
		string path = cache.Store(7, Jpeg)!;
		Assert.Equal(new CachedGameImage(path, true, false), cache.Lookup(7));
		Assert.Equal(Jpeg, File.ReadAllBytes(path));
		Assert.False(File.Exists(path + ".tmp"));

		utc = utc.AddDays(31);
		Assert.Equal(new CachedGameImage(path, false, false), cache.Lookup(7));

		cache.MarkMissing(8);
		Assert.True(cache.Lookup(8).KnownMissing);
		utc = utc.AddDays(8);
		Assert.False(cache.Lookup(8).KnownMissing);
	}

	[Fact]
	public void Cache_TrimKeepsTheNewestUnderTheLimit()
	{
		GameImageCache cache = NewCache(maxBytes: 3 * Jpeg.Length);
		for (int id = 1; id <= 5; id++)
		{
			cache.Store(id, Jpeg);
			utc = utc.AddMinutes(1);
		}
		cache.MarkMissing(99);
		utc = utc.AddDays(8);
		(int files, long bytes, int removed) = cache.Trim();
		Assert.Equal(3, files);
		Assert.Equal(3 * Jpeg.Length, bytes);
		Assert.Equal(3, removed); // two oldest pictures and the old "none" mark
		Assert.Null(cache.Lookup(1).Path);
		Assert.Null(cache.Lookup(2).Path);
		Assert.NotNull(cache.Lookup(5).Path);

		cache.Clear();
		Assert.Empty(Directory.GetFiles(CacheDir));
	}

	// ---- Source order and download rules ----

	private sealed class FakeDownloader : IGameImageDownloader
	{
		public readonly List<long> Calls = [];
		public Func<long, Task<GameImageDownload>> Respond = _ => Task.FromResult(new GameImageDownload(GameImageDownloadStatus.Ok, Jpeg));

		public Task<GameImageDownload> DownloadAsync(long appId, CancellationToken cancel)
		{
			lock (Calls)
			{
				Calls.Add(appId);
			}
			return Respond(appId);
		}
	}

	[Fact]
	public async Task SourceOrder_LocalThenCacheThenDownloadThenExeIconThenPlaceholder()
	{
		var downloader = new FakeDownloader();
		GameImageCache cache = NewCache();
		string local = WriteFile(Path.Combine(folder, "local.jpg"));
		GameImageResolver resolver = Resolver(downloader, cache, id => id == 1 ? local : null, appIdForExe: exe => exe.Contains("steamapps") ? 1 : null);

		// Steam's library cache wins, even for a game the user added from a Steam library.
		Assert.Equal(new GameImageFile(GameImageSource.SteamLibrary, local, 1), await resolver.ResolveAsync(new("a", 1)));
		Assert.Equal(GameImageSource.SteamLibrary, (await resolver.ResolveAsync(new("b", null, @"D:\steamapps\common\A\a.exe"))).Source);
		// Then the disk cache, without a download.
		string cached = cache.Store(2, Jpeg)!;
		Assert.Equal(new GameImageFile(GameImageSource.Downloaded, cached, 2), await resolver.ResolveAsync(new("c", 2)));
		Assert.Empty(downloader.Calls);
		// Then a download (saved to the cache).
		GameImageFile fetched = await resolver.ResolveAsync(new("d", 3));
		Assert.Equal(GameImageSource.Downloaded, fetched.Source);
		Assert.Equal([3L], downloader.Calls);
		Assert.Equal(cache.Lookup(3).Path, fetched.Path);
		// An added game with no picture: its exe's icon; nothing at all: the placeholder.
		downloader.Respond = _ => Task.FromResult(GameImageDownload.NotFound);
		Assert.Equal(new GameImageFile(GameImageSource.ExeIcon, @"C:\Games\Indie.exe"), await resolver.ResolveAsync(new("e", 4, @"C:\Games\Indie.exe")));
		Assert.Equal(GameImageFile.Placeholder, await resolver.ResolveAsync(new("f", null)));
		Assert.Equal(GameImageFile.Placeholder, await resolver.ResolveAsync(new("g", null, @"C:\Games\gone.bat")));
	}

	[Fact]
	public async Task SettingOff_UsesLocalSourcesOnly_AndNeverTheNetwork()
	{
		var downloader = new FakeDownloader();
		GameImageCache cache = NewCache();
		cache.Store(2, Jpeg);
		string local = WriteFile(Path.Combine(folder, "local.jpg"));
		GameImageResolver resolver = Resolver(downloader, cache, id => id == 1 ? local : null, on: false);

		Assert.Equal(GameImageSource.SteamLibrary, (await resolver.ResolveAsync(new("a", 1))).Source);
		Assert.Equal(GameImageSource.Placeholder, (await resolver.ResolveAsync(new("b", 2))).Source);
		Assert.Equal(GameImageSource.Placeholder, (await resolver.ResolveAsync(new("c", 3))).Source);
		Assert.Equal(GameImageSource.ExeIcon, (await resolver.ResolveAsync(new("d", 3, @"C:\Games\Indie.exe"))).Source);
		Assert.Empty(downloader.Calls);
	}

	[Fact]
	public async Task Offline_MakesNoRequest()
	{
		var downloader = new FakeDownloader();
		GameImageResolver resolver = Resolver(downloader, online: false);
		Assert.Equal(GameImageSource.Placeholder, (await resolver.ResolveAsync(new("a", 3))).Source);
		Assert.Empty(downloader.Calls);
	}

	[Fact]
	public async Task Expired_IsFetchedAgain_AndStillShownWhenThatFails()
	{
		var downloader = new FakeDownloader();
		GameImageCache cache = NewCache();
		string path = cache.Store(5, Jpeg)!;
		utc = utc.AddDays(31);
		GameImageResolver resolver = Resolver(downloader, cache);

		downloader.Respond = _ => Task.FromResult(GameImageDownload.Fail("timeout"));
		Assert.Equal(new GameImageFile(GameImageSource.Downloaded, path, 5), await resolver.ResolveAsync(new("a", 5)));
		Assert.Equal([5L], downloader.Calls);

		ms += (long)GameImageResolver.LongestPause.TotalMilliseconds;
		downloader.Respond = _ => Task.FromResult(new GameImageDownload(GameImageDownloadStatus.Ok, Jpeg));
		Assert.Equal(GameImageSource.Downloaded, (await resolver.ResolveAsync(new("a", 5))).Source);
		Assert.Equal([5L, 5L], downloader.Calls);
		Assert.True(cache.Lookup(5).Fresh);
	}

	[Fact]
	public async Task NotFound_IsRemembered_AndNotAskedAgain()
	{
		var downloader = new FakeDownloader { Respond = _ => Task.FromResult(GameImageDownload.NotFound) };
		GameImageCache cache = NewCache();
		GameImageResolver resolver = Resolver(downloader, cache);
		Assert.Equal(GameImageSource.Placeholder, (await resolver.ResolveAsync(new("a", 6))).Source);
		Assert.Equal(GameImageSource.Placeholder, (await resolver.ResolveAsync(new("a", 6))).Source);
		Assert.Equal([6L], downloader.Calls);
		Assert.True(cache.Lookup(6).KnownMissing);
		// A new run (new resolver) still remembers it.
		Assert.Equal(GameImageSource.Placeholder, (await Resolver(downloader, cache).ResolveAsync(new("a", 6))).Source);
		Assert.Single(downloader.Calls);
	}

	[Fact]
	public async Task Failure_PausesAllDownloads_WithGrowingPauses_AndCapsAttempts()
	{
		var downloader = new FakeDownloader { Respond = _ => Task.FromResult(GameImageDownload.Fail("HTTP 503")) };
		GameImageResolver resolver = Resolver(downloader);

		await resolver.ResolveAsync(new("a", 1));
		Assert.True(resolver.IsPaused);
		// Paused: no request for any game, however often rows ask.
		for (int i = 0; i < 20; i++)
		{
			await resolver.ResolveAsync(new("b", 2));
		}
		Assert.Equal([1L], downloader.Calls);

		// After the first pause one more try; it fails again: the next pause is twice as long.
		ms += (long)GameImageResolver.FirstPause.TotalMilliseconds;
		await resolver.ResolveAsync(new("b", 2));
		Assert.Equal([1L, 2L], downloader.Calls);
		ms += (long)GameImageResolver.FirstPause.TotalMilliseconds;
		Assert.True(resolver.IsPaused);
		await resolver.ResolveAsync(new("c", 3));
		Assert.Equal(2, downloader.Calls.Count);

		// An app id is tried at most MaxAttempts times per run.
		downloader.Respond = _ => Task.FromResult(GameImageDownload.Fail("timeout"));
		for (int i = 0; i < 5; i++)
		{
			ms += (long)GameImageResolver.LongestPause.TotalMilliseconds;
			await resolver.ResolveAsync(new("a", 1));
		}
		Assert.Equal(GameImageResolver.MaxAttempts, downloader.Calls.Count(id => id == 1));
	}

	[Fact]
	public async Task RowsAskingForTheSameGame_ShareOneDownload()
	{
		var gate = new TaskCompletionSource<GameImageDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
		var downloader = new FakeDownloader { Respond = _ => gate.Task };
		GameImageResolver resolver = Resolver(downloader);
		Task<GameImageFile> first = resolver.ResolveAsync(new("a", 9));
		Task<GameImageFile> second = resolver.ResolveAsync(new("a", 9));
		await WaitUntil(() => downloader.Calls.Count == 1);
		gate.SetResult(new GameImageDownload(GameImageDownloadStatus.Ok, Jpeg));
		Assert.Equal(GameImageSource.Downloaded, (await first).Source);
		Assert.Equal(GameImageSource.Downloaded, (await second).Source);
		Assert.Single(downloader.Calls);
	}

	[Fact]
	public async Task Cancelled_BeforeItsTurn_IsDropped_WithoutCountingAsFailure()
	{
		var gate = new TaskCompletionSource<GameImageDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
		var downloader = new FakeDownloader { Respond = _ => gate.Task };
		GameImageResolver resolver = Resolver(downloader);
		// MaxParallel downloads hold every slot.
		Task<GameImageFile>[] busy = Enumerable.Range(1, GameImageResolver.MaxParallel).Select(i => resolver.ResolveAsync(new("busy", i))).ToArray();
		await WaitUntil(() => downloader.Calls.Count == GameImageResolver.MaxParallel);

		// A row that scrolls away while its download waits for a slot: cancelled, and never sent.
		using var scrolledAway = new CancellationTokenSource();
		Task<GameImageFile> waiting = resolver.ResolveAsync(new("gone", 100), scrolledAway.Token);
		scrolledAway.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
		gate.SetResult(new GameImageDownload(GameImageDownloadStatus.Ok, Jpeg));
		await Task.WhenAll(busy);
		await Task.Delay(50);
		Assert.DoesNotContain(100L, downloader.Calls);
		Assert.False(resolver.IsPaused);

		// Asked again later, it is fetched normally.
		Assert.Equal(GameImageSource.Downloaded, (await resolver.ResolveAsync(new("gone", 100))).Source);
		Assert.Contains(100L, downloader.Calls);
	}

	private static async Task WaitUntil(Func<bool> condition)
	{
		for (int i = 0; i < 200 && !condition(); i++)
		{
			await Task.Delay(10);
		}
		Assert.True(condition());
	}

	// ---- Steam CDN client ----

	private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		public readonly List<HttpRequestMessage> Requests = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(respond(request));
		}
	}

	private static HttpResponseMessage Image() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) };

	private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	[Fact]
	public void Urls_UseSteamsPublicCdn_WithTheAppIdOnly()
	{
		Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1091500/header.jpg", SteamHeaderDownloader.HeaderUrl(1091500));
		Assert.Equal("https://store.steampowered.com/api/appdetails?appids=1091500&filters=basic", SteamHeaderDownloader.DetailsUrl(1091500));
		Assert.True(SteamHeaderDownloader.IsSteamCdn("https://shared.akamai.steamstatic.com/a/header.jpg"));
		Assert.False(SteamHeaderDownloader.IsSteamCdn("http://shared.akamai.steamstatic.com/a/header.jpg"));
		Assert.False(SteamHeaderDownloader.IsSteamCdn("https://steamstatic.com.example.net/a.jpg"));
		Assert.False(SteamHeaderDownloader.IsSteamCdn("https://example.net/a.jpg"));
		Assert.True(SteamHeaderDownloader.LooksLikeImage(Jpeg));
		Assert.False(SteamHeaderDownloader.LooksLikeImage("<html>not found</html>"u8));
	}

	[Fact]
	public async Task Downloader_PlainHeader()
	{
		var handler = new FakeHandler(_ => Image());
		GameImageDownload result = await new SteamHeaderDownloader(new HttpClient(handler)).DownloadAsync(42, default);
		Assert.Equal(GameImageDownloadStatus.Ok, result.Status);
		Assert.Equal(Jpeg, result.Bytes);
		Assert.Equal(SteamHeaderDownloader.HeaderUrl(42), Assert.Single(handler.Requests).RequestUri!.ToString());
		Assert.Null(handler.Requests[0].Headers.Authorization);
	}

	[Fact]
	public async Task Downloader_NewerApps_FindTheHashedHeaderThroughAppDetails()
	{
		const string hashed = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/43/0123abcd/header_alt.jpg?t=1";
		var handler = new FakeHandler(r => r.RequestUri!.ToString() switch
		{
			string u when u == SteamHeaderDownloader.HeaderUrl(43) => new HttpResponseMessage(HttpStatusCode.NotFound),
			string u when u == SteamHeaderDownloader.DetailsUrl(43) => Json("""{"43":{"success":true,"data":{"header_image":""" + "\"" + hashed.Replace("/", "\\/") + "\"}}}"),
			hashed => Image(),
			_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
		});
		GameImageDownload result = await new SteamHeaderDownloader(new HttpClient(handler)).DownloadAsync(43, default);
		Assert.Equal(GameImageDownloadStatus.Ok, result.Status);
		Assert.Equal(3, handler.Requests.Count);
	}

	[Theory]
	[InlineData("""{"44":{"success":false}}""")]
	[InlineData("""{"44":{"success":true,"data":{"header_image":"https://evil.example/x.jpg"}}}""")]
	[InlineData("""{"44":{"success":true,"data":{}}}""")]
	public async Task Downloader_NoUsableHeader_IsNotFound(string details)
	{
		var handler = new FakeHandler(r => r.RequestUri!.Host == "store.steampowered.com" ? Json(details) : new HttpResponseMessage(HttpStatusCode.NotFound));
		Assert.Equal(GameImageDownloadStatus.NotFound, (await new SteamHeaderDownloader(new HttpClient(handler)).DownloadAsync(44, default)).Status);
		Assert.Equal(2, handler.Requests.Count);
	}

	[Theory]
	[InlineData(HttpStatusCode.InternalServerError)]
	[InlineData(HttpStatusCode.TooManyRequests)]
	public async Task Downloader_ServerTrouble_IsAFailure(HttpStatusCode code)
	{
		var handler = new FakeHandler(_ => new HttpResponseMessage(code));
		GameImageDownload result = await new SteamHeaderDownloader(new HttpClient(handler)).DownloadAsync(45, default);
		Assert.Equal(GameImageDownloadStatus.Failed, result.Status);
		Assert.Single(handler.Requests);
	}

	[Fact]
	public async Task Downloader_NotAnImage_IsAFailure()
	{
		var handler = new FakeHandler(_ => Json("<html></html>"));
		Assert.Equal(GameImageDownloadStatus.Failed, (await new SteamHeaderDownloader(new HttpClient(handler)).DownloadAsync(46, default)).Status);
	}

	[Fact]
	public async Task Downloader_Timeout_IsAFailure()
	{
		var handler = new SlowHandler();
		GameImageDownload result = await new SteamHeaderDownloader(new HttpClient(handler), TimeSpan.FromMilliseconds(50)).DownloadAsync(47, default);
		Assert.Equal(new GameImageDownload(GameImageDownloadStatus.Failed, null, "timeout"), result);
	}

	private sealed class SlowHandler : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			await Task.Delay(Timeout.Infinite, cancellationToken);
			return new HttpResponseMessage(HttpStatusCode.OK);
		}
	}

	// ---- Exe icon ----

	[Fact]
	public void ExeIcon_ReadsAnExesIcon_AndNothingForAMissingFile()
	{
		IconPixels? icon = ExeIcon.Read(Path.Combine(Environment.SystemDirectory, "cmd.exe"), 48);
		Assert.NotNull(icon);
		Assert.InRange(icon!.Width, 16, 256);
		Assert.Equal(icon.Width * icon.Height * 4, icon.Bgra.Length);
		Assert.Contains(icon.Bgra.Where((_, i) => i % 4 == 3), a => a != 0);
		Assert.Null(ExeIcon.Read(Path.Combine(folder, "missing.exe"), 48));
	}
}
