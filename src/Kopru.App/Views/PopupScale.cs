// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Media;

namespace Kopru.App.Views;

// The content scale (UiScale) for popups. Popups open in the window's own overlay layer, outside the scaled content, so
// they are placed and sized against the target's on-screen bounds; the popup host's template then scales its content by
// this transform (App.axaml), so a drop-down or tooltip comes out at the same size as the rest of the window. The main
// window sets it whenever its content scale changes.
public sealed class PopupScale : INotifyPropertyChanged
{
	public static PopupScale Instance { get; } = new();

	private ScaleTransform _transform = new(1, 1);

	public ScaleTransform Transform => _transform;

	public double Factor => _transform.ScaleX;

	public event PropertyChangedEventHandler? PropertyChanged;

	public void Set(double factor)
	{
		if (Math.Abs(factor - _transform.ScaleX) < 0.0001)
		{
			return;
		}
		_transform = new ScaleTransform(factor, factor);
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Transform)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Factor)));
	}
}
