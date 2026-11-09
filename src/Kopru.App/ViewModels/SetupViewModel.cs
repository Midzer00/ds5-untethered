// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.ViewModels;

public sealed class SetupActionItem(string label, IAsyncRelayCommand command)
{
	public string Label { get; } = label;
	public IAsyncRelayCommand Command { get; } = command;
}

public sealed class SetupStepItem(SetupStep step, IReadOnlyList<SetupActionItem> actions)
{
	public SetupStepId Id => step.Id;
	public StepStatus Status => step.Status;
	public string Title => step.Title(null);
	public string Instructions => step.Instructions(null);
	public string Notes => string.Join(Environment.NewLine, step.Notes.Select(n => n(null)));
	public bool HasNotes => step.Notes.Count > 0;
	public string StatusText => step.Status switch
	{
		StepStatus.Done => Text.SetupStatusDone(),
		StepStatus.Warning => Text.SetupStatusWarning(),
		_ => Text.SetupStatusNeeded()
	};
	public bool IsDone => step.Status == StepStatus.Done;
	public bool IsWarning => step.Status == StepStatus.Warning;
	public IReadOnlyList<SetupActionItem> Actions { get; } = actions;
	public bool HasActions => Actions.Count > 0;
}

// First-run / "run setup again" page: the SetupWizard steps with their status, notes and buttons.
public sealed partial class SetupViewModel : PageViewModel
{
	private readonly SetupWizard _wizard;
	private readonly IControllerService _service;
	private readonly Action _close;

	public SetupViewModel(SetupWizard wizard, IControllerService service, Action close)
	{
		_wizard = wizard;
		_service = service;
		_close = close;
		// The language is picked in Settings and follows Windows until then.
		_wizard.LanguageChosen = true;
		_steps = Build();
	}

	public SetupWizard Wizard => _wizard;
	public override string Title => Text.SetupPageTitle();
	public string Intro => Text.SetupPageIntro();
	public string CloseText => Text.SetupClose();
	public bool HasMessage => !string.IsNullOrEmpty(Message);

	[ObservableProperty]
	private IReadOnlyList<SetupStepItem> _steps;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasMessage))]
	private string _message = string.Empty;

	[ObservableProperty]
	private bool _isBusy;

	[RelayCommand]
	private void Close() => _close();

	protected override void OnLanguageChanged()
	{
		Steps = Build();
		base.OnLanguageChanged();
	}

	private IReadOnlyList<SetupStepItem> Build() => _wizard.Steps
		.Select(s => new SetupStepItem(s, s.Actions.Select(a => new SetupActionItem(a.Label(null), new AsyncRelayCommand(() => RunAsync(a)))).ToList()))
		.ToList();

	private async Task RunAsync(SetupAction action)
	{
		if (IsBusy)
		{
			return;
		}
		IsBusy = true;
		try
		{
			if (_wizard.Execute(action))
			{
				Message = Text.AdapterTestRunning();
				AdapterTestResult result = await _service.TestAdapterAsync();
				_wizard.AdapterVerdict = result.Verdict;
				Message = result.Text;
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
	}
}
