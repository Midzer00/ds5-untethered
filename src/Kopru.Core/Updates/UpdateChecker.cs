// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Kopru.Core.Diagnostics;

namespace Kopru.Core.Updates;

public enum UpdateCheckStatus
{
	UpToDate,
	Available,
	Failed
}

public enum UpdateCheckError
{
	None,
	Offline,
	Timeout,
	NotFound,
	Invalid
}

// Outcome of one check. Manifest: the newer release (Available only).
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateManifest? Manifest = null, UpdateCheckError Error = UpdateCheckError.None)
{
	public static UpdateCheckResult Fail(UpdateCheckError error) => new(UpdateCheckStatus.Failed, null, error);
}

// When the automatic check runs: a short while after the app starts (not during startup), then whenever a week has
// passed since the last successful one; after a failed one, again after RetryAfterFailure at the earliest.
public static class UpdateSchedule
{
	public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);
	public static readonly TimeSpan Interval = TimeSpan.FromDays(7);
	public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);
	// How often a running app looks whether a check is due.
	public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

	public static bool IsDue(bool enabled, DateTimeOffset? lastCheck, DateTimeOffset? lastFailure, DateTimeOffset now)
	{
		if (!enabled)
		{
			return false;
		}
		if (lastFailure is { } failed && failed <= now && now - failed < RetryAfterFailure)
		{
			return false;
		}
		// A clock set back past the last check counts as due.
		return lastCheck == null || lastCheck.Value > now || now - lastCheck.Value >= Interval;
	}
}

// Reads the manifest and compares it with the running version. Sends nothing but the GET (no query string, no ids;
// the same generic User-Agent as the game list request).
public sealed class UpdateChecker(HttpClient http, UpdateEndpoints endpoints, TimeSpan? timeout = null)
{
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

	private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

	public UpdateEndpoints Endpoints { get; } = endpoints;

	public static UpdateChecker CreateDefault() => new(Games.GameCatalogUpdater.CreateHttpClient(), UpdateEndpoints.Current());

	public async Task<UpdateCheckResult> CheckAsync(AppVersion current, CancellationToken cancel = default)
	{
		string json;
		using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
		{
			limit.CancelAfter(_timeout);
			try
			{
				json = await ReadAsync(limit.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
			{
				return Failed(UpdateCheckError.Timeout, "timeout");
			}
			catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
			{
				return Failed(UpdateCheckError.NotFound, "manifest not found");
			}
			catch (HttpRequestException e)
			{
				return Failed(UpdateCheckError.Offline, e.StatusCode is { } code ? "HTTP " + (int)code : "offline");
			}
			catch (Exception e) when (e is InvalidDataException or DecoderFallbackException)
			{
				return Failed(UpdateCheckError.Invalid, "manifest too large or not text");
			}
		}
		UpdateManifest? manifest = UpdateManifest.Parse(json, Endpoints, out string? problem);
		if (manifest == null)
		{
			return Failed(UpdateCheckError.Invalid, "invalid manifest: " + problem);
		}
		if (manifest.Version <= current)
		{
			AppLog.Write($"update: checked, up to date ({current})");
			return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
		}
		AppLog.Write($"update: {manifest.Version} available");
		return new UpdateCheckResult(UpdateCheckStatus.Available, manifest);
	}

	private static UpdateCheckResult Failed(UpdateCheckError error, string reason)
	{
		AppLog.Write($"update: check failed ({reason})");
		return UpdateCheckResult.Fail(error);
	}

	private async Task<string> ReadAsync(CancellationToken cancel)
	{
		using HttpResponseMessage response = await http.GetAsync(Endpoints.ManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException(response.ReasonPhrase, null, response.StatusCode);
		}
		if (response.Content.Headers.ContentLength > UpdateManifest.MaxManifestBytes)
		{
			throw new InvalidDataException();
		}
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
		using var buffer = new MemoryStream();
		byte[] chunk = new byte[8 * 1024];
		int read;
		while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + read > UpdateManifest.MaxManifestBytes)
			{
				throw new InvalidDataException();
			}
			buffer.Write(chunk, 0, read);
		}
		return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿');
	}
}
