// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Kopru.Core.Bridge;

// What the running bridge plugged in, kept on disk until a clean stop so a crashed run's virtual pad can be found.
public sealed record BridgeAttachState(int Port, int TcpPort, string BusId);

// %LOCALAPPDATA%\Untethered\bridge-state.json. Never throws: a state that can't be read or written is skipped.
public sealed class BridgeStateFile(string path)
{
	public const string FileName = "bridge-state.json";

	public static BridgeStateFile CreateDefault() => new(System.IO.Path.Combine(AppPaths.DataDirectory, FileName));

	public string Path { get; } = path;

	public BridgeAttachState? Load()
	{
		try
		{
			return File.Exists(Path) ? JsonSerializer.Deserialize<BridgeAttachState>(File.ReadAllText(Path)) : null;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			return null;
		}
	}

	public void Save(BridgeAttachState state)
	{
		try
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
			File.WriteAllText(Path, JsonSerializer.Serialize(state));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			Diagnostics.AppLog.Write("bridge state: save failed: " + e.Message);
		}
	}

	public void Clear()
	{
		try
		{
			File.Delete(Path);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			Diagnostics.AppLog.Write("bridge state: clear failed: " + e.Message);
		}
	}
}
