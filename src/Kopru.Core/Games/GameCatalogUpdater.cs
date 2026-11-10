// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Kopru.Core.Localization;

namespace Kopru.Core.Games;

// Where the app downloads the supported games list from: the same file as the embedded copy, on the repo's default branch.
public static class GameListSource
{
	public const string Repository = "Midzer00/ds5-untethered";
	public const string Branch = "main";
	public const string FilePath = "src/Kopru.Core/Games/supported-games.json";
	public const string Url = "https://raw.githubusercontent.com/" + Repository + "/" + Branch + "/" + FilePath;
}

public enum CatalogUpdateStatus
{
	UpToDate,
	Updated,
	Failed
}

public enum CatalogUpdateError
{
	None,
	Offline,
	Timeout,
	TooLarge,
	InvalidData,
	UnsupportedVersion,
	SaveFailed
}

// Outcome of one check. Catalog is the list to use from now on (the current one unless Updated). Added and Removed are
// game names (Updated only).
public sealed record CatalogUpdateResult(CatalogUpdateStatus Status, GameCatalog Catalog, IReadOnlyList<string> Added,
	IReadOnlyList<string> Removed, CatalogUpdateError Error = CatalogUpdateError.None)
{
	public int Total => Catalog.Games.Count;

	// Localized reason of a failure, empty otherwise.
	public string Reason => Error switch
	{
		CatalogUpdateError.None => string.Empty,
		CatalogUpdateError.Offline => Text.GameListOffline(),
		CatalogUpdateError.Timeout => Text.GameListTimeout(),
		CatalogUpdateError.TooLarge or CatalogUpdateError.InvalidData => Text.GameListInvalid(),
		CatalogUpdateError.UnsupportedVersion => Text.GameListUnsupported(),
		_ => Text.GameListSaveFailed()
	};

	public static CatalogUpdateResult Fail(GameCatalog current, CatalogUpdateError error) => new(CatalogUpdateStatus.Failed, current, [], [], error);
}

// Downloads supported-games.json from GameListSource, checks it and, when it is newer than the list in use, saves it
// (atomically) to savePath, where GameCatalog.Load picks it up on the next start.
public sealed class GameCatalogUpdater(HttpClient http, string savePath, string? url = null, TimeSpan? timeout = null)
{
	public const int MaxBytes = 1024 * 1024;
	public const int MinGames = 20;
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromDays(7);

	private readonly string url = url ?? GameListSource.Url;
	private readonly TimeSpan timeout = timeout ?? DefaultTimeout;

	public string SavePath { get; } = savePath;

	public static GameCatalogUpdater CreateDefault() => new(CreateHttpClient(), AppPaths.GameList);

	public static HttpClient CreateHttpClient()
	{
		var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("DS5Untethered/1.0");
		return client;
	}

	// The automatic check runs when it is on and the last one was at least a week ago (or never).
	public static bool ShouldAutoCheck(bool enabled, DateTimeOffset? lastCheck, DateTimeOffset now) =>
		enabled && (lastCheck == null || now - lastCheck.Value >= AutoCheckInterval || lastCheck.Value > now);

	// Null when the catalog is usable, else why not.
	public static CatalogUpdateError? Validate(GameCatalog catalog)
	{
		if (catalog.SchemaVersion < 1 || catalog.SchemaVersion > GameCatalog.MaxSchemaVersion)
		{
			return CatalogUpdateError.UnsupportedVersion;
		}
		return catalog.Games.Count < MinGames || catalog.Games.Any(g => string.IsNullOrWhiteSpace(g.Name)) || string.IsNullOrWhiteSpace(catalog.Updated)
			? CatalogUpdateError.InvalidData
			: null;
	}

	public async Task<CatalogUpdateResult> CheckAsync(GameCatalog current, CancellationToken cancel = default)
	{
		string json;
		using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
		{
			limit.CancelAfter(timeout);
			try
			{
				json = await DownloadAsync(limit.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
			{
				return CatalogUpdateResult.Fail(current, CatalogUpdateError.Timeout);
			}
			catch (HttpRequestException)
			{
				return CatalogUpdateResult.Fail(current, CatalogUpdateError.Offline);
			}
			catch (TooLargeException)
			{
				return CatalogUpdateResult.Fail(current, CatalogUpdateError.TooLarge);
			}
			catch (DecoderFallbackException)
			{
				return CatalogUpdateResult.Fail(current, CatalogUpdateError.InvalidData);
			}
		}

		GameCatalog downloaded;
		try
		{
			downloaded = GameCatalog.Parse(json);
		}
		catch (JsonException)
		{
			return CatalogUpdateResult.Fail(current, CatalogUpdateError.InvalidData);
		}
		if (Validate(downloaded) is CatalogUpdateError invalid)
		{
			return CatalogUpdateResult.Fail(current, invalid);
		}
		if (downloaded.CompareFreshness(current) <= 0)
		{
			return new CatalogUpdateResult(CatalogUpdateStatus.UpToDate, current, [], []);
		}
		try
		{
			SaveAtomically(SavePath, json);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return CatalogUpdateResult.Fail(current, CatalogUpdateError.SaveFailed);
		}
		var before = current.Games.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var after = downloaded.Games.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new CatalogUpdateResult(CatalogUpdateStatus.Updated, downloaded,
			downloaded.Games.Select(g => g.Name).Where(n => !before.Contains(n)).ToList(),
			current.Games.Select(g => g.Name).Where(n => !after.Contains(n)).ToList());
	}

	// Writes a temporary file next to path, then moves it over path (a reader never sees half a file).
	public static void SaveAtomically(string path, string content)
	{
		string? folder = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(folder))
		{
			Directory.CreateDirectory(folder);
		}
		string temp = path + ".tmp";
		File.WriteAllText(temp, content, new UTF8Encoding(false));
		File.Move(temp, path, overwrite: true);
	}

	private async Task<string> DownloadAsync(CancellationToken cancel)
	{
		using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException(response.ReasonPhrase, null, response.StatusCode);
		}
		if (response.Content.Headers.ContentLength > MaxBytes)
		{
			throw new TooLargeException();
		}
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
		using var buffer = new MemoryStream();
		byte[] chunk = new byte[16 * 1024];
		int read;
		while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + read > MaxBytes)
			{
				throw new TooLargeException();
			}
			buffer.Write(chunk, 0, read);
		}
		return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF');
	}

	private sealed class TooLargeException : Exception;
}
