// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Kopru.Core.Hid;
using Kopru.Core.Setup;

namespace Kopru.Core.Diagnostics;

// What is known about the controller (FirmwareFeature: a feature report already read, e.g. 0x20, or null).
public sealed record ControllerInfo(int VendorId, int ProductId, bool IsEdge, Transport Transport, string? Path, byte[]? FirmwareFeature = null)
{
	public static ControllerInfo From(HidDevice device, byte[]? firmwareFeature = null) =>
		new(device.VendorId, device.ProductId, device.IsEdge, device.Transport, device.Path, firmwareFeature);
}

// Where the export takes its parts from; any part may be missing.
public sealed record DiagnosticsSources
{
	public Func<SystemStatus?>? SystemCheck { get; init; }

	public string? SettingsPath { get; init; }

	public string? HidingStatePath { get; init; }

	public string? LogDirectory { get; init; }

	// A stored adapter test result (any text, e.g. JSON), when the app keeps one.
	public string? AdapterResultPath { get; init; }

	public ControllerInfo? Controller { get; init; }

	public int LogLines { get; init; } = 500;

	public static DiagnosticsSources Default(ControllerInfo? controller = null) => new()
	{
		SystemCheck = OperatingSystem.IsWindows() ? RunSystemCheck : null,
		SettingsPath = AppPaths.Settings,
		HidingStatePath = Path.Combine(AppPaths.DataDirectory, Hiding.HidingStateStore.FileName),
		LogDirectory = AppLog.Directory,
		AdapterResultPath = Path.Combine(AppPaths.DataDirectory, DiagnosticsExport.AdapterResultFileName),
		Controller = controller
	};

	private static SystemStatus? RunSystemCheck() =>
		OperatingSystem.IsWindows() ? Setup.SystemCheck.CreateDefault().Run() : null;
}

// One zip for bug reports. Every text is scrubbed: Bluetooth addresses and the user name in paths are masked.
public static partial class DiagnosticsExport
{
	public const string AdapterResultFileName = "adapter-check.json";

	public static async Task CreateZipAsync(string path, DiagnosticsSources? sources = null, CancellationToken cancel = default)
	{
		DiagnosticsSources s = sources ?? DiagnosticsSources.Default();
		Dictionary<string, string> entries = await Task.Run(() => Collect(s), cancel).ConfigureAwait(false);
		string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (dir != null)
		{
			Directory.CreateDirectory(dir);
		}
		await using FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
		using ZipArchive zip = new(file, ZipArchiveMode.Create);
		foreach ((string name, string text) in entries)
		{
			ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
			await using Stream stream = entry.Open();
			byte[] bytes = Encoding.UTF8.GetBytes(Scrub(text));
			await stream.WriteAsync(bytes, cancel).ConfigureAwait(false);
		}
	}

