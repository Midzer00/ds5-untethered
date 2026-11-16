// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.ViewModels;

// Named properties a view model computes from shared state: Refresh reads them all and raises PropertyChanged only for
// the ones whose value differs from the last refresh (instead of one whole-object refresh on every state change).
public sealed class ShownProperties
{
	private readonly Action<string> notify;
	private readonly (string Name, Func<object?> Read)[] properties;
	private readonly object?[] last;

	public ShownProperties(Action<string> notify, params (string Name, Func<object?> Read)[] properties)
	{
		this.notify = notify;
		this.properties = properties;
		last = new object?[properties.Length];
		for (int i = 0; i < properties.Length; i++)
		{
			last[i] = properties[i].Read();
		}
	}

	// Returns how many properties changed.
	public int Refresh()
	{
		int changed = 0;
		for (int i = 0; i < properties.Length; i++)
		{
			object? now = properties[i].Read();
			if (!Equals(now, last[i]))
			{
				last[i] = now;
				changed++;
				notify(properties[i].Name);
			}
		}
		return changed;
	}
}
