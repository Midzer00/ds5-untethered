// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.LinuxSpike;

internal static class Program
{
	public static async Task<int> Main(string[] args)
	{
		SpikeOptions? options = SpikeOptions.Parse(args, out string? error);
		if (options == null)
		{
			Console.Error.WriteLine(error);
			Console.Error.WriteLine();
			Console.Error.WriteLine(SpikeOptions.Usage);
			return 2;
		}
		if (options.Help)
		{
			Console.WriteLine(SpikeOptions.Usage);
			return 0;
		}
		if (!OperatingSystem.IsLinux())
		{
			Console.Error.WriteLine("Kopru.LinuxSpike runs on Linux only (it uses hidraw and the usbip client).");
			return 2;
		}
		using SpikeLog log = new(options.LogFile);
		try
		{
			return await new Spike(options, log).RunAsync().ConfigureAwait(false);
		}
		catch (Exception e)
		{
			log.Write($"FATAL: {e.GetType().Name}: {e.Message}");
			log.Write(e.StackTrace ?? "");
			return 1;
		}
	}
}
