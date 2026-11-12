// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Updates;

// A release version: two to four dot-separated numbers ("0.1.5"), compared number by number ("0.1.10" > "0.1.9"; a
// missing part counts as 0, so "0.2" == "0.2.0"). Anything else (letters, a leading "v", suffixes) is not a version.
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
	private readonly int[] _parts;

	private AppVersion(int[] parts) => _parts = parts;

	public static bool TryParse(string? text, out AppVersion version)
	{
		version = null!;
		if (string.IsNullOrWhiteSpace(text) || text.Length > 40)
		{
			return false;
		}
		string[] pieces = text.Trim().Split('.');
		if (pieces.Length is < 2 or > 4)
		{
			return false;
		}
		var parts = new int[4];
		for (int i = 0; i < pieces.Length; i++)
		{
			if (pieces[i].Length == 0 || pieces[i].Any(c => c is < '0' or > '9')
				|| !int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
			{
				return false;
			}
		}
		version = new AppVersion(parts);
		return true;
	}

	public static AppVersion? Parse(string? text) => TryParse(text, out AppVersion v) ? v : null;

	// The running app's version (major.minor.patch of the given assembly; the entry assembly by default).
	public static AppVersion OfAssembly(System.Reflection.Assembly? assembly = null) =>
		(assembly ?? System.Reflection.Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly).GetName().Version is { } v
			? new AppVersion([v.Major, v.Minor, Math.Max(0, v.Build), 0])
			: new AppVersion([0, 0, 0, 0]);

	public int CompareTo(AppVersion? other)
	{
		if (other is null)
		{
			return 1;
		}
		for (int i = 0; i < 4; i++)
		{
			int c = _parts[i].CompareTo(other._parts[i]);
			if (c != 0)
			{
				return c;
			}
		}
		return 0;
	}

	public bool Equals(AppVersion? other) => CompareTo(other) == 0;
	public override bool Equals(object? obj) => obj is AppVersion v && Equals(v);
	public override int GetHashCode() => HashCode.Combine(_parts[0], _parts[1], _parts[2], _parts[3]);

	public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
	public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
	public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
	public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

	// "0.1.5" (a fourth part only when it is not 0).
	public override string ToString() => _parts[3] != 0
		? string.Join('.', _parts)
		: string.Join('.', _parts.Take(3));
}
