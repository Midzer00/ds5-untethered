// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Input;

namespace Kopru.Core.Bridge;

// Stick and trigger tuning for the input games receive, and a tap on the raw (untuned) input for previews and drift
// calibration.
public sealed partial class BridgeHost
{
	private InputTransform inputTransform = InputTransform.Identity;

	// Applies to the running connection at once and to every later one.
	public InputTransform InputTransform
	{
		get => inputTransform;
		set
		{
			inputTransform = value ?? InputTransform.Identity;
			Connection? c = current;
			if (c != null)
			{
				c.Bridge.InputTransform = inputTransform;
			}
		}
	}

	// Each raw input body (before tuning) while a connection runs; raised on the reader thread, keep handlers short.
	public event InputBodyHandler? RawInput;
}