	// Masks Bluetooth addresses (AA:BB:CC:DD:EE:FF, AA-BB-..., and the 12-hex form in device instance IDs) and the
	// user name in profile paths (C:\Users\<name>\..., also JSON-escaped and with forward slashes).
	public static string Scrub(string text)
	{
		string result = SeparatedMac().Replace(text, "XX:XX:XX:XX:XX:XX");
		result = BareMac().Replace(result, "XXXXXXXXXXXX");
		result = JsonEscapedMac().Replace(result, "XXXXXXXXXXXX");
		result = UserPath().Replace(result, "${prefix}<user>");
		string user = Environment.UserName;
		if (user.Length >= 2)
		{
			result = result.Replace(@"\" + user + @"\", @"\<user>\", StringComparison.OrdinalIgnoreCase);
		}
		return result;
	}

	private static Dictionary<string, string> Collect(DiagnosticsSources s)
	{
		Dictionary<string, string> entries = new(StringComparer.Ordinal)
		{
			["system.txt"] = SystemText(s)
		};
		AddFile(entries, "settings.json", s.SettingsPath);
		AddFile(entries, "hiding-state.json", s.HidingStatePath);
		AddFile(entries, "adapter-check.json", s.AdapterResultPath);
		if (s.LogDirectory != null)
		{
			entries["log.txt"] = string.Join(Environment.NewLine, AppLog.Tail(s.LogLines, s.LogDirectory));
		}
		if (s.Controller is { } c)
		{
			entries["controller.txt"] = ControllerText(c);
		}
		return entries;
	}

	private static void AddFile(Dictionary<string, string> entries, string name, string? path)
	{
		try
		{
			if (path != null && File.Exists(path))
			{
				entries[name] = File.ReadAllText(path);
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			entries[name + ".error.txt"] = e.Message;
		}
	}

	private static string SystemText(DiagnosticsSources s)
	{
		StringBuilder b = new();
		CultureInfo inv = CultureInfo.InvariantCulture;
		b.AppendLine(inv, $"App: DS5 Untethered {typeof(DiagnosticsExport).Assembly.GetName().Version}");
		b.AppendLine(inv, $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
		b.AppendLine(inv, $".NET: {RuntimeInformation.FrameworkDescription}");
		b.AppendLine(inv, $"Culture: {CultureInfo.CurrentUICulture.Name}");
		b.AppendLine(inv, $"Created: {DateTimeOffset.Now:O}");
		b.AppendLine();
		SystemStatus? status = null;
		try
		{
			status = s.SystemCheck?.Invoke();
		}
		catch (Exception e)
		{
			b.AppendLine(inv, $"System check failed: {e.GetType().Name}: {e.Message}");
		}
		if (status != null)
		{
			b.AppendLine(inv, $"USB/IP: {Component(status.UsbIp)}");
			b.AppendLine(inv, $"HidHide: {Component(status.HidHide)}");
			b.AppendLine(inv, $"Bluetooth radio: {status.Radio}");
			b.AppendLine(inv, $"Restart pending: {status.RestartPending?.ToString() ?? "unknown"}");
			b.AppendLine(inv, $"Controllers: {status.Controllers.Count}");
			foreach (HidDevice d in status.Controllers)
			{
				b.AppendLine(inv, $"  {d.Transport} VID {d.VendorId:X4} PID {d.ProductId:X4}{(d.IsEdge ? " (Edge)" : "")} in {d.InputLength} out {d.OutputLength} feature {d.FeatureLength} {d.Path}");
			}
		}
		return b.ToString();
	}

	private static string Component(ComponentInfo c) => string.Create(CultureInfo.InvariantCulture,
		$"installed {c.Installed}, driver {c.DriverPresent}, version {c.Version ?? "?"}, at {c.Location ?? "?"}");

	private static string ControllerText(ControllerInfo c)
	{
		StringBuilder b = new();
		CultureInfo inv = CultureInfo.InvariantCulture;
		b.AppendLine(inv, $"VID {c.VendorId:X4} PID {c.ProductId:X4}");
		b.AppendLine(inv, $"Edge: {c.IsEdge}");
		b.AppendLine(inv, $"Transport: {c.Transport}");
		b.AppendLine(inv, $"Path: {c.Path ?? "?"}");
		b.AppendLine(inv, $"Firmware feature: {(c.FirmwareFeature == null ? "not read" : Convert.ToHexString(c.FirmwareFeature))}");
		return b.ToString();
	}

	[GeneratedRegex(@"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])")]
	private static partial Regex SeparatedMac();

	// Not after "-" so the last group of a GUID ({...-00805F9B34FB}) stays readable.
	[GeneratedRegex(@"(?<![0-9A-Fa-f-])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])")]
	private static partial Regex BareMac();

	// The same after "&" written as & (settings.json escapes it in device instance ids).
	[GeneratedRegex(@"(?<=\\u0026)[0-9A-Fa-f]{12}(?![0-9A-Fa-f])")]
	private static partial Regex JsonEscapedMac();

	[GeneratedRegex(@"(?<prefix>(?:\\{1,2}|/)Users(?:\\{1,2}|/))[^\\/""\r\n]+", RegexOptions.IgnoreCase)]
	private static partial Regex UserPath();
}
