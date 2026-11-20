// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using System.Text;

namespace Kopru.Core.Instance;

// What Program needs from the one-copy guard (UnixSingleInstance; tests fake it).
public interface IAppInstance : IDisposable
{
	// True: no other copy ran when this one started.
	bool IsFirst { get; }

	// First copy: calls onShow (on a background thread) for every later start, then answers it.
	void Listen(Action onShow);

	// Later copy: asks the first one to show its window. True when it answered within the timeout.
	bool AskFirstToShow(TimeSpan timeout);
}

// One running copy per user on Linux (and other Unix systems). The first copy holds an exclusive lock on a lock file
// (.NET takes flock(LOCK_EX | LOCK_NB) for FileShare.None on Unix; the kernel drops it when the process ends, crash
// included) and listens on a Unix domain socket next to it. A later start fails to take the lock, connects to the socket,
// sends "show" and waits for "shown". Both files live in $XDG_RUNTIME_DIR (private to the user), else in the app's data
// folder.
public sealed class UnixSingleInstance : IAppInstance
{
	public static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromSeconds(3);

	public const string LockFileName = "instance.lock";
	public const string SocketFileName = "instance.sock";
	private const string ShowRequest = "show\n";
	private const string ShownAnswer = "shown\n";

	private readonly string socketPath;
	private readonly Stream? lockFile;
	private Socket? listener;
	private CancellationTokenSource? listening;
	private bool disposed;

	private UnixSingleInstance(string socketPath, Stream? lockFile)
	{
		this.socketPath = socketPath;
		this.lockFile = lockFile;
	}

	public bool IsFirst => lockFile != null;

	// The folder the lock and the socket go in: $XDG_RUNTIME_DIR/ds5-untethered when the runtime folder exists, else the
	// data folder.
	public static string DefaultDirectory()
	{
		string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
		return !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)
			? Path.Combine(runtime, AppPaths.XdgFolderName)
			: AppPaths.DataDirectory;
	}

	// Never throws: a folder that can't be made or a lock that can't be opened for another reason counts as the first copy
	// without a guard (the app still starts).
	public static UnixSingleInstance Acquire(string? directory = null)
	{
		directory ??= DefaultDirectory();
		string socket = Path.Combine(directory, SocketFileName);
		try
		{
			Directory.CreateDirectory(directory);
			FileStream file = new(Path.Combine(directory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			return new UnixSingleInstance(socket, file);
		}
		catch (IOException) when (File.Exists(Path.Combine(directory, LockFileName)))
		{
			// Locked by the running copy.
			return new UnixSingleInstance(socket, null);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			Diagnostics.AppLog.Write("instance: no single-instance lock (" + e.Message + ")");
			return new UnixSingleInstance(socket, Stream.Null);
		}
	}

	public void Listen(Action onShow)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (!IsFirst)
		{
			throw new InvalidOperationException("Only the first copy listens.");
		}
		if (listener != null)
		{
			throw new InvalidOperationException("Already listening.");
		}
		try
		{
			// A socket file left by a crashed copy: we hold the lock, so nobody listens on it any more.
			File.Delete(socketPath);
			Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			socket.Bind(new UnixDomainSocketEndPoint(socketPath));
			socket.Listen(4);
			listener = socket;
		}
		catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
		{
			Diagnostics.AppLog.Write("instance: cannot listen for later starts (" + e.Message + ")");
			return;
		}
		listening = new CancellationTokenSource();
		_ = AcceptLoopAsync(listener, onShow, listening.Token);
	}

	private static async Task AcceptLoopAsync(Socket socket, Action onShow, CancellationToken cancel)
	{
		while (!cancel.IsCancellationRequested)
		{
			Socket client;
			try
			{
				client = await socket.AcceptAsync(cancel).ConfigureAwait(false);
			}
			catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
			{
				return;
			}
			_ = Task.Run(async () =>
			{
				using (client)
				{
					try
					{
						byte[] buffer = new byte[64];
						using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
						timeout.CancelAfter(TimeSpan.FromSeconds(2));
						int read = await client.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
						if (Encoding.ASCII.GetString(buffer, 0, read) != ShowRequest)
						{
							return;
						}
						try
						{
							onShow();
						}
						finally
						{
							await client.SendAsync(Encoding.ASCII.GetBytes(ShownAnswer), SocketFlags.None, CancellationToken.None).ConfigureAwait(false);
						}
					}
					catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
					{
						// The other start gave up or this copy is exiting.
					}
				}
			}, CancellationToken.None);
		}
	}

	public bool AskFirstToShow(TimeSpan timeout)
	{
		if (IsFirst)
		{
			throw new InvalidOperationException("The first copy has no one to ask.");
		}
		DateTime deadline = DateTime.UtcNow + timeout;
		while (true)
		{
			try
			{
				using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
				socket.Connect(new UnixDomainSocketEndPoint(socketPath));
				socket.Send(Encoding.ASCII.GetBytes(ShowRequest));
				TimeSpan left = deadline - DateTime.UtcNow;
				socket.ReceiveTimeout = (int)Math.Max(1, left.TotalMilliseconds);
				byte[] buffer = new byte[64];
				int read = socket.Receive(buffer);
				return Encoding.ASCII.GetString(buffer, 0, read) == ShownAnswer;
			}
			catch (SocketException)
			{
				// The first copy is still starting (no socket yet) or hung.
				if (DateTime.UtcNow >= deadline)
				{
					return false;
				}
				Thread.Sleep(50);
			}
		}
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}
		disposed = true;
		listening?.Cancel();
		listener?.Dispose();
		if (listener != null)
		{
			try
			{
				File.Delete(socketPath);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				// The next first copy deletes it.
			}
		}
		lockFile?.Dispose();
	}
}
