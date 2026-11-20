// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kopru.Platform.Linux.Diagnostics;

namespace Kopru.LinuxSpike;

// Every line goes to the console and, with --log, to the file; both masked (LogMask: Bluetooth addresses, user name).
internal sealed class SpikeLog : IDisposable
{
	private readonly object gate = new();
	private readonly StreamWriter? file;
	private readonly DateTimeOffset started = DateTimeOffset.Now;

	public SpikeLog(string? path)
	{
		if (path != null)
		{
			file = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
		}
	}

	public string? FilePath => (file?.BaseStream as FileStream)?.Name;

	public void Write(string message)
	{
		TimeSpan since = DateTimeOffset.Now - started;
		string line = string.Create(CultureInfo.InvariantCulture, $"[{since.TotalSeconds,8:F3}] {LogMask.Mask(message)}");
		lock (gate)
		{
			Console.WriteLine(line);
			try
			{
				file?.WriteLine(line);
			}
			catch (IOException)
			{
				// The console still has it.
			}
		}
	}

	public void Dispose()
	{
		lock (gate)
		{
			file?.Dispose();
		}
	}
}
