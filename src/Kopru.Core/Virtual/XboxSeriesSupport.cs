// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kopru.Core.Virtual;

// When the Xbox Series pad can be used. Windows binds its in-box Xbox driver to a wired GIP pad without the security
// exchange only from Windows 10 21H2 (build 19044) on ([MS-GIPUSB] appendix B: "Removed device authentication requirement
// for USB endpoints in Windows 10 v21H2, Windows 11, and later"). On older Windows, or when the driver does not start the
// pad within StartTimeout (no Set Device State START), the app falls back to Xbox 360.
public static class XboxSeriesSupport
{
	// Windows 10 21H2.
	public const int MinimumBuild = 19044;

	// How long Windows gets to start a plugged-in Xbox Series pad. A known pad answers in a second or two; an OFF before
	// START takes the pad's re-announces (GipDevice.ReannounceDelayMs, up to MaxReannounces times) on top.
	public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

	// The same for a device id Windows never started on this PC: Windows installs the device first (and for the first
	// Xbox pad ever, sets up its driver), which can take much longer.
	public static readonly TimeSpan FirstStartTimeout = TimeSpan.FromSeconds(45);

	public static bool OsSupports(Version version) => version.Major > 10 || version.Major == 10 && version.Build >= MinimumBuild;

	// This PC's Windows is new enough.
	public static bool OsSupported { get; } = OperatingSystem.IsWindows() && OsSupports(Environment.OSVersion.Version);

	// "Windows 10.0.19045" (for the log).
	public static string OsText => string.Create(CultureInfo.InvariantCulture,
		$"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{Environment.OSVersion.Version.Build}");
}

// The GIP device id of a controller's Xbox Series pad ([MS-GIPUSB] 2.2.1.3: the top four bytes 00 00 FF FB, then four
// more). The USB serial carries it too, so Windows keeps it in the registry where any program can read it. A random id per
// session made Windows add a device entry for every session, so the id stays the same for a controller on this install: 32
// bits of HMAC-SHA-256 keyed with the install's random salt (XboxSeriesStore) over the controller's identity (its Bluetooth
// address, else its HID path). Without the salt the id can't be traced back to the address, and another install (another
// PC, a fresh install) gives the same controller other ids. The id is never logged (the controller's Tag is). Each
// controller has two ids (variant 0 and 1): the pad uses variant 0, and the other one only when the previous pad of the
// same controller went away without the host picking up its "powering off" status shortly before, or the app ended without
// unplugging it (a quick re-attach must not look like the return of an instance the host may still hold; see Variant). So
// a controller leaves at most two device entries.
public static class GipDeviceIds
{
	public const ulong Prefix = 0x0000FFFB_00000000UL;

	// A re-attach this soon after an unclean end uses the other variant.
	public static readonly TimeSpan QuickReattach = TimeSpan.FromSeconds(60);

	// The seed for a controller: its Bluetooth address when known, else its HID interface path. Kept in memory only, never
	// written or logged.
	public static string Seed(ulong? address, string path) => address is ulong a and > 0
		? "bt:" + a.ToString("X12", CultureInfo.InvariantCulture)
		: "path:" + (path ?? string.Empty).Trim().ToLowerInvariant();

	// The id for a seed and variant (0 or 1) on the install with this salt: 00 00 FF FB and 32 bits of HMAC-SHA-256; never 0
	// in the low half.
	public static ulong For(byte[] salt, string seed, int variant = 0)
	{
		byte[] mac = HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes($"DS5 Untethered GIP device id\0{seed}\0{(variant == 0 ? 0 : 1)}"));
		uint low = BitConverter.ToUInt32(mac, 0);
		return Prefix | (low == 0 ? 1u : low);
	}

	// The controller's tag on the install with this salt (12 hex digits): the key of its saved state, and what the log
	// shows. Not derivable from the device id or the other way round; without the salt it says nothing about the address.
	public static string Tag(byte[] salt, string seed) =>
		Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes($"DS5 Untethered controller tag\0{seed}")), 0, 6).ToLowerInvariant();

	// The variant for the next pad of a controller: the other one only after an unclean end within QuickReattach.
	// previous: the variant the controller's last pad used (null: none).
	public static int Variant(int? previous, bool previousEndedCleanly, TimeSpan sincePreviousEnd) =>
		previous is int v && !previousEndedCleanly && sincePreviousEnd < QuickReattach ? 1 - Math.Clamp(v, 0, 1) : 0;

	// The variant for the next pad of a controller from its saved state: a pad that never ended (the app crashed or was
	// killed while it was plugged in) counts as an unclean end just now.
	public static int Variant(XboxSeriesPadRecord? last, DateTimeOffset now) => last is null
		? 0
		: Variant(last.Variant, last.Clean, last.EndedAt is { } ended ? now - ended : TimeSpan.Zero);
}

// The saved state of a controller's Xbox Series pads: the device id variant its last pad used, whether that pad ended
// cleanly (false while it is plugged in, so a crash or kill leaves it false) and when (null: it never ended), and the
// variants Windows started on this PC before (bit 0: variant 0, bit 1: variant 1).
public sealed record XboxSeriesPadRecord(int Variant, bool Clean, DateTimeOffset? EndedAt, int Started = 0)
{
	public bool HasStarted(int variant) => (Started & (1 << Math.Clamp(variant, 0, 1))) != 0;
}

