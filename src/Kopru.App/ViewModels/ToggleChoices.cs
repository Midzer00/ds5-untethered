// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls.Selection;

namespace Kopru.App.ViewModels;

// One on/off choice of a segmented strip where several may be on (Invert: Horizontal / Vertical). Its short name is the
// segment's label; AccessibleName is the whole setting ("Invert Horizontal") for screen readers and the tooltip. It reads
// and writes its own setting.
public sealed class ToggleChoice(Func<string> name, Func<string> accessibleName, Func<bool> get, Action<bool> set) : LabelledChoice(name)
{
	public string AccessibleName => accessibleName();

	public bool IsOn
	{
		get => get();
		set => set(value);
	}
}

// Independent on/off choices shown as one segmented strip in multi-select mode (ListBox Theme=Segmented,
// SelectionMode="Multiple,Toggle", Selection bound to Selection): a selected segment is a choice that is on. Selecting or
// deselecting a segment writes that choice's setting; Sync shows the settings again after they changed elsewhere.
public sealed class ToggleChoices
{
	private bool _syncing;

	public ToggleChoices(params ToggleChoice[] choices)
	{
		Choices = choices;
		Selection = new SelectionModel<ToggleChoice>(choices) { SingleSelect = false };
		Sync();
		Selection.SelectionChanged += (_, _) =>
		{
			if (_syncing)
			{
				return;
			}
			_syncing = true;
			try
			{
				for (int i = 0; i < Choices.Count; i++)
				{
					bool on = Selection.IsSelected(i);
					if (on != Choices[i].IsOn)
					{
						Choices[i].IsOn = on;
					}
				}
			}
			finally
			{
				_syncing = false;
			}
		};
	}

	public IReadOnlyList<ToggleChoice> Choices { get; }

	public SelectionModel<ToggleChoice> Selection { get; }

	public void Sync()
	{
		if (_syncing)
		{
			return;
		}
		_syncing = true;
		try
		{
			for (int i = 0; i < Choices.Count; i++)
			{
				bool on = Choices[i].IsOn;
				if (on && !Selection.IsSelected(i))
				{
					Selection.Select(i);
				}
				else if (!on && Selection.IsSelected(i))
				{
					Selection.Deselect(i);
				}
			}
		}
		finally
		{
			_syncing = false;
		}
	}
}
