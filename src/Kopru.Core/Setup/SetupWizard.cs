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
	RunAdapterTest,
	// Copies Target (shell commands) to the clipboard; the page does the copying.
	CopyText
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

// First-run setup model: steps derived from a system check snapshot (LinuxSystemCheck) plus the user's choices.
public sealed class SetupWizard(Func<SystemStatus> check, IUrlLauncher launcher)
{
	private SystemStatus? _status;

	public bool LanguageChosen { get; set; }

	// Result of the adapter test step (run elsewhere by AdapterCheck); null = not run yet.
	public AdapterVerdict? AdapterVerdict { get; set; }

	public SystemStatus Status => _status ??= check();

	public SystemStatus Recheck() => _status = check();

	public IReadOnlyList<SetupStep> Steps => Build(Status);

	// The first required step still to do; optional steps (the adapter test) never count.
	public SetupStep? FirstIncomplete => Steps.FirstOrDefault(s => s.Id != SetupStepId.Done && !s.IsOptional && s.Status == StepStatus.Needed);

	// Finishing needs a language, both drivers and a controller on Bluetooth; the adapter test is optional and
	// warnings (restart pending, borderline adapter) do not block.
	public bool CanFinish => FirstIncomplete == null;

	// Runs an action; returns true when the caller must start the adapter test itself (or copy CopyText's target).
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

		return BuildLinux(s, s.Requirements ?? [], language, recheck);
	}

	private SetupStep Adapter(bool paired)
	{
		StepStatus adapterStatus = AdapterVerdict switch
		{
			null => StepStatus.Needed,
			Diagnostics.AdapterVerdict.Suitable => StepStatus.Done,
			_ => StepStatus.Warning
		};
		return new SetupStep(SetupStepId.AdapterTest, adapterStatus, Text.SetupAdapterTitle, Text.SetupAdapterInstructions,
			[], paired ? [new SetupAction(SetupActionKind.RunAdapterTest, Text.SetupActionRunAdapterTest)] : [], IsOptional: true);
	}

	// Linux: the system step lists what is missing (with "Copy Commands" for the shell fixes) and always says that the real
	// controller is not hidden yet; pairing happens in the desktop's own Bluetooth settings.
	private IReadOnlyList<SetupStep> BuildLinux(SystemStatus s, IReadOnlyList<SetupRequirement> requirements, SetupStep language, SetupAction recheck)
	{
		List<SetupRequirement> missing = [.. requirements.Where(r => !r.Ready)];
		var notes = new List<Func<Language?, string>>(missing.Select(r => r.Problem)) { Text.SetupLinuxHidingNote };
		var actions = new List<SetupAction>();
		string commands = string.Join(Environment.NewLine, missing.Select(r => r.FixCommands).Where(c => !string.IsNullOrEmpty(c)));
		if (commands.Length > 0)
		{
			actions.Add(new SetupAction(SetupActionKind.CopyText, Text.SetupActionCopyCommands, commands));
		}
		actions.Add(recheck);
		var system = new SetupStep(SetupStepId.Drivers, missing.Count > 0 ? StepStatus.Needed : StepStatus.Done, Text.SetupLinuxTitle,
			Text.SetupLinuxInstructions, notes, actions);

		var pairNotes = new List<Func<Language?, string>>();
		if (s.Radio == RadioState.Missing)
		{
			pairNotes.Add(Text.SetupRadioMissing);
		}
		else if (s.Radio == RadioState.Off)
		{
			pairNotes.Add(Text.SetupLinuxRadioOff);
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
		bool paired = s.Radio != RadioState.Missing && s.ControllerOnBluetooth;
		var pair = new SetupStep(SetupStepId.PairController, paired ? StepStatus.Done : StepStatus.Needed,
			Text.SetupPairTitle, Text.SetupLinuxPairInstructions, pairNotes, [recheck]);
		return Finish(language, system, pair, Adapter(paired));
	}

	private static IReadOnlyList<SetupStep> Finish(SetupStep language, SetupStep drivers, SetupStep pair, SetupStep adapter)
	{
		var steps = new List<SetupStep> { language, drivers, pair, adapter };
		bool finish = steps.All(x => x.IsOptional || x.Status != StepStatus.Needed);
		steps.Add(new SetupStep(SetupStepId.Done, finish ? StepStatus.Done : StepStatus.Needed,
			Text.SetupDoneTitle, finish ? Text.SetupDoneInstructions : Text.SetupNotDoneInstructions, [], []));
		return steps;
	}
}
