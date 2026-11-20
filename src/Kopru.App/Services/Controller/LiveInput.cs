// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.App.Services;

// Full live input for the controller test page. While a session is open, every input report is handed over (on any
// thread): from the bridge while it runs, otherwise from a read-only open of the controller. Disposing the session
// releases the controller.
public interface ILiveInputSource
{
	IDisposable OpenLiveInput(Action<InputSnapshot> onSnapshot);
}

public sealed partial class RealControllerService : ILiveInputSource
{
	// Pause between attempts to open the controller read-only (no pad, bridge running, battery probe busy).
	public static readonly TimeSpan LiveInputRetry = TimeSpan.FromMilliseconds(500);

	public IDisposable OpenLiveInput(Action<InputSnapshot> onSnapshot) => new LiveInputSession(this, onSnapshot);

	// Live sessions open now, and every report they read directly (bridge stopped). Drift calibration listens here so it
	// also works from the test page without the bridge.
	private int liveSessions;

	private event Action<InputSnapshot>? DirectInput;

	private sealed class LiveInputSession : IDisposable
	{
		private readonly RealControllerService service;
		private readonly Action<InputSnapshot> onSnapshot;
		private readonly CancellationTokenSource cancel = new();
		// The controller selected when the page opened.
		private readonly Kopru.Core.Bridge.BridgeHost host;

		public LiveInputSession(RealControllerService service, Action<InputSnapshot> onSnapshot)
		{
			this.service = service;
			this.onSnapshot = onSnapshot;
			host = service.Selected;
			Interlocked.Increment(ref service.liveSessions);
			host.RawInput += OnBridgeInput;
			Loop = Task.Run(RunAsync);
		}

		public Task Loop { get; }

		private void OnBridgeInput(ReadOnlySpan<byte> body)
		{
			if (body.Length >= Kopru.Core.Protocol.InputState.BodySize)
			{
				onSnapshot(InputSnapshot.Parse(body));
			}
		}

		private void OnDirectInput(InputSnapshot snapshot)
		{
			onSnapshot(snapshot);
			service.DirectInput?.Invoke(snapshot);
		}

		private async Task RunAsync()
		{
			CancellationToken token = cancel.Token;
			try
			{
				while (!token.IsCancellationRequested)
				{
					if (!service.started && host.Device == null && Volatile.Read(ref service.disposed) == 0)
					{
						await host.StreamInputAsync(OnDirectInput, token).ConfigureAwait(false);
					}
					await Task.Delay(LiveInputRetry, token).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				// Closed.
			}
			catch (Exception)
			{
				// The page shows no input; nothing else depends on it.
			}
		}

		public void Dispose()
		{
			if (cancel.IsCancellationRequested)
			{
				return;
			}
			Interlocked.Decrement(ref service.liveSessions);
			host.RawInput -= OnBridgeInput;
			cancel.Cancel();
		}
	}
}
