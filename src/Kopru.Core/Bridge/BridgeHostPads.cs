// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// The virtual controller type games see (DualSense by default, Xbox 360 for games chosen by the user). A change while
// running swaps only the virtual pad: the old one is unplugged first, then the new one is plugged in; the Bluetooth
// controller stays connected.
public sealed partial class BridgeHost
{
	private volatile VirtualPadType padType = VirtualPadType.DualSense;

	// The type games get now or on the next connection.
	public VirtualPadType PadType => padType;

	// Raised after a running bridge swapped the virtual pad (not on a fresh connection).
	public event Action<VirtualPadType>? PadTypeChanged;

	// GameModeController calls this when the game in front changes.
	public void UsePad(VirtualPadType type) => padType = type;

	private async Task SwapPadIfNeededAsync(Connection connection, CancellationToken token)
	{
		VirtualPadType wanted = padType;
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

		public VirtualPadType PadType => pad.Type;

		private IVirtualPad CreatePad(VirtualPadType type) => type switch
		{
			VirtualPadType.Xbox360 => new VirtualXbox360(host.BusId),
			VirtualPadType.DualShock4 => new VirtualDualShock4(host.BusId, identity.FeatureReports.GetValueOrDefault(CalibrationOverlay.Feature)),
			_ => new VirtualDualSense(identity, host.BusId)
		};

		private ControllerBridge CreateBridge() => new(Session, pad)
		{
			RumbleFeel = host.profile.Feel,
			LightFromGame = host.profile.LightFromGame,
			TriggerIntensity = host.profile.TriggerIntensity,
			FallbackTrigger = host.profile.FallbackTrigger,
			GameHapticsLevel = host.gameHapticsLevel,
			RumbleLevel = host.rumbleLevel,
			InputTransform = host.inputTransform,
			Shortcuts = host.Shortcuts,
			Desktop = host.Desktop,
			GyroMouse = host.GyroMouse
		};

		// Detach before attach: games never see two pads, nor one that stopped answering.
		public async Task SwapPadAsync(VirtualPadType type, CancellationToken token)
		{
			if (port is int p)
			{
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
			await AttachAsync(token).ConfigureAwait(false);
		}
	}
}
