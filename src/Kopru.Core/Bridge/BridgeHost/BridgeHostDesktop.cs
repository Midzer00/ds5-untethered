// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Bridge;

// Desktop mode and gyro to mouse: both send mouse/keyboard input through InputSink (nothing until the app sets one).
public sealed partial class BridgeHost
{
	public DesktopMode Desktop { get; } = new();

	public GyroMouse GyroMouse { get; } = new();

	public IInputSink InputSink
	{
		get => Desktop.Sink;
		set
		{
			Desktop.Sink = value;
			GyroMouse.Sink = value;
		}
	}
}
