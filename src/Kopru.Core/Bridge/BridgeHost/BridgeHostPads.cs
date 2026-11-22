// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// The virtual controller type games see (DualSense by default; Xbox Series, Xbox 360 or DualShock 4 as GameModeController
// decides). A change while running swaps only the virtual pad: the old one is unplugged first, then the new one is plugged
// in; the Bluetooth controller stays connected.
//
// Xbox Series: a wired GIP pad (VirtualXboxOne) with the Xbox Series identity 045E:0B12 (the DualSense Create button is
// View, as on the Xbox 360 pad; Share is not mapped), a GIP device id that stays the same for a controller on this install
// (GipDeviceIds, XboxSeriesStore), and a start check: on Windows before 10 21H2, or when Windows does not start the pad (no
// Set Device State START) within XboxSeriesSupport.StartTimeout of plugging it in (FirstStartTimeout for a device id Windows
// never started on this PC; time the PC slept does not count), this host uses Xbox 360 instead and raises XboxSeriesFailed
// (the app decides what to remember); a pad Windows started raises XboxSeriesStarted.
public sealed partial class BridgeHost
{
	private volatile VirtualPadType padType = VirtualPadType.DualSense;

	// This host's Xbox Series pad failed to start: Xbox Series is played as Xbox 360 from now on (this run).
	private volatile bool xboxSeriesFailed;

	// The salt for the device ids and what each controller's last Xbox Series pad did (in memory by default; the app's hosts
	// share XboxSeriesStore.Shared).
	public XboxSeriesStore SeriesStore { get; set; } = new();

	// The clock of the Xbox Series start check: the PC's awake time (AwakeClock), or the test clock given to the constructor.
	public Func<TimeSpan> AwakeTime { get; set; }

	// The type games get now or on the next connection.
	public VirtualPadType PadType => padType;

	// Raised after a running bridge swapped the virtual pad (not on a fresh connection).
	public event Action<VirtualPadType>? PadTypeChanged;

	// Windows did not start the Xbox Series pad in time, or Windows is too old (the reason, for the log). Raised on the
	// host's task.
	public event Action<string>? XboxSeriesFailed;

	// Windows started an Xbox Series pad (Set Device State START). Raised on the host's task.
	public event Action? XboxSeriesStarted;

	// Windows is new enough for the Xbox Series pad (tests set it).
	public bool XboxSeriesOsSupported { get; set; } = XboxSeriesSupport.OsSupported;

	// How long Windows gets to start a plugged-in Xbox Series pad (tests shorten it), and one whose device id it never
	// started on this PC.
	public TimeSpan XboxSeriesStartTimeout { get; set; } = XboxSeriesSupport.StartTimeout;

	public TimeSpan XboxSeriesFirstStartTimeout { get; set; } = XboxSeriesSupport.FirstStartTimeout;

	// GameModeController calls this when the game in front changes.
	public void UsePad(VirtualPadType type) => padType = type;

	// The type the virtual pad is made as: the selected type, with Xbox Series as Xbox 360 where it can't be used.
	private VirtualPadType EffectivePadType =>
		padType == VirtualPadType.XboxSeries && (xboxSeriesFailed || !XboxSeriesOsSupported) ? VirtualPadType.Xbox360 : padType;

