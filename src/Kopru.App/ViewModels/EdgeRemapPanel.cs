// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The DualSense Edge part of the remap tab: back paddle targets and "Present as Edge". Visible only while an Edge is
// connected. A paddle can keep its own function (only while presented as an Edge) or send any DualSense button.
public sealed class EdgeRemapPanel : ViewModelBase
{
	private readonly IControllerService _service;
	private readonly Func<IReadOnlyList<ButtonOption>> _standardTargets;
	private readonly Dictionary<DsButton, IReadOnlyList<ButtonOption>> _targets = [];

	public EdgeRemapPanel(IControllerService service, SettingsState settings, Func<IReadOnlyList<ButtonOption>> standardTargets)
	{
		_service = service;
		_standardTargets = standardTargets;
		Settings = settings;
		BuildTargets();
		Rows = ButtonRemap.Paddles.Select(b => new RemapRow(settings, b, () => _targets[b])).ToList();
		service.StateChanged += (_, _) => RunOnUi(() => OnPropertyChanged(nameof(IsVisible)));
	}

	public SettingsState Settings { get; }

	public IReadOnlyList<RemapRow> Rows { get; }

	public bool IsVisible => _service.Kind == ControllerKind.DualSenseEdge;

	public bool PresentAsEdge
	{
		get => Settings.Remap.PresentAsEdge;
		set
		{
			if (value != Settings.Remap.PresentAsEdge)
			{
				Settings.Remap = Settings.Remap with { PresentAsEdge = value };
			}
		}
	}

	public string Title => Text.EdgePaddlesTitle();
	public string Hint => Text.EdgePaddlesHint();
	public string PresentAsEdgeLabel => Text.PresentAsEdge();
	public string PresentAsEdgeHint => Text.PresentAsEdgeHint();

	// After a remap or language change: the "keep" option comes and goes with PresentAsEdge.
	public void Refresh()
	{
		BuildTargets();
		foreach (RemapRow row in Rows)
		{
			row.Refresh();
		}
		OnPropertyChanged(string.Empty);
	}

	private void BuildTargets()
	{
		foreach (DsButton paddle in ButtonRemap.Paddles)
		{
			IReadOnlyList<ButtonOption> standard = _standardTargets();
			_targets[paddle] = Settings.Remap.PresentAsEdge ? [new ButtonOption(paddle, () => Text.EdgeKeepPaddle()), .. standard] : standard;
		}
	}
}
