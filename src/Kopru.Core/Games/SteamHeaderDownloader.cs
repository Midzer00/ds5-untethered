// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Kopru.Core.Games;

public enum GameImageDownloadStatus
{
	Ok,
	// Steam has no header picture for this app (remembered, not asked again for a while).
	NotFound,
	// Offline, timeout, server error, rate limit or bad data: try again later.
	Failed
}

public sealed record GameImageDownload(GameImageDownloadStatus Status, byte[]? Bytes = null, string? Reason = null)
{
	public static GameImageDownload NotFound { get; } = new(GameImageDownloadStatus.NotFound);

	public static GameImageDownload Fail(string reason) => new(GameImageDownloadStatus.Failed, null, reason);
}

public interface IGameImageDownloader
{
	Task<GameImageDownload> DownloadAsync(long appId, CancellationToken cancel);
}

// Fetches a game's Steam store header picture (460x215 JPEG) from Steam's public CDN. Only the app id is in the request;
// nothing about the user is sent. Apps whose store assets were uploaded since late 2024 keep the header under a hashed
// folder, so a 404 on the plain URL asks the store's public app details once for the header's address.
public sealed class SteamHeaderDownloader(HttpClient http, TimeSpan? timeout = null) : IGameImageDownloader
{
	public const string HeaderUrlFormat = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/header.jpg";
	public const string DetailsUrlFormat = "https://store.steampowered.com/api/appdetails?appids={0}&filters=basic";
	public const int MaxImageBytes = 1024 * 1024;
	public const int MaxDetailsBytes = 512 * 1024;
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

	private readonly TimeSpan timeout = timeout ?? DefaultTimeout;

	public static SteamHeaderDownloader CreateDefault() => new(GameCatalogUpdater.CreateHttpClient());

	public static string HeaderUrl(long appId) => string.Format(CultureInfo.InvariantCulture, HeaderUrlFormat, appId);

	public static string DetailsUrl(long appId) => string.Format(CultureInfo.InvariantCulture, DetailsUrlFormat, appId);

	public async Task<GameImageDownload> DownloadAsync(long appId, CancellationToken cancel)
	{
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		limit.CancelAfter(timeout);
		try
		{
			GameImageDownload first = await GetImageAsync(HeaderUrl(appId), limit.Token).ConfigureAwait(false);
			if (first.Status != GameImageDownloadStatus.NotFound)
			{
				return first;
			}
			string? url = await FindHeaderUrlAsync(appId, limit.Token).ConfigureAwait(false);
			return url == null ? GameImageDownload.NotFound : await GetImageAsync(url, limit.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
		{
			return GameImageDownload.Fail("timeout");
		}
		catch (HttpRequestException e)
		{
			return GameImageDownload.Fail(e.StatusCode is HttpStatusCode code ? "HTTP " + (int)code : "offline");
		}
		catch (Exception e) when (e is JsonException or IOException or InvalidDataException)
		{
			return GameImageDownload.Fail("bad data");
		}
	}

	// A JPEG or PNG under MaxImageBytes.
	public static bool LooksLikeImage(ReadOnlySpan<byte> bytes) =>
		bytes.Length > 8 && bytes.Length <= MaxImageBytes
		&& ((bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) || (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47));

	// Only https pictures on Steam's CDN are followed.
	public static bool IsSteamCdn(string? url) =>
		Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
		&& (uri.Host.Equals("steamstatic.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase));

	// The "header_image" of the app details answer: { "<appid>": { "success": true, "data": { "header_image": "https://..." } } }.
	public static string? ParseHeaderUrl(string json, long appId)
	{
		using JsonDocument doc = JsonDocument.Parse(json);
		if (doc.RootElement.ValueKind == JsonValueKind.Object
			&& doc.RootElement.TryGetProperty(appId.ToString(CultureInfo.InvariantCulture), out JsonElement app)
			&& app.ValueKind == JsonValueKind.Object
			&& app.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
			&& app.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
			&& data.TryGetProperty("header_image", out JsonElement header) && header.ValueKind == JsonValueKind.String)
		{
			string? url = header.GetString();
			return IsSteamCdn(url) ? url : null;
		}
		return null;
	}

	private async Task<GameImageDownload> GetImageAsync(string url, CancellationToken cancel)
	{
		using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
		if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
		{
			return GameImageDownload.NotFound;
		}
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException(response.ReasonPhrase, null, response.StatusCode);
		}
		byte[] bytes = await ReadLimitedAsync(response, MaxImageBytes, cancel).ConfigureAwait(false);
		return LooksLikeImage(bytes) ? new GameImageDownload(GameImageDownloadStatus.Ok, bytes) : GameImageDownload.Fail("bad data");
	}

	private async Task<string?> FindHeaderUrlAsync(long appId, CancellationToken cancel)
	{
		using HttpResponseMessage response = await http.GetAsync(DetailsUrl(appId), HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException(response.ReasonPhrase, null, response.StatusCode);
		}
		byte[] bytes = await ReadLimitedAsync(response, MaxDetailsBytes, cancel).ConfigureAwait(false);
		return ParseHeaderUrl(System.Text.Encoding.UTF8.GetString(bytes), appId);
	}

	private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int max, CancellationToken cancel)
	{
		if (response.Content.Headers.ContentLength > max)
		{
			throw new InvalidDataException();
		}
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
		using var buffer = new MemoryStream();
		byte[] chunk = new byte[16 * 1024];
		int read;
		while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + read > max)
			{
				throw new InvalidDataException();
			}
			buffer.Write(chunk, 0, read);
		}
		return buffer.ToArray();
	}
}