	// Once per plugged-in Xbox Series pad: Windows started it, or the time ran out (then the next SwapPadIfNeededAsync puts
	// Xbox 360 in).
	private void CheckXboxSeriesStart(Connection c)
	{
		if (c.XboxSeriesJudged || c.SeriesPad is not { } pad || c.PadAttachedAt is not { } at)
		{
			return;
		}
		TimeSpan waited = AwakeTime() - at;
		if (pad.HostStarted)
		{
			c.XboxSeriesJudged = true;
			c.SeriesStarted();
			Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
				$"virtual Xbox Series pad{LogSlot}: started by Windows within {waited.TotalSeconds:F1} s"));
			XboxSeriesStarted?.Invoke();
			return;
		}
		TimeSpan limit = c.SeriesStartedBefore ? XboxSeriesStartTimeout : XboxSeriesFirstStartTimeout;
		if (waited < limit)
		{
			return;
		}
		c.XboxSeriesJudged = true;
		xboxSeriesFailed = true;
		string reason = string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"Windows did not start the pad within {limit.TotalSeconds:F0} s{(c.SeriesStartedBefore ? string.Empty : " (first start of this device id)")} " +
			$"(state {pad.State}, {pad.Counters.Hellos} Hello, {pad.Counters.MetadataRequests} metadata requests)");
		Diagnostics.AppLog.Write($"virtual Xbox Series pad{LogSlot}: {reason} -> Xbox 360");
		XboxSeriesFailed?.Invoke(reason);
	}

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

		// The Xbox Series pad while games get one.
		public VirtualXboxOne? SeriesPad => pad as VirtualXboxOne;

		// The Xbox Series pad's controller tag (XboxSeriesStore) and device id variant.
		private string? seriesTag;

		private int seriesVariant;

		// Windows started a pad with this controller's device id variant on this PC before.
		public bool SeriesStartedBefore { get; private set; }

		public void SeriesStarted()
		{
			if (seriesTag != null)
			{
				host.SeriesStore.Started(seriesTag, seriesVariant);
			}
		}

		// type: EffectivePadType (Xbox Series only where it can be used).
		private IVirtualPad CreatePad(VirtualPadType type) => type switch
		{
			VirtualPadType.XboxSeries => CreateXboxSeries(),
			VirtualPadType.Xbox360 => new VirtualXbox360(host.BusId),
			VirtualPadType.DualShock4 => new VirtualDualShock4(host.BusId, identity.FeatureReports.GetValueOrDefault(CalibrationOverlay.Feature)),
			_ => new VirtualDualSense(identity, host.BusId)
		};

		// The Xbox Series identity (or the developer override, ExperimentalPads) and this controller's device id. The pad is
		// saved as not ended cleanly until it is unplugged, so after a crash or kill the next one uses the other variant. The
		// log shows the controller's tag, never the device id (the USB serial).
		private VirtualXboxOne CreateXboxSeries()
		{
			GipIdentity identity = ExperimentalPads.XboxOne ?? GipIdentity.XboxSeries;
			XboxSeriesStore store = host.SeriesStore;
			string seed = GipDeviceIds.Seed(ControllerAddress(), link.Device.Path);
			string tag = store.Tag(seed);
			XboxSeriesPadRecord? last = store.Last(tag);
			seriesTag = tag;
			seriesVariant = GipDeviceIds.Variant(last, host.time.GetUtcNow());
			SeriesStartedBefore = last?.HasStarted(seriesVariant) == true;
			store.Attaching(tag, seriesVariant);
			ulong id = store.DeviceId(seed, seriesVariant);
			Diagnostics.AppLog.Write($"virtual Xbox Series pad{host.LogSlot}: {identity}, controller {tag[..6]} variant {seriesVariant}" +
				(last == null ? ", new" : $" (last {last.Variant}, {(last.Clean ? "clean" : last.EndedAt == null ? "never ended" : "unclean")} end)") +
				(SeriesStartedBefore ? string.Empty : ", first start of this device id") +
				(ExperimentalPads.XboxOne != null ? $", identity from {ExperimentalPads.XboxOneVariable}" : string.Empty));
			return new VirtualXboxOne(host.BusId, identity, deviceId: id);
		}

		// The controller's Bluetooth address (the details already read for this link, else feature 0x09), or null. Only the
		// device id's seed (with the install's salt); never logged or saved.
		private ulong? ControllerAddress()
		{
			if (host.Details is { } d && string.Equals(d.Path, link.Device.Path, StringComparison.OrdinalIgnoreCase) && d.Address is ulong known)
			{
				return known;
			}
			try
			{
				return Hid.PairingInfo.ParseAddress(link.GetFeature(Hid.PairingInfo.ReportId));
			}
			catch (Exception)
			{
				return null;
			}
		}

		// Only the Xbox Series pad has something to say before it is unplugged ("powering off"); other pads skip this. Its
		// outcome picks the device id variant of the controller's next Xbox Series pad.
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
			if (pad is VirtualXboxOne series && seriesTag != null)
			{
				host.SeriesStore.Ended(seriesTag, seriesVariant, series.EndedCleanly, host.time.GetUtcNow());
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
			CustomRightTrigger = host.profile.CustomRightTrigger,
			CustomLeftTrigger = host.profile.CustomLeftTrigger,
			GameSession = host.profile.GameSession,
			GameHapticsLevel = host.gameHapticsLevel,
			RumbleLevel = host.rumbleLevel,
			InputTransform = host.inputTransform,
			Shortcuts = host.Shortcuts,
			Desktop = host.Desktop,
			GyroMouse = host.GyroMouse,
			ModLightAllowed = host.light.Brightness > 0,
			TriggerRumbleFeel = host.profile.PlayedTriggerRumble,
			// Developer override only (KOPRU_EXPERIMENTAL_XBOXONE): the live tuning file of the WGI probe's sliders.
			TriggerRumbleTuningSource = pad is ITriggerRumbleSource ? ImpulseTuningFile.Shared : null
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
