// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Kopru.Core.Games;

namespace Kopru.Core.Steam;

// Reads the PlayStation controller categories of a few apps from Steam's own cache on this PC, <Steam>\appcache\appinfo.vdf
// (read only; nothing is sent anywhere). Binary layout, little-endian:
//   header: uint32 magic (0x07564427 v27, 0x07564428 v28, 0x07564429 v29), uint32 universe, v29: int64 offset of the key
//   string table (uint32 count, then that many zero-terminated UTF-8 strings);
//   entries until app id 0: uint32 app id, uint32 size (bytes after this field), uint32 state, uint32 last update,
//   uint64 access token, 20-byte SHA-1, uint32 change number, v28+: 20-byte SHA-1 of the binary data, then one binary
//   KeyValues object: per field a type byte (0 object, 1 string, 2 int32, 3 float32, 4 pointer, 5 wide string, 6 colour,
//   7 uint64, 10 int64, 8 or 11 end of object), the key (a zero-terminated string; v29: a uint32 index into the string
//   table) and the value.
// The categories sit at appinfo/common/category/category_<n> ("1" when set): 57/58 give DualSense, else 55/56 DualShock 4.
// The kind of app sits at appinfo/common/type ("Game", "Application", "Tool", "Demo", ...).
// Entries of the apps not asked for are skipped by their size. Any problem with the file gives null (the caller ignores it).
public static class SteamAppInfo
{
	public const uint Magic27 = 0x07564427;
	public const uint Magic28 = 0x07564428;
	public const uint Magic29 = 0x07564429;

	// Large enough for any real entry; a bigger size means the file is not what we think it is.
	private const int MaxEntrySize = 64 * 1024 * 1024;
	private const int MaxDepth = 32;

	// Far more keys than a real string table has (a few thousand); a bigger count means a broken file.
	private const uint MaxStrings = 1_000_000;

	// The PlayStation support of the asked apps that appinfo.vdf has (an app without the categories is left out), or null when
	// the file cannot be read as appinfo.vdf.
	public static Dictionary<long, PlayStationPad>? ReadPlayStation(Stream stream, IReadOnlyCollection<long> appIds) =>
		ReadDetails(stream, appIds) is { } details
			? details.Where(p => p.Value.PlayStation != null).ToDictionary(p => p.Key, p => p.Value.PlayStation!.Value)
			: null;

	// What appinfo.vdf says about each asked app it has (PlayStation support and the kind of app), or null when the file cannot
	// be read as appinfo.vdf.
	public static Dictionary<long, SteamAppDetails>? ReadDetails(Stream stream, IReadOnlyCollection<long> appIds) =>
		Safe(() => Entries(stream, appIds).ToDictionary(p => p.Key, p => new SteamAppDetails(PadFor(p.Value.Categories), p.Value.Type)));

	// The categories set for each asked app (app id -> category numbers), or null when the file cannot be read.
	public static Dictionary<long, HashSet<int>>? ReadCategories(Stream stream, IReadOnlyCollection<long> appIds) =>
		Safe(() => Entries(stream, appIds).ToDictionary(p => p.Key, p => p.Value.Categories));

