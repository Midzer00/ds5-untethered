// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Threading;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One choice of a picker or a segmented switch, the base of every option type. Its name is read in the current language
// each time it is shown, and on a language switch the choice tells its views to read it again. So an option list is
// built once and stays the same objects across a language switch: a picker keeps its selection (a new list would make
// it drop the selected item, and the view models ignore that cleared selection) and only the shown names change.
// Choices hold their language subscription weakly, so short-lived lists (a game row's pickers) are not kept alive.
public class LabelledChoice : INotifyPropertyChanged
{
	private static readonly List<WeakReference<LabelledChoice>> Live = [];
	private static int _pruneAt = 64;
	private readonly Func<string> _name;

	static LabelledChoice()
	{
		Localizer.LanguageChanged += _ =>
		{
			if (Dispatcher.UIThread.CheckAccess())
			{
				NotifyAll();
			}
			else
			{
				Dispatcher.UIThread.Post(NotifyAll);
			}
		};
	}

	public LabelledChoice(Func<string> name)
	{
		_name = name;
		lock (Live)
		{
			if (Live.Count >= _pruneAt)
			{
				Live.RemoveAll(w => !w.TryGetTarget(out _));
				_pruneAt = Math.Max(64, Live.Count * 2);
			}
			Live.Add(new WeakReference<LabelledChoice>(this));
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Name => _name();

	public override string ToString() => Name;

	// Every property again (the name and anything else worded in the language, e.g. a tooltip line).
	public void Refresh() => OnPropertyChanged(string.Empty);

	protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

	private static void NotifyAll()
	{
		List<LabelledChoice> alive = [];
		lock (Live)
		{
			Live.RemoveAll(w => !w.TryGetTarget(out _));
			foreach (WeakReference<LabelledChoice> w in Live)
			{
				if (w.TryGetTarget(out LabelledChoice? c))
				{
					alive.Add(c);
				}
			}
		}
		foreach (LabelledChoice c in alive)
		{
			c.Refresh();
		}
	}
}
