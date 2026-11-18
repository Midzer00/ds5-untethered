// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Virtual;

// The levels (percent, 0-100) the game plays on the trigger motors ("impulse triggers") right now.
public readonly record struct TriggerRumble(byte Left, byte Right)
{
	public bool AnyOn => (Left | Right) != 0;
}

// A pad whose games can drive trigger motors (the experimental Xbox One pad). The bridge plays the levels on the
// DualSense's adaptive triggers (ControllerBridge.PlayTriggerRumble). Raised whenever a level changes: by a game's
// command, when the command's time is over, or when the host clears the motors. Raised in order, one at a time.
public interface ITriggerRumbleSource
{
	event Action<TriggerRumble>? TriggerRumbleChanged;
}

// A pad that reports its motors only when their levels change (the Xbox Series pad). When the bridge stops a rumble the
// game left on (hold limit, game exit, host release, bridge stop), the pad forgets those levels without raising anything,
// so the game's next command for them plays again even if it repeats the last one.
public interface IChangeOnlyMotors
{
	void ForgetMotors(bool main, bool triggers);
}
