// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.ViewModels;

public sealed class SetupActionItem(string label, IAsyncRelayCommand command)
{
	public string Label { get; } = label;
	public IAsyncRelayCommand Command { get; } = command;
}

// One row of the numbered stepper. Number is 1-based; IsCurrent marks the first pending required step; IsLast hides the connector.
public sealed class SetupStepItem(SetupStep step, IReadOnlyList<SetupActionItem> actions, int number = 1, bool isCurrent = false, bool isLast = false,
	string optionalText = "", SetupViewModel? adapterTest = null)
{
	// The page, on the adapter test row only: its progress shows inside the row while the test runs.
	public SetupViewModel? AdapterTest { get; } = step.Id == SetupStepId.AdapterTest ? adapterTest : null;
	// The page, on the drivers row only: the restart button and its inline question show there.
	public SetupViewModel? RestartHost { get; } = step.Id == SetupStepId.Drivers ? adapterTest : null;
	// Optional steps (the adapter test) never block finishing and are never the current step.
	public bool IsOptional => step.IsOptional;
	public string OptionalText { get; } = optionalText;
	public SetupStepId Id => step.Id;
	public StepStatus Status => step.Status;
	public int Number { get; } = number;
	public bool IsCurrent { get; } = isCurrent;
	public bool IsLast { get; } = isLast;
	public bool HasConnector => !IsLast;
	public bool HasConnectorAbove => Number > 1;
	// Outline circle with the number: every step that is neither done nor current.
	public bool ShowsNumber => !IsDone && !IsCurrent;
	public string Title => step.Title(null);
	public string Instructions => step.Instructions(null);
	public string Notes => string.Join(Environment.NewLine, step.Notes.Select(n => n(null)));
	public bool HasNotes => step.Notes.Count > 0;
	public bool IsDone => step.Status == StepStatus.Done;
	public bool IsWarning => step.Status == StepStatus.Warning;
	public bool IsPending => !IsDone && !IsWarning;
	public IReadOnlyList<SetupActionItem> Actions { get; } = actions;
	public bool HasActions => Actions.Count > 0;
}

// One measured value of the adapter test: what was measured, what is needed (muted), and how it did (coloured word).
public sealed class AdapterLineItem(AdapterLine line)
{
	public string Measured { get; } = line.Measured;
	public string Needed { get; } = " · " + line.Needed;
	public string Status { get; } = line.Status;
	public bool IsGood => line.Level == LinkQuality.Good;
	public bool IsWarn => line.Level == LinkQuality.Borderline;
	public bool IsBad => line.Level == LinkQuality.Poor;
}

// Elapsed time for the adapter test progress; tests pass a fake one.
public interface IElapsedClock
{
	TimeSpan Elapsed { get; }
	void Restart();
}

public sealed class StopwatchClock : IElapsedClock
{
	private readonly System.Diagnostics.Stopwatch _watch = new();
	public TimeSpan Elapsed => _watch.Elapsed;
	public void Restart() => _watch.Restart();
}

// First-run / "run setup again" page: the SetupWizard steps with their status, notes and buttons. Closing (or finishing)
// goes to the Overview; on the first run it goes there by itself once the required steps are done.
public sealed partial class SetupViewModel : PageViewModel
{
	private readonly SetupWizard _wizard;
	private readonly IControllerService _service;
	private readonly Action _close;
	private readonly IElapsedClock _clock;
	private readonly Func<CancellationToken, Task> _tick;
	private CancellationTokenSource? _cancel;

	// close: leaves the page for the Overview. autoFinish: go there by itself when the required steps become done
	// (first run). clock / tick: progress timing (tick waits between progress refreshes).
	public SetupViewModel(SetupWizard wizard, IControllerService service, Action close, bool autoFinish = false,
		IElapsedClock? clock = null, Func<CancellationToken, Task>? tick = null, SetupRestart? restart = null)
	{
		_wizard = wizard;
		Restart = restart;
		_service = service;
		_close = close;
		_clock = clock ?? new StopwatchClock();
		_tick = tick ?? (c => Task.Delay(100, c));
		AutoFinish = autoFinish;
		// The language is picked in Settings and follows Windows until then.
		_wizard.LanguageChosen = true;
		_steps = Build();
	}

	public SetupWizard Wizard => _wizard;
	public bool AutoFinish { get; }

	// One phase of the adapter test (idle, then with vibration).
	public TimeSpan PhaseLength { get; init; } = RealControllerService.AdapterPhase;

