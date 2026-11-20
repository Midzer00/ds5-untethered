// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// The virtual controller type games see (DualSense by default; Xbox Series, Xbox 360 or DualShock 4 as GameModeController
// decides). A change while running swaps only the virtual pad: the old one is unplugged first, then the new one is plugged
// in; the Bluetooth controller stays connected.
//
// Xbox Series: this build has no Xbox Series pad (its GIP pad needs the Windows driver path); a game or the user choosing it
// gets Xbox 360.
public sealed partial class BridgeHost
{
	private volatile VirtualPadType padType = VirtualPadType.DualSense;

	// The type games get now or on the next connection.
	public VirtualPadType PadType => padType;

	// Raised after a running bridge swapped the virtual pad (not on a fresh connection).
	public event Action<VirtualPadType>? PadTypeChanged;

	// Kept for the game mode (it listens for both); never raised: there is no Xbox Series pad in this build.
	public event Action<string>? XboxSeriesFailed
	{
		add { }
		remove { }
	}

	public event Action? XboxSeriesStarted
	{
		add { }
		remove { }
	}

	// GameModeController calls this when the game in front changes.
	public void UsePad(VirtualPadType type) => padType = type;

	// The type the virtual pad is made as: the selected type, with Xbox Series as Xbox 360.
	private VirtualPadType EffectivePadType => padType == VirtualPadType.XboxSeries ? VirtualPadType.Xbox360 : padType;

	private async Task SwapPadIfNeededAsync(Connection connection, CancellationToken token)
	{
		VirtualPadType wanted = EffectivePadType;
		if (connection.PadType == wanted)
		{
			return;
		}
		try
		{
			await connection.SwapPadAsync(wanted, token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception e)
		{
			Record(BridgeError.AttachFailed, e.Message);
			connection.LostReason = "virtual pad swap failed";
			connection.Lost = true;
			return;
		}
		ApplySpeakerTo(connection);
		ApplyMicTo(connection);
		Diagnostics.AppLog.Write($"virtual pad {wanted}");
		linkQuality.Restart(NowMs, "virtual pad swap");
		PadTypeChanged?.Invoke(wanted);
	}

	private sealed partial class Connection
	{
		private readonly DeviceIdentity identity;

		private readonly Hid.IHidLink link;

		public VirtualPadType PadType => pad.Type;

		// type: EffectivePadType (never Xbox Series).
		private IVirtualPad CreatePad(VirtualPadType type) => type switch
		{
			VirtualPadType.Xbox360 or VirtualPadType.XboxSeries => new VirtualXbox360(host.BusId),
			VirtualPadType.DualShock4 => new VirtualDualShock4(host.BusId, identity.FeatureReports.GetValueOrDefault(CalibrationOverlay.Feature))
			{
				PreciseWaitFactory = host.PreciseWaitFactory
			},
			_ => new VirtualDualSense(identity, host.BusId) { PreciseWaitFactory = host.PreciseWaitFactory }
		};

		// A pad that has something to say before it is unplugged ("powering off"); the others skip this.
		private async Task NoticeUnplugAsync()
		{
			if (pad is not IUnplugNotice notice)
			{
				return;
			}
			try
			{
				await notice.BeforeUnplugAsync().ConfigureAwait(false);
			}
			catch (Exception e)
			{
				Diagnostics.AppLog.Write($"virtual pad unplug notice failed: {e.Message}");
			}
		}

		// A pad swap keeps the app's mute LED in the new bridge, so no game report gets the game's own value through before
		// the mic settings are applied again.
		private ControllerBridge CreateBridge()
		{
			ControllerBridge bridge = NewBridge();
			// A pad swap or a reconnect: what the game was already doing does not count as it starting again (the previous
			// bridge's edge state), and the game's first reports to the new pad don't either (quiet until the pad settled).
			if ((Bridge?.CaptureGameUse() ?? host.lastGameUse) is { } previous)
			{
				bridge.SeedGameUse(previous);
			}
			bridge.QuietGameUse(ControllerBridge.GameUseSettle);
			// The game's light goes on with the new bridge where the new pad lets a game set the light (not the Xbox pads).
			if (pad.Type.HasLight() && (Bridge?.CaptureGameLight() ?? host.lastGameLight) is { } light)
			{
				bridge.SeedGameLight(light);
			}
			bridge.SeedModOutput(host.modOutput);
			bridge.GameUseStarted += host.OnGameUse;
			return bridge;
		}

		private ControllerBridge NewBridge() => new(Session, pad)
		{
			LogSlot = host.LogSlot,
			Time = host.time,
			GameLightIdle = host.GameLightIdle,
			AppMuteLed = Bridge?.AppMuteLed ?? ControllerBridge.NoAppMuteLed,
			RumbleFeel = host.profile.Feel,
			LightFromGame = host.profile.LightFromGame,
			TriggerIntensity = host.profile.TriggerIntensity,
			FallbackTrigger = host.profile.FallbackTrigger,
			GameSession = host.profile.GameSession,
			GameHapticsLevel = host.gameHapticsLevel,
			RumbleLevel = host.rumbleLevel,
			InputTransform = host.inputTransform,
			Shortcuts = host.Shortcuts,
			Desktop = host.Desktop,
			GyroMouse = host.GyroMouse,
			ModLightAllowed = host.light.Brightness > 0,
			TriggerRumbleFeel = host.profile.PlayedTriggerRumble,
			TriggerRumbleTuningSource = null
		};

		// Detach before attach: games never see two pads, nor one that stopped answering.
		public async Task SwapPadAsync(VirtualPadType type, CancellationToken token)
		{
			if (port is int p)
			{
				await NoticeUnplugAsync().ConfigureAwait(false);
				port = null;
				host.NotifyPadAudio(a => a.PadDetaching(host.Slot));
				try
				{
					await host.attacher.DetachAsync(p).ConfigureAwait(false);
					host.ClearAttached();
				}
				catch (Exception)
				{
					// Already gone; closing the server ends it anyway.
				}
			}
			Bridge.Dispose();
			if (server != null)
			{
				await server.DisposeAsync().ConfigureAwait(false);
				server = null;
			}
			pad.Dispose();
			pad = CreatePad(type);
			Bridge = CreateBridge();
			Bridge.RestoreGameLight();
			host.SyncModOutput(Bridge);
			await AttachAsync(token).ConfigureAwait(false);
		}
	}
}
