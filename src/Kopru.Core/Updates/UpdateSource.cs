// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Updates;

// Where the app learns about a new version and where it may download one from. The manifest (update.json at the repo
// root, see docs/RELEASING.md) is read from the default branch, like the supported games list; installers are accepted
// only from this repository's GitHub release downloads (GitHub redirects those to its asset host, which HttpClient
// follows).
public sealed record UpdateEndpoints(string ManifestUrl, string DownloadPrefix, string PagePrefix, bool AllowLoopbackHttp = false)
{
	public const string Repository = "Midzer00/ds5-untethered";
	public const string Branch = "main";
	public const string ManifestPath = "update.json";
	public const string OfficialManifestUrl = "https://raw.githubusercontent.com/" + Repository + "/" + Branch + "/" + ManifestPath;
	public const string OfficialDownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
	public const string OfficialPagePrefix = "https://github.com/" + Repository + "/";

	// DEBUG builds only (compiled out of Release): rehearse the update flow against a local manifest and installer.
	public const string ManifestOverrideVariable = "KOPRU_UPDATE_MANIFEST_URL";
	public const string PrefixOverrideVariable = "KOPRU_UPDATE_DOWNLOAD_PREFIX";
	public const string InstalledOverrideVariable = "KOPRU_UPDATE_TREAT_AS_INSTALLED";

	public static UpdateEndpoints Official { get; } = new(OfficialManifestUrl, OfficialDownloadPrefix, OfficialPagePrefix);

	// The endpoints the app uses: the official ones, or in a DEBUG build the rehearsal overrides when they are set. An
	// override prefix may be plain http only on this PC (localhost / 127.0.0.1), for a local test server.
	public static UpdateEndpoints Current()
	{
#if DEBUG
		return FromOverrides(Environment.GetEnvironmentVariable(ManifestOverrideVariable), Environment.GetEnvironmentVariable(PrefixOverrideVariable));
#else
		return Official;
#endif
	}

#if DEBUG
	public static UpdateEndpoints FromOverrides(string? manifestUrl, string? downloadPrefix)
	{
		if (string.IsNullOrWhiteSpace(manifestUrl) && string.IsNullOrWhiteSpace(downloadPrefix))
		{
			return Official;
		}
		string prefix = string.IsNullOrWhiteSpace(downloadPrefix) ? OfficialDownloadPrefix : downloadPrefix.Trim();
		bool loopback = Uri.TryCreate(prefix, UriKind.Absolute, out Uri? uri) && uri.IsLoopback;
		return new UpdateEndpoints(string.IsNullOrWhiteSpace(manifestUrl) ? OfficialManifestUrl : manifestUrl.Trim(), prefix, OfficialPagePrefix, loopback);
	}
#endif

	// True when url may be downloaded: https (or loopback http in a rehearsal), no user info, and under DownloadPrefix.
	public bool IsAllowedDownload(string? url) => IsUnder(url, DownloadPrefix, AllowLoopbackHttp);

	// The release page opened in the browser for zip copies: this repository on GitHub only.
	public bool IsAllowedPage(string? url) => IsUnder(url, PagePrefix, false);

	private static bool IsUnder(string? url, string prefix, bool loopbackHttp)
	{
		if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length > 0)
		{
			return false;
		}
		bool scheme = uri.Scheme == Uri.UriSchemeHttps || (loopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
		// Compared on the normalised form, so "/../" tricks cannot leave the prefix.
		return scheme && uri.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal) && uri.AbsoluteUri.Length > prefix.Length
			&& uri.Query.Length == 0 && uri.Fragment.Length == 0;
	}
}
