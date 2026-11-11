// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Kopru.App.Services;
using Kopru.Core.Audio;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One entry of a device picker: a short name (the controller's own endpoints get plain names), the Windows name for the
// tooltip.
public sealed record AudioDeviceItem(string Id, string Name, string FullName, bool IsPad)
{
	public override string ToString() => Name;
}

// The "Ses Çıkışı" and "Mikrofon" pickers (Quick Settings and Settings use this one instance through SettingsState): the
// active endpoints of each flow with the Windows default selected, following changes made anywhere (Windows settings,
// the default device guard) live. Choosing an entry makes it the Windows default for every role (Core AudioDevices); the
// default device guard counts it as the user's own choice. Disabled with a one-line reason when setting the default does
// not work here.
public sealed partial class AudioDevicesViewModel : ViewModelBase
{
	private readonly IControllerService _service;
	private readonly AudioDevices? _devices;
	private IReadOnlyList<AudioDeviceItem> _outputs = [];
	private IReadOnlyList<AudioDeviceItem> _inputs = [];
	private AudioDeviceItem? _selectedOutput;
	private AudioDeviceItem? _selectedInput;
	private bool _syncing;
	private string _errorText = string.Empty;

	public AudioDevicesViewModel(IControllerService service)
	{
		_service = service;
		_devices = service.AudioDevices;
		if (_devices != null)
		{
			_devices.Changed += s => RunOnUi(() => Apply(s));
			Apply(_devices.Snapshot);
			_devices.Start();
		}
		Localizer.LanguageChanged += _ => RunOnUi(() => Apply(Snapshot, force: true));
	}

	// Raised on the UI thread after the devices, the defaults or the microphone's mute switch changed.
	public event Action<AudioDeviceSnapshot>? SnapshotChanged;

	public AudioDeviceSnapshot Snapshot { get; private set; } = AudioDeviceSnapshot.Empty;

	public string OutputLabel => Text.AudioOutput();
	public string InputLabel => Text.AudioInput();
	public string OutputHint => Text.AudioOutputHint();
	public string InputHint => Text.AudioInputHint();
	public string OutputTip => Text.QuickOutputTip();
	public string InputTip => Text.QuickInputTip();

	public IReadOnlyList<AudioDeviceItem> Outputs => _outputs;
	public IReadOnlyList<AudioDeviceItem> Inputs => _inputs;

	public AudioDeviceItem? SelectedOutput
	{
		get => _selectedOutput;
		set => Pick(AudioFlow.Render, value, ref _selectedOutput, nameof(SelectedOutput));
	}

	public AudioDeviceItem? SelectedInput
	{
		get => _selectedInput;
		set => Pick(AudioFlow.Capture, value, ref _selectedInput, nameof(SelectedInput));
	}

	// The pickers work: Windows devices are known here and setting the default has not failed.
	public bool CanChoose => _devices != null && Snapshot.CanSetDefault;

	public bool ShowsUnavailable => !CanChoose;

	public string UnavailableText => CanChoose ? string.Empty : Text.AudioDevicesUnavailable();

	public string UnavailableShortText => CanChoose ? string.Empty : Text.AudioDevicesUnavailableShort();

	// A choice Windows did not take (cleared by the next choice).
	public string ErrorText
	{
		get => _errorText;
		private set
		{
			if (_errorText != value)
			{
				_errorText = value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(HasError));
				OnPropertyChanged(nameof(HasOutputError));
				OnPropertyChanged(nameof(HasInputError));
			}
		}
	}

	public bool HasError => ErrorText.Length > 0;

	// The same message under the picker that was used (Settings shows each picker in its own group).
	public bool HasOutputError => HasError && _errorFlow == AudioFlow.Render;

	public bool HasInputError => HasError && _errorFlow == AudioFlow.Capture;

	private AudioFlow _errorFlow;

	// Mutes or unmutes the selected (default) microphone; the snapshot follows.
	public void SetMicMute(bool muted) => _devices?.SetMicMute(muted);

	private void Pick(AudioFlow flow, AudioDeviceItem? value, ref AudioDeviceItem? field, string name)
	{
		if (_syncing || value == null || value == field)
		{
			return;
		}
		if (!CanChoose)
		{
			OnPropertyChanged(name);
			return;
		}
		field = value;
		ErrorText = string.Empty;
		_errorFlow = flow;
		_devices!.Choose(flow, value.Id, ok => RunOnUi(() =>
		{
			// Unavailable now: the reason line says it instead.
			if (!ok && _devices.Snapshot.CanSetDefault)
			{
				ErrorText = Text.AudioDeviceNotChanged();
			}
			// Back to what Windows has (also after a refused choice).
			Apply(_devices.Snapshot, force: true);
		}));
	}

	private void Apply(AudioDeviceSnapshot snapshot, bool force = false)
	{
		bool changed = force || snapshot != Snapshot;
		Snapshot = snapshot;
		_syncing = true;
		try
		{
			IReadOnlyList<AudioDeviceItem> outputs = Items(snapshot.Render, AudioFlow.Render);
			IReadOnlyList<AudioDeviceItem> inputs = Items(snapshot.Capture, AudioFlow.Capture);
			if (!outputs.SequenceEqual(_outputs))
			{
				_outputs = outputs;
				OnPropertyChanged(nameof(Outputs));
			}
			if (!inputs.SequenceEqual(_inputs))
			{
				_inputs = inputs;
				OnPropertyChanged(nameof(Inputs));
			}
			_selectedOutput = _outputs.FirstOrDefault(i => i.Id == snapshot.RenderDefault);
			_selectedInput = _inputs.FirstOrDefault(i => i.Id == snapshot.CaptureDefault);
			OnPropertyChanged(nameof(SelectedOutput));
			OnPropertyChanged(nameof(SelectedInput));
			OnPropertyChanged(nameof(CanChoose));
			OnPropertyChanged(nameof(ShowsUnavailable));
			OnPropertyChanged(nameof(UnavailableText));
			OnPropertyChanged(nameof(UnavailableShortText));
		}
		finally
		{
			_syncing = false;
		}
		if (changed)
		{
			SnapshotChanged?.Invoke(snapshot);
		}
	}

	private IReadOnlyList<AudioDeviceItem> Items(IReadOnlyList<AudioDeviceEntry> entries, AudioFlow flow)
	{
		int pads = entries.Count(e => e.IsPad);
		int ordinal = 0;
		var list = new List<AudioDeviceItem>();
		foreach (AudioDeviceEntry e in entries)
		{
			if (!e.IsPad)
			{
				continue;
			}
			ordinal++;
			int? slot = pads > 1 ? (e.Port is int port ? _service.SlotOfAudioPort(port) : null) ?? ordinal : null;
			string name = flow == AudioFlow.Render ? Text.ControllerSpeakerDevice(slot) : Text.ControllerMicDevice(slot);
			list.Add(new AudioDeviceItem(e.Id, name, e.Name, true));
		}
		// The controller first, then the PC's devices by name.
		list.AddRange(entries.Where(e => !e.IsPad).Select(e => new AudioDeviceItem(e.Id, ShortName(e.Name), e.Name, false))
			.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase));
		return list;
	}

	// Windows names like "Hoparlör (2- Realtek(R) Audio)": without the "2- " numbering and the (R) / (TM) marks.
	public static string ShortName(string name)
	{
		string s = Regex.Replace(name, @"\(\d+- ", "(");
		s = Regex.Replace(s, @"\((R|TM)\)", string.Empty, RegexOptions.IgnoreCase);
		s = Regex.Replace(s, @"\s{2,}", " ").Trim();
		return s.Length > 0 ? s : name;
	}
}
