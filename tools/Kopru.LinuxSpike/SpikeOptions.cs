// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Platform.Linux.UsbIp;

namespace Kopru.LinuxSpike;

internal sealed record SpikeOptions
{
	public const string Usage = """
		Kopru.LinuxSpike - runs the DS5 Untethered bridge headless on Linux and logs what happens.

		Usage: Kopru.LinuxSpike [options]

		  --list              List the DualSense hidraw nodes and the system checks, then exit.
		  --attach MODE       How the virtual pad gets plugged in with usbip (needs root):
		                        auto    direct when running as root, else manual (default)
		                        direct  run "usbip attach" ourselves (the spike must run as root)
		                        sudo    run "sudo usbip attach" (sudo asks for the password)
		                        manual  print the exact "sudo usbip attach" command and wait
		  --log FILE          Also write the log to FILE (Bluetooth addresses and the user name are masked).
		  --seconds N         Stop after N seconds (default: run until Ctrl+C).
		  --no-trim           Send output reports at the full length (as Windows does) instead of the length the report
		                      descriptor declares. Only for comparing when haptics or triggers fail.
		  --help              Show this text.
		""";

	public bool Help { get; init; }

	public bool ListOnly { get; init; }

	public UsbIpAttachMode AttachMode { get; init; } = UsbIpAttachMode.Auto;

	public string? LogFile { get; init; }

	public int? Seconds { get; init; }

	public bool NoTrim { get; init; }

	public static SpikeOptions? Parse(IReadOnlyList<string> args, out string? error)
	{
		error = null;
		SpikeOptions options = new();
		for (int i = 0; i < args.Count; i++)
		{
			string arg = args[i];
			string? Next()
			{
				return i + 1 < args.Count ? args[++i] : null;
			}
			switch (arg)
			{
				case "--help" or "-h":
					options = options with { Help = true };
					break;
				case "--list":
					options = options with { ListOnly = true };
					break;
				case "--no-trim":
					options = options with { NoTrim = true };
					break;
				case "--log":
					string? file = Next();
					if (string.IsNullOrWhiteSpace(file))
					{
						error = "--log needs a file name";
						return null;
					}
					options = options with { LogFile = file };
					break;
				case "--seconds":
					if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0)
					{
						error = "--seconds needs a positive whole number";
						return null;
					}
					options = options with { Seconds = seconds };
					break;
				case "--attach":
					string? mode = Next();
					if (!Enum.TryParse(mode, ignoreCase: true, out UsbIpAttachMode parsed) || !Enum.IsDefined(parsed) || int.TryParse(mode, out _))
					{
						error = "--attach needs one of: auto, direct, sudo, manual";
						return null;
					}
					options = options with { AttachMode = parsed };
					break;
				default:
					error = "unknown option: " + arg;
					return null;
			}
		}
		return options;
	}
}