// What this install keeps for the Xbox Series pads, in %LOCALAPPDATA%\Untethered\xbox-series.json (not in settings.json, so
// a diagnostics export or a settings backup never carries it): a random salt made once (see GipDeviceIds), and per
// controller tag its XboxSeriesPadRecord. Thread-safe; never throws: a file that can't be read starts over with a new salt,
// one that can't be written keeps the state for this run.
public sealed class XboxSeriesStore
{
	public const string FileName = "xbox-series.json";

	public const int SaltSize = 32;

	private static readonly Lazy<XboxSeriesStore> shared = new(() => new XboxSeriesStore(Path.Combine(AppPaths.DataDirectory, FileName)));

	private readonly object gate = new();

	private readonly string? path;

	private readonly byte[] salt;

	private readonly Dictionary<string, XboxSeriesPadRecord> pads = new(StringComparer.Ordinal);

	// path: the file (null: memory only, as in tests). salt: a fixed salt (tests); otherwise the saved one, or a new random
	// one, saved at once.
	public XboxSeriesStore(string? path = null, byte[]? salt = null)
	{
		this.path = path;
		Saved? saved = Read(path);
		byte[]? savedSalt = null;
		try
		{
			savedSalt = saved?.Salt is { Length: > 0 } text ? Convert.FromBase64String(text) : null;
		}
		catch (FormatException)
		{
			saved = null;
		}
		bool fresh = salt == null && savedSalt is not { Length: SaltSize };
		this.salt = salt ?? (fresh ? RandomNumberGenerator.GetBytes(SaltSize) : savedSalt!);
		if (!fresh && saved?.Pads != null)
		{
			foreach ((string tag, XboxSeriesPadRecord record) in saved.Pads)
			{
				if (tag.Length > 0 && record != null)
				{
					pads[tag] = record with { Variant = Math.Clamp(record.Variant, 0, 1) };
				}
			}
		}
		if (fresh && path != null)
		{
			Save();
		}
	}

	// The app's store (one for all controller slots).
	public static XboxSeriesStore Shared => shared.Value;

	// The device id of a controller's pad (see GipDeviceIds).
	public ulong DeviceId(string seed, int variant) => GipDeviceIds.For(salt, seed, variant);

	public string Tag(string seed) => GipDeviceIds.Tag(salt, seed);

	public XboxSeriesPadRecord? Last(string tag)
	{
		lock (gate)
		{
			return pads.TryGetValue(tag, out XboxSeriesPadRecord? record) ? record : null;
		}
	}

	// A pad with this variant is being plugged in: it counts as not ended cleanly until Ended says otherwise.
	public void Attaching(string tag, int variant) => Change(tag, r => new XboxSeriesPadRecord(variant, false, null, r?.Started ?? 0));

	// The pad was unplugged (clean: the host picked up its powering-off status).
	public void Ended(string tag, int variant, bool clean, DateTimeOffset at) =>
		Change(tag, r => new XboxSeriesPadRecord(variant, clean, at, r?.Started ?? 0));

	// Windows started the pad with this variant.
	public void Started(string tag, int variant) =>
		Change(tag, r => (r ?? new XboxSeriesPadRecord(variant, false, null)) with { Started = (r?.Started ?? 0) | 1 << Math.Clamp(variant, 0, 1) });

	private void Change(string tag, Func<XboxSeriesPadRecord?, XboxSeriesPadRecord> change)
	{
		lock (gate)
		{
			XboxSeriesPadRecord? before = pads.TryGetValue(tag, out XboxSeriesPadRecord? r) ? r : null;
			XboxSeriesPadRecord after = change(before);
			if (after == before)
			{
				return;
			}
			pads[tag] = after;
			Save();
		}
	}

	// Caller holds the lock (or is the constructor).
	private void Save()
	{
		if (path == null)
		{
			return;
		}
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
			string temp = path + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(new Saved(Convert.ToBase64String(salt), new Dictionary<string, XboxSeriesPadRecord>(pads))));
			File.Move(temp, path, overwrite: true);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			Diagnostics.AppLog.Write($"xbox series store: save failed ({e.GetType().Name})");
		}
	}

	private static Saved? Read(string? path)
	{
		if (path == null)
		{
			return null;
		}
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) : null;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			Diagnostics.AppLog.Write($"xbox series store: not readable ({e.GetType().Name}), starting over");
			return null;
		}
	}

	private sealed record Saved(string? Salt, Dictionary<string, XboxSeriesPadRecord>? Pads);
}

// Time the PC was awake since it booted, without the time spent in sleep or hibernation: a timeout measured with it does
// not run out because the PC slept.
public static class AwakeClock
{
	public static TimeSpan Now => OperatingSystem.IsWindows() && QueryUnbiasedInterruptTime(out ulong ticks)
		? TimeSpan.FromTicks((long)ticks)
		: TimeSpan.FromMilliseconds(Environment.TickCount64);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
