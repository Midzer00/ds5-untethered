// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

public sealed class GyroTargetOption(GyroTarget target)
{
	public GyroTarget Target { get; } = target;
	public string Name => Text.GyroTargetName(Target);
}

// Where gyro aiming goes: the right stick or the mouse pointer (GyroMouse).
public sealed partial class RemapViewModel
{
	private IReadOnlyList<GyroTargetOption>? _gyroTargets;

	public IReadOnlyList<GyroTargetOption> GyroTargets
	{
		get => _gyroTargets ??= BuildGyroTargets();
		private set => _gyroTargets = value;
	}

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

	private static IReadOnlyList<GyroTargetOption> BuildGyroTargets() => [new(GyroTarget.RightStick), new(GyroTarget.Mouse)];
}