	// The step to scroll to when the page opens (a health fix), or null.
	public SetupStepId? FocusStep { get; init; }
	public override string Title => Text.SetupPageTitle();
	public string Intro => Text.SetupPageIntro();
	public string CloseText => Text.SetupClose();
	public string OptionalText => Text.SetupOptional();
	public string GoToOverviewText => Text.SetupGoToOverview();
	public string CancelText => Text.AdapterTestCancel();
	public bool HasMessage => !string.IsNullOrEmpty(Message);

	// The required steps are done (the adapter test is optional).
	public bool IsComplete => _wizard.CanFinish;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsComplete))]
	private IReadOnlyList<SetupStepItem> _steps;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasMessage))]
	private string _message = string.Empty;

	[ObservableProperty]
	private bool _isBusy;

	// Adapter test progress: 0-100 over both phases, the phase as text and the seconds left.
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(CancelTestCommand))]
	private bool _isTesting;

	[ObservableProperty]
	private double _testProgress;

	[ObservableProperty]
	private string _testPhase = string.Empty;

	[ObservableProperty]
	private string _testRemaining = string.Empty;

	// "Restart The Computer" on the drivers step while Windows reports a pending restart: asks inline first.
	public SetupRestart? Restart { get; }
	public bool IsRestartPending => Restart != null && _wizard.Status.RestartPending == true;
	// The drivers row's buttons hide while the restart question shows in their place.
	public bool ShowsActions => !IsConfirmingRestart;
	public string RestartText => Text.SetupRestartButton();
	public string RestartQuestion => Text.SetupRestartQuestion();
	public string RestartConfirmText => Text.SetupRestartConfirm();
	public string RestartCancelText => Text.SetupRestartCancel();
	public bool HasRestartError => !string.IsNullOrEmpty(RestartError);

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowsActions))]
	private bool _isConfirmingRestart;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasRestartError))]
	private string _restartError = string.Empty;

	[RelayCommand]
	private void AskRestart()
	{
		RestartError = string.Empty;
		IsConfirmingRestart = IsRestartPending;
	}

	[RelayCommand]
	private void CancelRestart() => IsConfirmingRestart = false;

	[RelayCommand]
	private void ConfirmRestart()
	{
		if (!IsConfirmingRestart || Restart == null)
		{
			return;
		}
		IsConfirmingRestart = false;
		if (!Restart.Run())
		{
			RestartError = Text.SetupRestartFailed();
		}
	}

	partial void OnStepsChanged(IReadOnlyList<SetupStepItem> value)
	{
		OnPropertyChanged(nameof(IsRestartPending));
		if (!IsRestartPending)
		{
			IsConfirmingRestart = false;
		}
	}

	[RelayCommand]
	private void Close() => _close();

	[RelayCommand]
	private void GoToOverview() => _close();

	[RelayCommand(CanExecute = nameof(IsTesting))]
	private void CancelTest() => _cancel?.Cancel();

	// The last adapter test in words (AdapterCheck.Explain, in the current language): shown inside the step, under where
	// the progress was. Null before a test, and for a result without a measurement but with a verdict (then Message).
	private AdapterTestResult? _adapterResult;

	public AdapterExplanation? AdapterExplanation => _adapterResult is { } r && (r.Report != null || r.Verdict == null) ? AdapterCheck.Explain(r.Report) : null;
	public bool HasAdapterResult => !IsTesting && AdapterExplanation != null;
	public string AdapterHeadline => AdapterExplanation?.Headline ?? string.Empty;
	public bool IsAdapterGood => AdapterExplanation?.Verdict == AdapterVerdict.Suitable;
	public bool IsAdapterBorderline => AdapterExplanation?.Verdict == AdapterVerdict.Borderline;
	public bool IsAdapterBad => AdapterExplanation?.Verdict == AdapterVerdict.Unsuitable;
	public IReadOnlyList<AdapterLineItem> AdapterLines => AdapterExplanation?.Lines.Select(l => new AdapterLineItem(l)).ToList() ?? [];
	public IReadOnlyList<string> AdapterAdvice => AdapterExplanation?.Advice ?? [];
	public bool HasAdapterAdvice => AdapterAdvice.Count > 0;

	// Shows a finished test's result (also used by tests and renders).
	public void ShowAdapterResult(AdapterTestResult? result)
	{
		_adapterResult = result;
		_wizard.AdapterVerdict = result?.Verdict;
		Message = result != null && AdapterExplanation == null ? result.Text : string.Empty;
		OnPropertyChanged(nameof(AdapterExplanation));
		OnPropertyChanged(nameof(HasAdapterResult));
		OnPropertyChanged(nameof(AdapterHeadline));
		OnPropertyChanged(nameof(IsAdapterGood));
		OnPropertyChanged(nameof(IsAdapterBorderline));
		OnPropertyChanged(nameof(IsAdapterBad));
		OnPropertyChanged(nameof(AdapterLines));
		OnPropertyChanged(nameof(AdapterAdvice));
		OnPropertyChanged(nameof(HasAdapterAdvice));
		Steps = Build();
	}

	partial void OnIsTestingChanged(bool value) => OnPropertyChanged(nameof(HasAdapterResult));

	partial void OnIsBusyChanged(bool value)
	{
		foreach (SetupActionItem a in Steps.SelectMany(s => s.Actions))
		{
			a.Command.NotifyCanExecuteChanged();
		}
	}

	protected override void OnLanguageChanged()
	{
		Steps = Build();
		if (IsTesting)
		{
			RefreshProgress();
		}
		base.OnLanguageChanged();
	}

	// Sets the progress from the clock: the first phase is idle, the second with vibration; it stops just short of the
	// end until the result arrives (stopping and restarting the bridge takes a moment too).
	public void RefreshProgress()
	{
		TimeSpan total = PhaseLength * 2;
		TimeSpan elapsed = _clock.Elapsed;
		double fraction = total > TimeSpan.Zero ? elapsed / total : 1;
		TestProgress = Math.Round(Math.Clamp(fraction, 0, 0.99) * 100, 1);
		TestPhase = elapsed >= total ? Text.AdapterTestFinishing()
			: elapsed < PhaseLength ? Text.AdapterTestIdlePhase() : Text.AdapterTestLoadPhase();
		int left = (int)Math.Ceiling(Math.Max(0, (total - elapsed).TotalSeconds));
		TestRemaining = left > 0 ? Text.AdapterTestSecondsLeft(left) : string.Empty;
	}

	// The drivers row gets "Restart The Computer" after its own buttons while a restart is pending.
	private IReadOnlyList<SetupStepItem> Build() => BuildItems(_wizard.Steps,
		s => s.Actions.Select(a => new SetupActionItem(a.Label(null), new AsyncRelayCommand(() => RunAsync(a), () => !IsBusy)))
			.Concat(s.Id == SetupStepId.Drivers && IsRestartPending
				? [new SetupActionItem(RestartText, new AsyncRelayCommand(() =>
				{
					AskRestart();
					return Task.CompletedTask;
				}, () => !IsBusy))]
				: Array.Empty<SetupActionItem>())
			.ToList(),
		OptionalText, this);

	// Numbers the steps and marks the current one: the first pending required step, none when nothing is pending.
	public static IReadOnlyList<SetupStepItem> BuildItems(IReadOnlyList<SetupStep> steps, Func<SetupStep, IReadOnlyList<SetupActionItem>> actions,
		string optionalText = "", SetupViewModel? owner = null)
	{
		int current = -1;
		for (int i = 0; i < steps.Count; i++)
		{
			if (!steps[i].IsOptional && steps[i].Status is not (StepStatus.Done or StepStatus.Warning))
			{
				current = i;
				break;
			}
		}
		return steps.Select((s, i) => new SetupStepItem(s, actions(s), i + 1, i == current, i == steps.Count - 1, optionalText, owner)).ToList();
	}

	// Runs a step button; the adapter test reports progress while it runs and can be cancelled.
	public async Task RunAsync(SetupAction action)
	{
		if (IsBusy)
		{
			return;
		}
		bool wasComplete = IsComplete;
		IsBusy = true;
		try
		{
			if (_wizard.Execute(action))
			{
				await RunAdapterTestAsync();
			}
		}
		catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			Message = e.Message;
		}
		finally
		{
			IsBusy = false;
		}
		Steps = Build();
		if (AutoFinish && !wasComplete && IsComplete)
		{
			_close();
		}
	}

	private async Task RunAdapterTestAsync()
	{
		using var cancel = new CancellationTokenSource();
		_cancel = cancel;
		_adapterResult = null;
		Message = string.Empty;
		_clock.Restart();
		RefreshProgress();
		IsTesting = true;
		Task<AdapterTestResult> test = _service.TestAdapterAsync(cancel.Token);
		try
		{
			while (!test.IsCompleted)
			{
				await Task.WhenAny(test, _tick(CancellationToken.None));
				RefreshProgress();
			}
			ShowAdapterResult(await test);
		}
		catch (OperationCanceledException)
		{
			Message = Text.AdapterTestCancelled();
		}
		finally
		{
			IsTesting = false;
			_cancel = null;
		}
	}
}
