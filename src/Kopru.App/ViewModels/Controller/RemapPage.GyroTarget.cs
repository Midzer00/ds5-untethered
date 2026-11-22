// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

public sealed class GyroTargetOption(GyroTarget target) : LabelledChoice(() => Text.GyroTargetName(target))
{
	public GyroTarget Target { get; } = target;
}

// Where gyro aiming goes: the right stick or the mouse pointer (GyroMouse).
public sealed partial class RemapViewModel
{
	public IReadOnlyList<GyroTargetOption> GyroTargets { get; } = [new(GyroTarget.RightStick), new(GyroTarget.Mouse)];

	public string GyroTargetLabel => Text.GyroTargetLabel();
	public string GyroTargetHint => Text.GyroTargetHint();

	public GyroTargetOption? SelectedGyroTarget
	{
		get => GyroTargets.FirstOrDefault(t => t.Target == Settings.Gyro.Target);
		set
		{
			if (value != null && value.Target != Settings.Gyro.Target)
			{
				SetGyro(g => g with { Target = value.Target });
			}
		}
	}
}
