// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Kopru.Core.Diagnostics;

namespace Kopru.Core.Updates;

public enum DownloadStatus
{
	Verified,
	// The release on GitHub has no such file yet (the manifest went live first): try again later.
	NotYetAvailable,
	Offline,
	Rejected,
	SaveFailed,
	Cancelled
}

// Path: the verified installer (Verified only). Reason: a short English reason for the log.
public sealed record DownloadResult(DownloadStatus Status, string? Path = null, string? Reason = null);

// Checks the installer's Authenticode signature before it is started. The builds are not signed yet, so the app uses
// UnsignedBuilds, which accepts the file (the size and the SHA-256 from the manifest are the check). Once releases are
// code-signed, an implementation that requires the expected signer (WinVerifyTrust + the signer's certificate) goes
// here, and a failure rejects the download like a hash mismatch.
public interface IInstallerSignatureCheck
{
	// Null when the file may run, else why not.
	string? Check(string path);
}

public sealed class UnsignedBuilds : IInstallerSignatureCheck
{
	public static UnsignedBuilds Instance { get; } = new();

	public string? Check(string path) => null;
}

// Downloads a release's installer into the updates folder, enforcing the manifest's size (and the 300 MB cap) while it
// streams and its SHA-256 at the end; a file that fails either is deleted. The partial file is "<name>.part" until it
// is verified, so an interrupted download never looks like an installer.
public sealed class UpdateDownloader(HttpClient http, UpdateEndpoints endpoints, string folder, IUpdateFiles? files = null,
	IInstallerSignatureCheck? signature = null)
{
	private readonly IUpdateFiles _files = files ?? PhysicalUpdateFiles.Instance;
	private readonly IInstallerSignatureCheck _signature = signature ?? UnsignedBuilds.Instance;

	public string Folder { get; } = folder;

	public static string DefaultFolder => System.IO.Path.Combine(AppPaths.DataDirectory, "updates");

	public static UpdateDownloader CreateDefault() =>
		new(CreateHttpClient(), UpdateEndpoints.Current(), DefaultFolder);

	// Same generic User-Agent as the other requests; no overall timeout (a slow line may take minutes), the read is
	// cancelled when nothing arrives for StallTimeout instead.
	public static HttpClient CreateHttpClient() => Games.GameCatalogUpdater.CreateHttpClient();

	public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

	// The local file name of a release's installer ("DS5-Untethered-0.1.6-Setup.exe").
	public static string FileNameFor(AppVersion version) => $"DS5-Untethered-{version}-Setup.exe";

	public async Task<DownloadResult> DownloadAsync(UpdateAsset asset, AppVersion version, IProgress<double>? progress = null, CancellationToken cancel = default)
	{
		if (!endpoints.IsAllowedDownload(asset.Url))
		{
			return Reject(null, "address not allowed");
		}
		if (asset.Size <= 0 || asset.Size > UpdateManifest.MaxAssetBytes)
		{
			return Reject(null, "size not allowed");
		}
		string target = System.IO.Path.Combine(Folder, FileNameFor(version));
		string part = target + ".part";
		AppLog.Write(FormattableString.Invariant($"update: downloading {version} ({asset.Size / (1024.0 * 1024.0):F1} MB)"));
		try
		{
			_files.CreateDirectory(Folder);
			using HttpResponseMessage response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
			if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
			{
				AppLog.Write("update: download not available yet (HTTP 404)");
				return new DownloadResult(DownloadStatus.NotYetAvailable, Reason: "HTTP 404");
			}
			if (!response.IsSuccessStatusCode)
			{
				AppLog.Write($"update: download failed (HTTP {(int)response.StatusCode})");
				return new DownloadResult(DownloadStatus.Offline, Reason: "HTTP " + (int)response.StatusCode);
			}
			if (response.Content.Headers.ContentLength is long announced && announced != asset.Size)
			{
				return Reject(null, FormattableString.Invariant($"size {announced} instead of {asset.Size}"));
			}
			using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			long total = 0;
			await using (Stream source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
			await using (Stream sink = _files.Create(part))
			{
				byte[] buffer = new byte[81920];
				int lastPercent = -1;
				while (true)
				{
					int read;
					using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel))
					{
						stall.CancelAfter(StallTimeout);
						try
						{
							read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
						}
						catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
						{
							throw new IOException("stalled");
						}
					}
					if (read == 0)
					{
						break;
					}
					total += read;
					if (total > asset.Size)
					{
						sink.Dispose();
						return Reject(part, "larger than announced");
					}
					hash.AppendData(buffer, 0, read);
					await sink.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
					int percent = (int)(total * 100 / asset.Size);
					if (percent != lastPercent)
					{
						lastPercent = percent;
						progress?.Report(percent);
					}
				}
			}
			if (total != asset.Size)
			{
				return Reject(part, FormattableString.Invariant($"interrupted at {total} of {asset.Size} bytes"));
			}
			if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				return Reject(part, "SHA-256 mismatch");
			}
			_files.Move(part, target);
		}
		catch (OperationCanceledException) when (cancel.IsCancellationRequested)
		{
			TryDelete(part);
			AppLog.Write("update: download cancelled");
			return new DownloadResult(DownloadStatus.Cancelled, Reason: "cancelled");
		}
		catch (HttpRequestException e)
		{
			TryDelete(part);
			AppLog.Write($"update: download failed ({(e.StatusCode is { } c ? "HTTP " + (int)c : "offline")})");
			return new DownloadResult(DownloadStatus.Offline, Reason: "offline");
		}
		catch (IOException e) when (e.Message == "stalled" || e.InnerException is HttpRequestException or System.Net.Sockets.SocketException)
		{
			TryDelete(part);
			AppLog.Write("update: download failed (connection lost)");
			return new DownloadResult(DownloadStatus.Offline, Reason: "connection lost");
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			TryDelete(part);
			AppLog.Write($"update: download failed (could not save: {e.GetType().Name})");
			return new DownloadResult(DownloadStatus.SaveFailed, Reason: "could not save");
		}
		if (_signature.Check(target) is { } unsigned)
		{
			return Reject(target, "signature: " + unsigned);
		}
		AppLog.Write("update: download verified");
		return new DownloadResult(DownloadStatus.Verified, target);
	}

	// Checks a file on disk against the manifest once more (right before it is started). False: it was deleted.
	public async Task<bool> VerifyAsync(string path, UpdateAsset asset, CancellationToken cancel = default)
	{
		string? problem = null;
		try
		{
			await using Stream stream = _files.OpenRead(path);
			if (stream.Length != asset.Size)
			{
				problem = "size changed";
			}
			else
			{
				byte[] digest = await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false);
				if (!string.Equals(Convert.ToHexString(digest), asset.Sha256, StringComparison.OrdinalIgnoreCase))
				{
					problem = "SHA-256 mismatch";
				}
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			problem = "unreadable";
		}
		problem ??= _signature.Check(path);
		if (problem != null)
		{
			Reject(path, problem);
			return false;
		}
		return true;
	}

	// At start: installers and partial downloads from earlier updates are no longer needed (the log of the last install
	// stays).
	public int CleanUp()
	{
		int removed = 0;
		foreach (string file in _files.List(Folder, "*.exe").Concat(_files.List(Folder, "*.part")).ToList())
		{
			if (TryDelete(file))
			{
				removed++;
			}
		}
		return removed;
	}

	private DownloadResult Reject(string? delete, string reason)
	{
		if (delete != null)
		{
			TryDelete(delete);
		}
		AppLog.Write($"update: download rejected ({reason})");
		return new DownloadResult(DownloadStatus.Rejected, Reason: reason);
	}

	private bool TryDelete(string path)
	{
		try
		{
			if (_files.Exists(path))
			{
				_files.Delete(path);
				return true;
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// Removed at the next start.
		}
		return false;
	}
}
