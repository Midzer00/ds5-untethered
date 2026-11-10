// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;

namespace Kopru.Core.Setup;

public enum SetupStepId
{
	Language,
	Drivers,
	PairController,
	AdapterTest,
	Done
}

public enum StepStatus
{
	Done,
	Needed,
	Warning
}

public enum SetupActionKind
{
	OpenLink,
	Recheck,
	RunAdapterTest
}

// A button on a step. Target is the URL / settings URI for OpenLink, null otherwise.
public sealed record SetupAction(SetupActionKind Kind, Func<Language?, string> Label, string? Target = null);

// One step as shown to the user. Notes are state-dependent hints (e.g. "Bluetooth is off").
public sealed record SetupStep(
	SetupStepId Id,
	StepStatus Status,
	Func<Language?, string> Title,
	Func<Language?, string> Instructions,
	IReadOnlyList<Func<Language?, string>> Notes,
	IReadOnlyList<SetupAction> Actions,
	bool IsOptional = false);

// First-run setup model: steps derived from a SystemCheck snapshot plus the user's choices.
public sealed class SetupWizard(SystemCheck check, IUrlLauncher launcher)
{
	private SystemStatus? _status;

	public bool LanguageChosen { get; set; }

	// Result of the adapter test step (run elsewhere by AdapterCheck); null = not run yet.
	public AdapterVerdict? AdapterVerdict { get; set; }

	public SystemStatus Status => _status ??= check.Run();

	public SystemStatus Recheck() => _status = check.Run();

	public IReadOnlyList<SetupStep> Steps => Build(Status);

	// The first required step still to do; optional steps (the adapter test) never count.
	public SetupStep? FirstIncomplete => Steps.FirstOrDefault(s => s.Id != SetupStepId.Done && !s.IsOptional && s.Status == StepStatus.Needed);

	// Finishing needs a language, both drivers and a controller on Bluetooth; the adapter test is optional and
	// warnings (restart pending, borderline adapter) do not block.
	public bool CanFinish => FirstIncomplete == null;

	// Runs an action; returns true when the caller must start the adapter test itself.
	public bool Execute(SetupAction action)
	{
		switch (action.Kind)
		{
			case SetupActionKind.OpenLink:
				launcher.Open(action.Target ?? throw new ArgumentException("Link action without target.", nameof(action)));
				return false;
			case SetupActionKind.Recheck:
				Recheck();
				return false;
			default:
				return true;
		}
	}

	private IReadOnlyList<SetupStep> Build(SystemStatus s)
	{
		var recheck = new SetupAction(SetupActionKind.Recheck, Text.SetupActionRecheck);

		var language = new SetupStep(SetupStepId.Language, LanguageChosen ? StepStatus.Done : StepStatus.Needed,
			Text.SetupLanguageTitle, Text.SetupLanguageInstructions, [], []);

		var driverNotes = new List<Func<Language?, string>>();
		var driverActions = new List<SetupAction>();
		if (!s.UsbIp.Ready)
		{
			driverNotes.Add(Text.SetupUsbIpMissing);
			driverActions.Add(new SetupAction(SetupActionKind.OpenLink, Text.SetupActionDownloadUsbIp, SetupLinks.UsbIpReleases));
		}
		if (!s.HidHide.Ready)
		{
			driverNotes.Add(Text.SetupHidHideMissing);
			driverActions.Add(new SetupAction(SetupActionKind.OpenLink, Text.SetupActionDownloadHidHide, SetupLinks.HidHideReleases));
		}
		bool driversOk = s.UsbIp.Ready && s.HidHide.Ready;
		if (s.RestartPending == true)
		{
			driverNotes.Add(Text.SetupRestartPending);
		}
		driverActions.Add(recheck);
		StepStatus driverStatus = !driversOk ? StepStatus.Needed : s.RestartPending == true ? StepStatus.Warning : StepStatus.Done;
		var drivers = new SetupStep(SetupStepId.Drivers, driverStatus, Text.SetupDriversTitle, Text.SetupDriversInstructions, driverNotes, driverActions);

		var pairNotes = new List<Func<Language?, string>>();
		if (s.Radio == RadioState.Missing)
		{
			pairNotes.Add(Text.SetupRadioMissing);
		}
		else if (s.Radio == RadioState.Off)
		{
			pairNotes.Add(Text.SetupRadioOff);
		}
		if (s.ControllerOnUsbOnly)
		{
			pairNotes.Add(Text.SetupControllerOnUsb);
		}
		else if (!s.ControllerOnBluetooth)
		{
			pairNotes.Add(Text.SetupControllerMissing);
		}
		if (s.EdgeOnBluetooth)
		{
			pairNotes.Add(Text.SetupEdgeConnected);
		}
		bool paired = s.Radio == RadioState.On && s.ControllerOnBluetooth;
		var pair = new SetupStep(SetupStepId.PairController, paired ? StepStatus.Done : StepStatus.Needed,
			Text.SetupPairTitle, Text.SetupPairInstructions, pairNotes,
			[new SetupAction(SetupActionKind.OpenLink, Text.SetupActionOpenBluetooth, SetupLinks.BluetoothSettings), recheck]);

		StepStatus adapterStatus = AdapterVerdict switch
		{
			null => StepStatus.Needed,
			Diagnostics.AdapterVerdict.Suitable => StepStatus.Done,
			_ => StepStatus.Warning
		};
		var adapterNotes = AdapterVerdict is Diagnostics.AdapterVerdict.Borderline or Diagnostics.AdapterVerdict.Unsuitable
			? new List<Func<Language?, string>> { Text.SetupAdapterWeak }
			: [];
		var adapter = new SetupStep(SetupStepId.AdapterTest, adapterStatus, Text.SetupAdapterTitle, Text.SetupAdapterInstructions,
			adapterNotes, paired ? [new SetupAction(SetupActionKind.RunAdapterTest, Text.SetupActionRunAdapterTest)] : [], IsOptional: true);

		var steps = new List<SetupStep> { language, drivers, pair, adapter };
		bool finish = steps.All(x => x.IsOptional || x.Status != StepStatus.Needed);
		steps.Add(new SetupStep(SetupStepId.Done, finish ? StepStatus.Done : StepStatus.Needed,
			Text.SetupDoneTitle, finish ? Text.SetupDoneInstructions : Text.SetupNotDoneInstructions, [], []));
		return steps;
	}
}
