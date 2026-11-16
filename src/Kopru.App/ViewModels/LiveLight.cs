// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Lighting;

namespace Kopru.App.ViewModels;

// The light the selected controller shows right now, for the controller drawings (sidebar card, Overview, controller
// test): the colour, player LEDs and their brightness last written to it (IControllerService.ControllerLight), whoever
// set them (the app's light and its effects, the game, Low Power, another controller picked). With no controller open
// the strip is off (the drawing's neutral channel) and the LEDs are dark.
//
// Refresh: while the window is shown, a UI timer reads the snapshot (a field read) every FastInterval for a second after
// it last changed (breathing and rainbow follow the controller frame for frame) and every SlowInterval otherwise; the
// drawings are told only when the snapshot's change counter moves. Hidden: no timer.
public sealed partial class LiveLight : ObservableObject
{
	public static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(33);
	public static readonly TimeSpan SlowInterval = TimeSpan.FromMilliseconds(250);
	private static readonly TimeSpan StayFast = TimeSpan.FromSeconds(1);

	private readonly IControllerService _service;
	private DispatcherTimer? _timer;
	private LightOutput? _last;
	private bool _known;
	private DateTime _changedAt;

	public LiveLight(IControllerService service)
	{
		_service = service;
		Apply(null);
	}

	// The light bar colour as sent (its brightness included, so the drawing uses LightIntensity 1).
	[ObservableProperty]
	private Color _color;

	[ObservableProperty]
	private int _playerLedMask;

	[ObservableProperty]
	private double _playerLedLevel = 1;

	// Changes pushed to the drawings so far (tests: none while the snapshot is unchanged).
	public int Updates { get; private set; }

	public double Intensity => 1;

	// Reads the snapshot; true when the drawings were told about a change.
	public bool Refresh()
	{
		LightOutput? now = _service.IsConnected ? _service.ControllerLight : null;
		if (_known && (ReferenceEquals(now, _last) || Equals(now, _last)))
		{
			return false;
		}
		_known = true;
		_last = now;
		Apply(now);
		Updates++;
		_changedAt = DateTime.UtcNow;
		return true;
	}

	// Polls while the window is on screen; nothing while it is hidden.
	public void SetShown(bool shown)
	{
		if (!shown)
		{
			_timer?.Stop();
			_timer = null;
			return;
		}
		Refresh();
		if (_timer == null)
		{
			_timer = new DispatcherTimer(SlowInterval, DispatcherPriority.Background, (_, _) => Tick());
			_timer.Start();
		}
	}

	private void Tick()
	{
		Refresh();
		TimeSpan want = DateTime.UtcNow - _changedAt < StayFast ? FastInterval : SlowInterval;
		if (_timer != null && _timer.Interval != want)
		{
			_timer.Interval = want;
		}
	}

	private void Apply(LightOutput? light)
	{
		Color = light is { } l ? Color.FromRgb(l.R, l.G, l.B) : Colors.Black;
		PlayerLedMask = light?.PlayerLeds ?? 0;
		PlayerLedLevel = light?.PlayerLedLevel ?? 1;
	}
}
