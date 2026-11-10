// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kopru.Core.Hiding;

// One running copy of the app: process id plus start time, so a reused process id is not mistaken for an old run.
public readonly record struct HidingOwner(int ProcessId, long StartedUtcTicks)
{
	// Claims imported from the flat file of older versions: their owner is unknown and treated as exited.
	public static readonly HidingOwner Unknown = new(0, 0);

	public static HidingOwner Current { get; } = CreateCurrent();

	private static HidingOwner CreateCurrent()
	{
		using Process self = Process.GetCurrentProcess();
		return new HidingOwner(self.Id, self.StartTime.ToUniversalTime().Ticks);
	}

	// True while that process still runs (same id and the same start time).
	public static bool IsRunning(HidingOwner owner)
	{
		if (owner == Current)
		{
			return true;
		}
		if (owner.ProcessId <= 0)
		{
			return false;
		}
		try
		{
			using Process process = Process.GetProcessById(owner.ProcessId);
			return Math.Abs(process.StartTime.ToUniversalTime().Ticks - owner.StartedUtcTicks) < TimeSpan.TicksPerSecond;
		}
		catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
		{
			return false; // exited, or a process we cannot inspect (not one of ours)
		}
	}
}

public enum HidingClaimKind
{
	Cloak,
	Device,
	App
}

// Session: the bridge's hiding (undone when the bridge stops). Early: our exe allowed at app start (undone on exit).
public enum HidingClaimScope
{
	Session,
	Early
}

// "This copy of our app put this entry into HidHide (or shares one another running copy put there)". An entry is
// undone only when the last claim on it goes away; entries without any claim were not made by us and are never touched.
public sealed class HidingClaim
{
	public int ProcessId { get; set; }
	public long StartedUtcTicks { get; set; }
	public HidingClaimScope Scope { get; set; }
	public HidingClaimKind Kind { get; set; }
	public string Value { get; set; } = "";

	[JsonIgnore]
	public HidingOwner Owner => new(ProcessId, StartedUtcTicks);

	public static HidingClaim For(HidingOwner owner, HidingClaimScope scope, HidingClaimKind kind, string value = "") =>
		new() { ProcessId = owner.ProcessId, StartedUtcTicks = owner.StartedUtcTicks, Scope = scope, Kind = kind, Value = value };

	public bool SameEntry(HidingClaimKind kind, string value) =>
		Kind == kind && string.Equals(Value, value, StringComparison.OrdinalIgnoreCase);
}

public sealed class HidingLedger
{
	public int Version { get; set; } = 2;
	public List<HidingClaim> Claims { get; set; } = [];

	public bool IsClaimed(HidingClaimKind kind, string value = "") => Claims.Any(c => c.SameEntry(kind, value));
}

// The flat record of versions before the ledger (hiding-state.json); only read, to recover what they left behind.
public sealed class HidingChanges
{
	public List<string> AddedDevices { get; set; } = [];
	public List<string> AddedApps { get; set; } = [];
	public bool TurnedCloakOn { get; set; }
	public List<string> EarlyApps { get; set; } = [];

	[JsonIgnore]
	public bool IsEmpty => AddedDevices.Count == 0 && AddedApps.Count == 0 && !TurnedCloakOn && EarlyApps.Count == 0;

	public static HidingChanges? Load(string path)
	{
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<HidingChanges>(File.ReadAllText(path)) : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public IEnumerable<HidingClaim> ToClaims(HidingOwner owner)
	{
		if (TurnedCloakOn)
		{
			yield return HidingClaim.For(owner, HidingClaimScope.Session, HidingClaimKind.Cloak);
		}
		foreach (string id in AddedDevices)
		{
			yield return HidingClaim.For(owner, HidingClaimScope.Session, HidingClaimKind.Device, id);
		}
		foreach (string app in AddedApps)
		{
			yield return HidingClaim.For(owner, HidingClaimScope.Session, HidingClaimKind.App, app);
		}
		foreach (string app in EarlyApps)
		{
			yield return HidingClaim.For(owner, HidingClaimScope.Early, HidingClaimKind.App, app);
		}
	}
}

// The ledger of HidHide entries our app made (hiding-ledger.json in %LOCALAPPDATA%\Untethered, the same for every
// exe path). Every change is written here before HidHide is touched and removed only after it was undone, so a crash
// or kill never loses it. All copies of the app share the file under a lock file; claims of a copy that is still
// running are never undone by another copy's recovery.
public sealed class HidingStateStore(string path, HidingOwner? owner = null, Func<HidingOwner, bool>? isRunning = null)
{
	public const string FileName = "hiding-ledger.json";

	// The flat file of older versions (recovered and then removed).
	public const string LegacyFileName = "hiding-state.json";

	private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

	private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

	public string Path { get; } = path;

	public HidingOwner Owner { get; } = owner ?? HidingOwner.Current;

	public static HidingStateStore CreateDefault() => new(System.IO.Path.Combine(AppPaths.DataDirectory, FileName));

	// Where older versions kept their flat file: the Untethered folder, then the folder before the rename.
	public static IReadOnlyList<string> LegacyPaths() =>
	[
		System.IO.Path.Combine(AppPaths.DataDirectory, LegacyFileName),
		System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kopru", LegacyFileName)
	];

	public bool IsRunning(HidingOwner other) => other == Owner || (isRunning ?? HidingOwner.IsRunning)(other);

	public HidingLedger Load()
	{
		try
		{
			if (!File.Exists(Path))
			{
				return new HidingLedger();
			}
			try
			{
				return JsonSerializer.Deserialize<HidingLedger>(File.ReadAllText(Path), Json) ?? new HidingLedger();
			}
			catch (JsonException)
			{
				// Keep the damaged file for diagnostics instead of silently dropping what it recorded.
				File.Move(Path, Path + ".damaged", overwrite: true);
				Diagnostics.AppLog.Write("hiding: the ledger file was damaged; kept as .damaged, starting empty");
				return new HidingLedger();
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			throw new HidHideException(Localization.Text.HidHideCommandFailed(FileName, e.Message));
		}
	}

	public void Save(HidingLedger ledger)
	{
		try
		{
			if (ledger.Claims.Count == 0)
			{
				if (File.Exists(Path))
				{
					File.Delete(Path);
				}
				return;
			}
			string? dir = System.IO.Path.GetDirectoryName(Path);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}
			string temp = Path + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(ledger, Json));
			File.Move(temp, Path, overwrite: true);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			throw new HidHideException(Localization.Text.HidHideCommandFailed(FileName, e.Message));
		}
	}

	// Serializes ledger changes (and the HidHide commands they cover) across all running copies of the app.
	public async Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
	{
		string lockPath = Path + ".lock";
		var clock = Stopwatch.StartNew();
		while (true)
		{
			try
			{
				string? dir = System.IO.Path.GetDirectoryName(lockPath);
				if (!string.IsNullOrEmpty(dir))
				{
					Directory.CreateDirectory(dir);
				}
				return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				if (clock.Elapsed > LockTimeout)
				{
					throw new HidHideException(Localization.Text.HidHideCommandFailed(FileName, e.Message));
				}
			}
			await Task.Delay(50, cancellationToken).ConfigureAwait(false);
		}
	}
}
