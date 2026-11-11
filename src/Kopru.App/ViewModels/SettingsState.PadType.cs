// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.ViewModels;

// The global controller type ("Kumanda Türü"): one instance for every page that shows it.
public sealed partial class SettingsState
{
	private PadTypeChoiceViewModel? _padType;

	public PadTypeChoiceViewModel PadType => _padType ??= new PadTypeChoiceViewModel(_service.Games);
}
