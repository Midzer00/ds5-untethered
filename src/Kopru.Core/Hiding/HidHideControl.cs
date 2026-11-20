// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Kopru.Core.Hiding;

public sealed record CommandResult(int ExitCode, string Output, string Error);

// Runs an external program; faked in tests. Arguments are passed one by one (never joined into one command line), so
// nothing in them is split or interpreted.
public interface ICommandRunner
{
	Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
	public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
	{
		var info = new ProcessStartInfo(fileName)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (string argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}
		using Process process = Process.Start(info) ?? throw new InvalidOperationException(fileName);
		Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
		Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
		try
		{
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Cancelled or timed out: the program and everything it started go too (a pkexec dialog closes with it).
			Kill(process);
			throw;
		}
		return new CommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
	}

	private static void Kill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
		{
			// Already gone, or not ours to end (a program running as root after pkexec).
		}
	}
}

public sealed class HidHideException(string message) : Exception(message);
