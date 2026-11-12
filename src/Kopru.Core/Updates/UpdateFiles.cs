// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Updates;

// The few file operations the updater and the settings backup need (tests use an in-memory one).
public interface IUpdateFiles
{
	bool Exists(string path);
	void CreateDirectory(string path);
	// Creates or truncates.
	Stream Create(string path);
	Stream OpenRead(string path);
	void Delete(string path);
	void Move(string from, string to);
	// Copies and stamps the copy with the current time (the backups are ordered by it).
	void Copy(string from, string to);
	IEnumerable<string> List(string folder, string pattern);
	DateTime LastWriteUtc(string path);
}

public sealed class PhysicalUpdateFiles : IUpdateFiles
{
	public static PhysicalUpdateFiles Instance { get; } = new();

	public bool Exists(string path) => File.Exists(path);
	public void CreateDirectory(string path) => Directory.CreateDirectory(path);
	public Stream Create(string path) => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
	public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
	public void Delete(string path) => File.Delete(path);
	public void Move(string from, string to) => File.Move(from, to, overwrite: true);

	public void Copy(string from, string to)
	{
		File.Copy(from, to, overwrite: true);
		File.SetLastWriteTimeUtc(to, DateTime.UtcNow);
	}

	public IEnumerable<string> List(string folder, string pattern) =>
		Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern) : [];

	public DateTime LastWriteUtc(string path) => File.GetLastWriteTimeUtc(path);
}
