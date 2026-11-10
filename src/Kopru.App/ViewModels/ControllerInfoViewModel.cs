// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The "Controller Info" card (controller test page) and the compact line in Settings → About: model, connection,
// firmware/hardware version, build date, masked Bluetooth address, connected for (live) and battery; copy and reconnect.
public sealed partial class ControllerInfoViewModel : ViewModelBase
{
	private readonly IControllerService _service;
	private readonly TimeProvider _time;
	private readonly DispatcherTimer? _timer;
	private string _connectedFor = "—";

	public ControllerInfoViewModel(IControllerService service, TimeProvider? time = null)
	{
		_service = service;
		_time = time ?? TimeProvider.System;
		_service.StateChanged += (_, _) => RunOnUi(Refresh);
		_connectedFor = ConnectedForNow();
		if (Dispatcher.UIThread.CheckAccess())
		{
			_timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
			_timer.Start();
		}
	}

	// Puts text on the clipboard (set by the view; null in tests without a window).
	public Func<string, Task>? Clipboard { get; set; }

	// The last text copied (tests read it).
	public string? LastCopied { get; private set; }

	private ControllerDetails? Details => _service.Details;

	public bool HasDetails => Details != null;
	public bool HasNoDetails => !HasDetails;

	public string Title => Text.InfoTitle();
	public string Hint => Text.InfoHint();
	public string NotConnectedText => Text.InfoNotConnected();
	public string ModelLabel => Text.InfoModel();
	public string ConnectionLabel => Text.InfoConnection();
	public string FirmwareLabel => Text.InfoFirmware();
	public string HardwareLabel => Text.InfoHardware();
	public string BuildDateLabel => Text.InfoBuildDate();
	public string AddressLabel => Text.InfoAddress();
	public string ConnectedForLabel => Text.InfoConnectedFor();
	public string BatteryLabel => Text.InfoBattery();
	public string CopyText => Text.InfoCopy();
	public string ReconnectText => Text.ReconnectController();
	public string ReconnectHint => Text.ReconnectHint();

	public string Model => Details?.ModelName ?? (_service.IsConnected ? _service.DeviceName : "—");
	public string Connection => Details?.TransportName ?? (_service.IsConnected ? ControllerDetails.BluetoothName : "—");
	public string Firmware => Details?.Firmware is { } f ? f.FirmwareText : Unknown;
	public string FirmwareRaw => Details?.Firmware?.FirmwareHex ?? string.Empty;
	public string Hardware => Details?.Firmware?.HardwareText ?? Unknown;
	public string BuildDate => Details?.BuildDateText() ?? Unknown;
	public string Address => Details?.Address is null ? Unknown : Details.MaskedAddress;
	public string ConnectedFor => _connectedFor;
	public string Battery => _service.HasBattery
		? _service.IsCharging ? Text.BatteryValue(Math.Round(_service.BatteryPercent)) + " · " + Text.Charging() : Text.BatteryValue(Math.Round(_service.BatteryPercent))
		: Text.BatteryUnknown();

	// Settings → About: one line, or "no controller".
	public string Compact => Details is { } d
		? Text.InfoCompact(d.ModelName, d.TransportName, d.Firmware?.FirmwareText ?? Unknown, _connectedFor)
		: Text.InfoNotConnected();

	public bool CanReconnect => _service.IsConnected || HasDetails;

	[ObservableProperty]
	private string _copyFeedback = string.Empty;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
	private bool _isReconnecting;

	private static string Unknown => Text.InfoUnknown();

	// The clipboard text (address masked).
	public string DetailsText() => Details is { } d
		? d.CopyText(_service.ConnectedSince is { } since ? _time.GetUtcNow() - since : null,
			_service.HasBattery ? new BatteryStatus((int)Math.Round(_service.BatteryPercent), _service.IsCharging ? ChargeState.Charging : ChargeState.Discharging) : null)
		: Text.InfoNotConnected();

	public void Refresh()
	{
		_connectedFor = ConnectedForNow();
		OnPropertyChanged(string.Empty);
		ReconnectCommand.NotifyCanExecuteChanged();
	}

	// Once a second: only the duration changes.
	public void Tick()
	{
		string next = ConnectedForNow();
		if (next != _connectedFor)
		{
			_connectedFor = next;
			OnPropertyChanged(nameof(ConnectedFor));
			OnPropertyChanged(nameof(Compact));
		}
	}

	private string ConnectedForNow() => _service.ConnectedSince is { } since ? Core.Hid.ConnectedFor.Format(_time.GetUtcNow() - since) : "—";

	[RelayCommand]
	private async Task Copy()
	{
		string text = DetailsText();
		LastCopied = text;
		if (Clipboard is { } set)
		{
			try
			{
				await set(text);
			}
			catch (Exception)
			{
				return;
			}
		}
		CopyFeedback = Text.InfoCopied();
	}

	private bool CanRunReconnect() => !IsReconnecting && CanReconnect;

	[RelayCommand(CanExecute = nameof(CanRunReconnect))]
	private async Task Reconnect()
	{
		IsReconnecting = true;
		try
		{
			await _service.ReconnectAsync();
		}
		finally
		{
			IsReconnecting = false;
		}
	}
}
