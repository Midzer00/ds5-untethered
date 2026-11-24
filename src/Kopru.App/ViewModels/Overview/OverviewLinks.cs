// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Controls;
using Kopru.Core.Health;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The Overview's connection line (hero and sidebar card), the live card's link notice, and the live card's panel while
// there are no live numbers: the chain controller -> virtual controller -> game with a title and one paragraph.
public sealed partial class OverviewViewModel
{
	// What each link does for the controller shown: the one in use is drawn in the accent, a connected idle one in grey.
	public LinkUse BluetoothUse => IsOnCable || !_service.IsConnected ? LinkUse.Absent
		: _service.IsWired || !_service.IsBridgeRunning ? LinkUse.Standby
		: LinkUse.InUse;

	public LinkUse UsbUse => IsOnCable ? LinkUse.Direct
		: !_service.IsConnected ? LinkUse.Absent
		: _service.IsWired ? LinkUse.InUse
		: _service.CablePluggedIn ? LinkUse.ChargeOnly
		: LinkUse.Absent;

	// Hero: the link line under the drawing while a controller is connected ("Bluetooth İle Bağlı", "Bluetooth · Kablo
	// Takılı", "Kablo Üzerinden Çalışıyor" with "Deneysel"; "Sanal Kumanda Kapalı" while it is off).
	public bool ShowsHeroLink => _service.IsConnected && !IsOnCable;
	public string HeroLinkText => !_service.IsBridgeRunning ? Text.BridgeStateStopped()
		: _service.IsWired ? Text.HeroLinkWired()
		: _service.CablePluggedIn ? Text.ConnectionBluetoothCable()
		: Text.HeroLinkBluetooth();
	public string HeroLinkTag => Text.ExperimentalTag();
	public bool ShowsHeroLinkTag => _service.IsWired && _service.IsBridgeRunning;

	// The link notice: the sentence after its title.
	public string LinkNoticeRest => " · " + LinkAdvice;

	// Sidebar card: the link's name ("Bluetooth", "Bluetooth · Kablo Takılı", "USB Kablosu (Deneysel)", "Kabloyla Bağlı",
	// "Bağlı Değil").
	public string SidebarLinkText => IsOnCable ? Text.ConnectionOnUsb()
		: !_service.IsConnected ? Text.DeviceNotConnected()
		: ConnectionLabel;

	// Header: Turn Off Controller (with a controller), out of the way while Close Game asks whether to end the game.
	public bool ShowsPowerOff => _service.IsConnected && BackToGame?.ConfirmsForceClose != true;

	// Hero: under "Oyun Şunu Görüyor" what the type lacks, or a type held back for the running game.
	public bool ShowsHeroPadLoss => ShowsPadStatus && ShowsPadLoss;
	public bool ShowsHeroPendingPadType => ShowsPadStatus && HasPendingPadType;

	// On the cable there is no verdict: a cable tile and "Kablolu" in the gauge's place.
	public bool IsLinkWired => _service.IsWired;

	// Live card: a short notice on top while the link is fair or weak (the verdict's colour on its icon only).
	public bool ShowsLinkNotice => IsLive && HasLinkAdvice;
	public string LinkNoticeTitle => IsLinkPoor ? Text.NotifyLinkTitle() : Text.LinkNoticeFair();

	// Live card while not live: which state, its chain, title and paragraph.
	private enum IdleState { None, NotConnected, OtherNoController, Waiting, VirtualOff, CableOnly }

	private IdleState Idle => IsLive ? IdleState.None
		: IsOnCable ? IdleState.CableOnly
		: _service.IsConnected ? IdleState.VirtualOff
		: _service.IsBridgeRunning ? IdleState.Waiting
		: Connection.Kind == ConnectionKind.PairedNotConnected ? IdleState.NotConnected
		: IdleState.OtherNoController;

	public string StateHeading => Idle switch
	{
		IdleState.NotConnected => Text.StateTurnOnHeading(),
		IdleState.Waiting => Text.OverviewWaitingController(),
		IdleState.VirtualOff => Text.BridgeStateStopped(),
		IdleState.CableOnly => Text.StatePausedHeading(),
		IdleState.OtherNoController => Connection.Label(),
		_ => string.Empty
	};

	public string StateText => Idle switch
	{
		IdleState.NotConnected => Text.StateNotConnectedText(),
		IdleState.Waiting => Text.StateWaitingText(),
		IdleState.VirtualOff => Text.StateVirtualOffText(),
		IdleState.CableOnly => Text.StateCableOnlyText(),
		IdleState.OtherNoController => Text.LiveIdle(),
		_ => string.Empty
	};

	public ChainSpec? StateChain => Idle switch
	{
		IdleState.NotConnected => new(
			new(IconPaths.PsButton, Text.CardController(), Text.ChainPressPs(), NodeLook.On), new(LinkLook.Chevron),
			new(IconPaths.Bluetooth, "Bluetooth", Text.ChainConnects(), NodeLook.On), new(LinkLook.Chevron),
			new(IconPaths.Bridge, Text.RowVirtualPad(), Text.ChainStarts(), NodeLook.On)),
		IdleState.Waiting => new(
			Pad(Text.ChainWaiting(), NodeLook.Off), Radio(LinkLook.Dashed),
			Virtual(Text.HealthOn(), NodeLook.On), new(LinkLook.Dashed),
			Game(null, NodeLook.Off)),
		IdleState.VirtualOff => new(
			Pad(Text.ChainConnected(), NodeLook.On), Radio(LinkLook.Quiet),
			Virtual(Text.HealthOff(), NodeLook.Off), new(LinkLook.Dashed),
			Game(null, NodeLook.Off)),
		IdleState.CableOnly => new(
			Pad(Text.ConnectionOnUsb(), NodeLook.On), Radio(LinkLook.Dashed),
			Virtual(Text.ChainPaused(), NodeLook.Off), new(LinkLook.Dashed),
			Game(Text.ChainSeesController(), NodeLook.On),
			CableCaption: Text.ChainUsbCable()),
		IdleState.OtherNoController => new(
			Pad(null, NodeLook.Off), Radio(LinkLook.Dashed),
			Virtual(_service.IsBridgeRunning ? Text.HealthOn() : Text.HealthOff(), _service.IsBridgeRunning ? NodeLook.On : NodeLook.Off), new(LinkLook.Dashed),
			Game(null, NodeLook.Off)),
		_ => null
	};

	private static ChainNode Pad(string? state, NodeLook look) => new(IconPaths.Gamepad, Text.CardController(), state, look);
	private static ChainNode Virtual(string state, NodeLook look) => new(IconPaths.Bridge, Text.RowVirtualPad(), state, look);
	private static ChainNode Game(string? state, NodeLook look) => new(IconPaths.Screen, Text.HeroGame(), state, look);
	private static ChainLink Radio(LinkLook look) => new(look, IconPaths.Bluetooth);
}