	private static T? Safe<T>(Func<T> read) where T : class
	{
		try
		{
			return read();
		}
		catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or ArgumentException or OverflowException
			or NotSupportedException or DecoderFallbackException or OutOfMemoryException)
		{
			return null;
		}
	}

	public static PlayStationPad? PadFor(IReadOnlySet<int> categories) =>
		categories.Contains(57) || categories.Contains(58) ? PlayStationPad.DualSense
		: categories.Contains(55) || categories.Contains(56) ? PlayStationPad.DualShock4
		: null;

	private sealed record Entry(HashSet<int> Categories, string? Type);

	private static Dictionary<long, Entry> Entries(Stream stream, IReadOnlyCollection<long> appIds)
	{
		var wanted = new HashSet<long>(appIds);
		var result = new Dictionary<long, Entry>();
		if (wanted.Count == 0)
		{
			return result;
		}
		if (!stream.CanSeek)
		{
			throw new NotSupportedException("appinfo.vdf: stream must be seekable");
		}
		using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
		uint magic = reader.ReadUInt32();
		if (magic is not (Magic27 or Magic28 or Magic29))
		{
			throw new InvalidDataException($"appinfo.vdf: unknown version 0x{magic:X8}");
		}
		reader.ReadUInt32(); // universe
		string[]? strings = null;
		if (magic == Magic29)
		{
			long tableAt = reader.ReadInt64();
			long entriesAt = stream.Position;
			if (tableAt < entriesAt || tableAt > stream.Length)
			{
				throw new InvalidDataException("appinfo.vdf: string table outside the file");
			}
			stream.Position = tableAt;
			uint count = reader.ReadUInt32();
			if (count > MaxStrings || count > (stream.Length - tableAt))
			{
				throw new InvalidDataException("appinfo.vdf: string table too large");
			}
			strings = new string[count];
			for (int i = 0; i < count; i++)
			{
				strings[i] = ReadString(reader);
			}
			stream.Position = entriesAt;
		}
		int headerAfterSize = 4 + 4 + 8 + 20 + 4 + (magic >= Magic28 ? 20 : 0);
		while (true)
		{
			uint appId = reader.ReadUInt32();
			if (appId == 0)
			{
				break;
			}
			uint size = reader.ReadUInt32();
			if (size < headerAfterSize || size > MaxEntrySize)
			{
				throw new InvalidDataException("appinfo.vdf: bad entry size");
			}
			long end = stream.Position + size;
			if (end > stream.Length)
			{
				throw new EndOfStreamException();
			}
			if (wanted.Contains(appId))
			{
				stream.Position += headerAfterSize;
				var found = new HashSet<int>();
				var path = new List<string>();
				string? kind = null;
				ReadObject(reader, strings, end, path, found, ref kind, 0);
				result[appId] = new Entry(found, kind);
				if (result.Count == wanted.Count)
				{
					break;
				}
			}
			stream.Position = end;
		}
		return result;
	}

	// Reads one object's fields up to its end marker, collecting common/category/category_<n> set to a non-zero value and
	// common/type.
	private static void ReadObject(BinaryReader reader, string[]? strings, long end, List<string> path, HashSet<int> categories, ref string? kind,
		int depth)
	{
		if (depth > MaxDepth)
		{
			throw new InvalidDataException("appinfo.vdf: nested too deep");
		}
		Stream stream = reader.BaseStream;
		while (true)
		{
			if (stream.Position >= end)
			{
				throw new InvalidDataException("appinfo.vdf: entry ends inside an object");
			}
			byte type = reader.ReadByte();
			if (type is 8 or 11)
			{
				return;
			}
			string key = ReadKey(reader, strings);
			bool inCategory = path.Count >= 2 && path[^1] == "category" && path[^2] == "common";
			switch (type)
			{
				case 0:
					path.Add(key);
					ReadObject(reader, strings, end, path, categories, ref kind, depth + 1);
					path.RemoveAt(path.Count - 1);
					break;
				case 1:
					string text = ReadString(reader);
					if (inCategory && text.Trim() is { Length: > 0 } t && t != "0")
					{
						AddCategory(key, categories);
					}
					else if (path.Count == 2 && path[1] == "common" && string.Equals(key, "type", StringComparison.OrdinalIgnoreCase)
						&& text.Trim().Length > 0)
					{
						kind = text.Trim();
					}
					break;
				case 2:
				case 4:
				case 6:
					int number = reader.ReadInt32();
					if (inCategory && number != 0)
					{
						AddCategory(key, categories);
					}
					break;
				case 3:
					reader.ReadSingle();
					break;
				case 5:
					ReadWideString(reader);
					break;
				case 7:
				case 10:
					long big = reader.ReadInt64();
					if (inCategory && big != 0)
					{
						AddCategory(key, categories);
					}
					break;
				default:
					throw new InvalidDataException($"appinfo.vdf: unknown field type {type}");
			}
		}
	}

	private static void AddCategory(string key, HashSet<int> categories)
	{
		const string prefix = "category_";
		if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			&& int.TryParse(key.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
		{
			categories.Add(n);
		}
	}

	private static string ReadKey(BinaryReader reader, string[]? strings)
	{
		if (strings == null)
		{
			return ReadString(reader);
		}
		uint index = reader.ReadUInt32();
		return index < strings.Length ? strings[index] : throw new InvalidDataException("appinfo.vdf: bad key index");
	}

	private static string ReadString(BinaryReader reader)
	{
		var bytes = new List<byte>(32);
		while (true)
		{
			byte b = reader.ReadByte();
			if (b == 0)
			{
				break;
			}
			bytes.Add(b);
			if (bytes.Count > 1 << 20)
			{
				throw new InvalidDataException("appinfo.vdf: string too long");
			}
		}
		return Encoding.UTF8.GetString(bytes.ToArray());
	}

	private static void ReadWideString(BinaryReader reader)
	{
		int count = 0;
		while (reader.ReadUInt16() != 0)
		{
			if (++count > 1 << 20)
			{
				throw new InvalidDataException("appinfo.vdf: string too long");
			}
		}
	}

	// Steam's appinfo.vdf on this PC, read for these apps: their PlayStation support (see ReadLocalDetails).
	public static Dictionary<long, PlayStationPad> ReadLocal(string? steamPath, IReadOnlyCollection<long> appIds, Action<string>? log = null) =>
		ReadLocalDetails(steamPath, appIds, log).Where(p => p.Value.PlayStation != null).ToDictionary(p => p.Key, p => p.Value.PlayStation!.Value);

	// Steam's appinfo.vdf on this PC, read for these apps; empty when Steam, the file or any part of it can't be read. What
	// was read is kept while the file's size and time stay the same, so a scan reads the file again only when Steam changed
	// it or an app not read yet is asked for. Logs the first problem once per process (never a path).
	public static Dictionary<long, SteamAppDetails> ReadLocalDetails(string? steamPath, IReadOnlyCollection<long> appIds, Action<string>? log = null)
	{
		if (string.IsNullOrEmpty(steamPath) || appIds.Count == 0)
		{
			return [];
		}
		string file = Path.Combine(steamPath, "appcache", "appinfo.vdf");
		Dictionary<long, SteamAppDetails>? found;
		try
		{
			var info = new FileInfo(file);
			if (!info.Exists)
			{
				return [];
			}
			var stamp = (file, info.Length, info.LastWriteTimeUtc);
			lock (cacheGate)
			{
				if (cacheStamp != stamp)
				{
					cacheStamp = stamp;
					cacheBroken = false;
					cache.Clear();
				}
				List<long> missing = cacheBroken ? [] : appIds.Where(id => !cache.ContainsKey(id)).Distinct().ToList();
				if (missing.Count > 0)
				{
					Interlocked.Increment(ref localReads);
					Dictionary<long, SteamAppDetails>? read;
					try
					{
						using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
						read = ReadDetails(stream, missing);
					}
					catch (Exception e) when (e is IOException or UnauthorizedAccessException)
					{
						// Locked while Steam writes it: tried again at the next scan.
						cacheStamp = null;
						throw;
					}
					if (read == null)
					{
						// Not readable as appinfo.vdf: not read again until Steam writes it again.
						cacheBroken = true;
						cache.Clear();
					}
					else
					{
						foreach (long id in missing)
						{
							cache[id] = read.TryGetValue(id, out SteamAppDetails? details) ? details : null;
						}
					}
				}
				found = cacheBroken ? null : appIds.Distinct().Where(id => cache.TryGetValue(id, out SteamAppDetails? d) && d != null)
					.ToDictionary(id => id, id => cache[id]!);
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException
			or NotSupportedException or OutOfMemoryException)
		{
			found = null;
		}
		if (found == null)
		{
			if (Interlocked.Exchange(ref loggedProblem, 1) == 0)
			{
				(log ?? Diagnostics.AppLog.Write)("steam: appinfo cache not readable; controller support of installed games comes from the shipped list only");
			}
			return [];
		}
		return found;
	}

	// How many times ReadLocalDetails opened the file (for the tests).
	public static int LocalReads => Volatile.Read(ref localReads);

	private static int loggedProblem;
	private static int localReads;
	private static readonly object cacheGate = new();
	private static (string File, long Length, DateTime Written)? cacheStamp;
	private static bool cacheBroken;
	private static readonly Dictionary<long, SteamAppDetails?> cache = [];
}

// What Steam's appinfo.vdf says about an app: its PlayStation controller support (null: none listed) and its kind
// (common/type, e.g. "Game", "Application", "Tool"; null when not listed).
public sealed record SteamAppDetails(PlayStationPad? PlayStation, string? Type)
{
	// Kinds that are games: "Game", "Demo", and "Beta" (playtests). Wallpaper Engine, Lossless Scaling and other software
	// are "Application" or "Tool".
	public static bool IsGameType(string? type) =>
		type != null && (type.Equals("game", StringComparison.OrdinalIgnoreCase) || type.Equals("demo", StringComparison.OrdinalIgnoreCase)
			|| type.Equals("beta", StringComparison.OrdinalIgnoreCase));

	public bool IsGame => IsGameType(Type);
}
