// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Kopru.Core.Diagnostics;

// A tiny rolling file log: %LOCALAPPDATA%\Untethered\logs\app.log, moved to app.1.log when it passes MaxBytes.
// Never throws; a log that can't be written is skipped.
public static class AppLog
{
	public const string FileName = "app.log";

	public const string PreviousFileName = "app.1.log";

	public static readonly string DefaultDirectory = Path.Combine(AppPaths.DataDirectory, "logs");

	private static readonly object Gate = new();

	private static string directory = DefaultDirectory;

	public static long MaxBytes { get; set; } = 512 * 1024;

	public static string Directory
	{
		get
		{
			lock (Gate)
			{
				return directory;
			}
		}
		set
		{
			lock (Gate)
			{
				directory = value;
			}
		}
	}

	// Raised after every Write with the message (tests watch for their lines).
	public static event Action<string>? Written;

	public static void Write(string message)
	{
		string line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " +
			message.ReplaceLineEndings(" ") + Environment.NewLine;
		lock (Gate)
		{
			try
			{
				System.IO.Directory.CreateDirectory(directory);
				string file = Path.Combine(directory, FileName);
				var info = new FileInfo(file);
				if (info.Exists && info.Length + line.Length > MaxBytes)
				{
					File.Move(file, Path.Combine(directory, PreviousFileName), true);
				}
				File.AppendAllText(file, line, Encoding.UTF8);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				// Logging is best effort.
			}
		}
		Written?.Invoke(message);
	}

	// The last lines of the log (older file first).
	public static IReadOnlyList<string> Tail(int lines, string? fromDirectory = null)
	{
		List<string> all = [];
		lock (Gate)
		{
			string dir = fromDirectory ?? directory;
			foreach (string name in new[] { PreviousFileName, FileName })
			{
				try
				{
					string file = Path.Combine(dir, name);
					if (File.Exists(file))
					{
						all.AddRange(File.ReadAllLines(file));
					}
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException)
				{
					// Skipped.
				}
			}
		}
		return all.Count <= lines ? all : all.GetRange(all.Count - lines, lines);
	}
}
