// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.ViewModels;
using Kopru.Core.Setup;
using Xunit;

namespace Kopru.App.Tests;

public sealed class SetupStepperTests
{
	private static SetupStep Step(StepStatus status) =>
		new(SetupStepId.Drivers, status, _ => "t", _ => "i", [], []);

	private static IReadOnlyList<SetupStepItem> Build(params StepStatus[] statuses) =>
		SetupViewModel.BuildItems(statuses.Select(Step).ToList(), _ => []);

	[Fact]
	public void NumbersAreSequentialAndOnlyTheLastHasNoConnector()
	{
		var items = Build(StepStatus.Done, StepStatus.Needed, StepStatus.Needed, StepStatus.Done);
		Assert.Equal([1, 2, 3, 4], items.Select(i => i.Number));
		Assert.Equal([false, false, false, true], items.Select(i => i.IsLast));
		Assert.False(items[0].HasConnectorAbove);
		Assert.True(items[1].HasConnectorAbove);
	}

	[Fact]
	public void CurrentIsTheFirstPendingStep()
	{
		var items = Build(StepStatus.Done, StepStatus.Warning, StepStatus.Needed, StepStatus.Needed);
		Assert.Single(items, i => i.IsCurrent);
		Assert.True(items[2].IsCurrent);
		Assert.True(items[3].ShowsNumber);
		Assert.True(items[1].ShowsNumber);
		Assert.False(items[0].ShowsNumber);
	}

	[Fact]
	public void NoCurrentStepWhenAllDone()
	{
		var items = Build(StepStatus.Done, StepStatus.Done);
		Assert.DoesNotContain(items, i => i.IsCurrent);
		Assert.All(items, i => Assert.True(i.IsDone));
	}
}
