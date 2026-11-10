// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Kopru.App.Views;

// Shows the current page's view and keeps the views of the cached pages (the sidebar pages) after they are left, hidden,
// so going back to a page does not build and lay out its whole visual tree again. Other pages (the setup page, created
// anew each time) are dropped once left. Views come from the application's data templates, like a ContentControl.
public sealed class PageHost : Panel
{
	public static readonly StyledProperty<object?> PageProperty = AvaloniaProperty.Register<PageHost, object?>(nameof(Page));

	public static readonly StyledProperty<IEnumerable?> CachedPagesProperty = AvaloniaProperty.Register<PageHost, IEnumerable?>(nameof(CachedPages));

	private readonly Dictionary<object, Control> _views = new(ReferenceEqualityComparer.Instance);

	public object? Page
	{
		get => GetValue(PageProperty);
		set => SetValue(PageProperty, value);
	}

	// The pages whose views are kept while hidden.
	public IEnumerable? CachedPages
	{
		get => GetValue(CachedPagesProperty);
		set => SetValue(CachedPagesProperty, value);
	}

	// How many page views were built (tests: a page shown again reuses its view).
	public int ViewsBuilt { get; private set; }

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == PageProperty)
		{
			Show(change.GetNewValue<object?>());
		}
	}

	private bool IsCached(object page) => CachedPages?.Cast<object>().Contains(page, ReferenceEqualityComparer.Instance) == true;

	private void Show(object? page)
	{
		Control? view = null;
		if (page != null && !_views.TryGetValue(page, out view))
		{
			view = this.FindDataTemplate(page)?.Build(page) ?? new TextBlock { Text = page.ToString() };
			view.DataContext = page;
			ViewsBuilt++;
			_views[page] = view;
			Children.Add(view);
		}
		foreach ((object key, Control other) in _views.ToList())
		{
			if (other == view)
			{
				continue;
			}
			if (IsCached(key))
			{
				other.IsVisible = false;
			}
			else
			{
				_views.Remove(key);
				Children.Remove(other);
			}
		}
		if (view != null)
		{
			view.IsVisible = true;
		}
	}
}
