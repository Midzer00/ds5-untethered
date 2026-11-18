// SPDX-License-Identifier: GPL-3.0-or-later
// Kopru.WgiProbe: what Windows' game controller APIs see while the experimental Xbox One pad is attached
// (docs/research/xbox-one-probe.md). A window of its own (Windows.Gaming.Input gives readings and vibration only to the
// app in the foreground) that shows the XInput slots and the first WGI gamepad's readings live, and plays vibration on
// it: buttons for each motor, and an automatic sequence 3 s after start (trigger motors, then main motors). A level
// slider (0-100 %) sets the strength; with Hold on, a button keeps its motors running at the slider's level (moving the
// slider changes it live) until Stop, so steady levels can be felt. Every line goes to the window and to wgi-probe.log
// next to the exe, with times to match the "GIP motor command" and "trigger rumble" lines in app.log.
//
// Trigger feel (EXPERIMENTAL tuning): sliders for the impulse trigger emulation (min/max Hz, frequency and amplitude
// curves, spin-up/spin-down ms) and Xbox-like / Soft / Sharp presets. Every change is written to
// %LOCALAPPDATA%\Untethered\impulse-tuning.json, which the app re-reads within 0.1 s while KOPRU_EXPERIMENTAL_XBOXONE is
// set, so a held level (Hold) changes feel live. The line under the sliders shows what the app plays for the slider's
// level (same code: src/Kopru.Core/Bridge/ImpulseTuning.cs), and the log gets the numbers to report back.
//
//   dotnet run --project tools\Kopru.WgiProbe [-- --level 0.75] [--no-auto]
using System.Globalization;
using System.Runtime.InteropServices;
using Kopru.Core.Bridge;
using Windows.Gaming.Input;

namespace Kopru.WgiProbe;

internal static class Program
{
	[STAThread]
	private static void Main(string[] args)
	{
		CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
		ApplicationConfiguration.Initialize();
		double level = 0.75;
		int i = Array.IndexOf(args, "--level");
		if (i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
		{
			level = Math.Clamp(v, 0, 1);
		}
		Application.Run(new ProbeForm(level, !args.Contains("--no-auto")));
	}
}

internal sealed class ProbeForm : Form
{
	// Long enough to feel each step clearly on the trigger.
	private const int StepMs = 2000;
	private const int GapMs = 500;
	private const int AutoDelayMs = 3000;

	private readonly string logPath = Path.Combine(AppContext.BaseDirectory, "wgi-probe.log");
	private readonly Label front = new() { AutoSize = true };
	private readonly Label padInfo = new() { AutoSize = true, MaximumSize = new Size(860, 0) };
	private readonly Label reading = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 10f) };
	private readonly Label xinput = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 9f) };
	private readonly TrackBar levelSlider = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 1, LargeChange = 10, Width = 220 };
	private readonly Label levelText = new() { AutoSize = true, Padding = new Padding(0, 6, 8, 0) };
	private readonly CheckBox hold = new() { Text = "Hold", AutoSize = true, Padding = new Padding(0, 4, 8, 0) };
	private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f) };
	private readonly System.Windows.Forms.Timer timer = new() { Interval = 50 };
	private readonly bool auto;
	private Gamepad? pad;
	private GamepadReading lastReading;
	private DateTime lastReadingLogged, lastXInput;
	private CancellationTokenSource? playing;
	private int ticks;

	// While Hold plays: the motors it drives (0 or 1 each, scaled by the slider) and its name; null when nothing is held.
	private (string Name, GamepadVibration Shape)? held;
	private DateTime lastLevelLogged;

	// Trigger feel tuning: the sliders (value = setting x scale), what they last wrote, and a pending write.
	private readonly TuningSlider minHz = new("Min Hz", 1, 255, 1);
	private readonly TuningSlider maxHz = new("Max Hz", 1, 255, 1);
	private readonly TuningSlider frequencyCurve = new("Hz curve", 20, 300, 100);
	private readonly TuningSlider amplitudeCurve = new("Amp curve", 20, 300, 100);
	private readonly TuningSlider attackMs = new("Spin-up ms", 0, 300, 1);
	private readonly TuningSlider decayMs = new("Spin-down ms", 0, 300, 1);
	private readonly Label mapping = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 9f), MaximumSize = new Size(860, 0) };
	private ImpulseTuning tuning = ImpulseTuning.XboxLike;
	private bool tuningDirty, loadingTuning;
	private DateTime lastTuningWrite;

	public ProbeForm(double level, bool auto)
	{
		this.auto = auto;
		Text = "Kopru WGI probe";
		Width = 900;
		Height = 860;
		StartPosition = FormStartPosition.CenterScreen;
		levelSlider.Value = (int)Math.Round(level * 100);
		levelText.Text = LevelLabel();
		levelSlider.ValueChanged += (_, _) => OnLevelChanged();
		hold.CheckedChanged += (_, _) =>
		{
			if (!hold.Checked && held != null)
			{
				StopPlaying("Hold off");
			}
		};

		FlowLayoutPanel buttons = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
		buttons.Controls.Add(new Label { Text = "Level", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
		buttons.Controls.Add(levelSlider);
		buttons.Controls.Add(levelText);
		buttons.Controls.Add(hold);
		AddButton(buttons, "Left Trigger", () => Play("LeftTrigger", Shape(lt: 1)));
		AddButton(buttons, "Right Trigger", () => Play("RightTrigger", Shape(rt: 1)));
		AddButton(buttons, "Left Motor", () => Play("LeftMotor", Shape(lm: 1)));
		AddButton(buttons, "Right Motor", () => Play("RightMotor", Shape(rm: 1)));
		AddButton(buttons, "All", () => Play("all four", Shape(1, 1, 1, 1)));
		AddButton(buttons, "Sequence", Sequence);
		AddButton(buttons, "Stop", () => { StopPlaying("Stop pressed"); return Task.CompletedTask; });

		FlowLayoutPanel feel = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
		feel.Controls.Add(new Label { Text = "Trigger feel (experimental):", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
		foreach ((string name, ImpulseTuning preset) in ImpulseTuning.Presets)
		{
			AddButton(feel, name, () =>
			{
				ShowTuning(preset);
				Say($"tuning preset {name}");
				return Task.CompletedTask;
			});
		}
		foreach (TuningSlider s in Sliders)
		{
			s.Changed += OnTuningSliderChanged;
			feel.Controls.Add(s.Panel);
		}
		feel.Controls.Add(mapping);
		levelSlider.ValueChanged += (_, _) => ShowMapping();
		ShowTuning(LoadTuning());

		TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
		layout.Controls.Add(front);
		layout.Controls.Add(padInfo);
		layout.Controls.Add(reading);
		layout.Controls.Add(xinput);
		layout.Controls.Add(buttons);
		layout.Controls.Add(feel);
		layout.Controls.Add(log);
		for (int r = 0; r < 6; r++)
		{
			layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		}
		layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		Controls.Add(layout);

		timer.Tick += (_, _) => OnTick();
		// Losing focus makes Windows clear the motors (QUIESCE); a held level comes back when the window is in front again.
		Activated += (_, _) => ApplyHeld("window in front again");
		Shown += async (_, _) => await OnShownAsync();
		FormClosing += (_, _) =>
		{
			StopPlaying(null);
			timer.Stop();
			Say("closing");
		};
	}

	private async Task OnShownAsync()
	{
		// Bring this window to the front so WGI talks to us.
		TopMost = true;
		Activate();
		TopMost = false;
		Say($"Kopru WGI probe, Windows {Environment.OSVersion.Version}, log file {logPath}");
		Gamepad.GamepadAdded += (_, _) => BeginInvoke(() => Say("event: GamepadAdded"));
		Gamepad.GamepadRemoved += (_, _) => BeginInvoke(() => Say("event: GamepadRemoved"));
		RawGameController.RawGameControllerAdded += (_, r) => BeginInvoke(() => Say($"event: RawGameControllerAdded {Describe(r)}"));
		LogXInputSlots();
		timer.Start();
		if (auto)
		{
			Say($"automatic sequence in {AutoDelayMs / 1000} s (keep this window in front)");
			await Task.Delay(AutoDelayMs);
			await Sequence();
		}
	}

	private void OnTick()
	{
		bool inFront = GetForegroundWindow() == Handle;
		front.Text = inFront ? "This window is in front: WGI readings and vibration should work." : "NOT in front: click this window, WGI ignores background apps.";
		front.ForeColor = inFront ? Color.DarkGreen : Color.DarkRed;
		if (pad == null && Gamepad.Gamepads.Count > 0)
		{
			pad = Gamepad.Gamepads[0];
			RawGameController? raw = RawGameController.FromGameController(pad);
			string text = $"first WGI gamepad: {(raw != null ? Describe(raw) : "(no raw controller)")}, wireless {pad.IsWireless}";
			padInfo.Text = text;
			Say(text);
			Say($"RawGameControllers: {RawGameController.RawGameControllers.Count}, Gamepads: {Gamepad.Gamepads.Count}");
		}
		else if (pad != null && !Gamepad.Gamepads.Contains(pad))
		{
			Say("the gamepad went away");
			pad = null;
			padInfo.Text = "no WGI gamepad";
		}
		if (pad == null)
		{
			if (ticks++ == 60)
			{
				Say("still no WGI gamepad after 3 s");
			}
			reading.Text = "no WGI gamepad";
		}
		else
		{
			GamepadReading r = pad.GetCurrentReading();
			reading.Text = Format(r);
			if (!Same(r, lastReading) && DateTime.UtcNow - lastReadingLogged > TimeSpan.FromMilliseconds(100))
			{
				lastReading = r;
				lastReadingLogged = DateTime.UtcNow;
				Say("reading " + Format(r).Replace(Environment.NewLine, "  "), screen: false);
			}
		}
		if (DateTime.UtcNow - lastXInput > TimeSpan.FromMilliseconds(500))
		{
			lastXInput = DateTime.UtcNow;
			xinput.Text = string.Join(Environment.NewLine, XInputLines());
		}
		WriteTuningIfDirty();
	}

	private TuningSlider[] Sliders => [minHz, maxHz, frequencyCurve, amplitudeCurve, attackMs, decayMs];

	// The tuning file as the app would read it; the Xbox-like default without one.
	private ImpulseTuning LoadTuning()
	{
		try
		{
			return File.Exists(ImpulseTuning.DefaultPath) ? ImpulseTuning.FromJson(File.ReadAllText(ImpulseTuning.DefaultPath)) : ImpulseTuning.XboxLike;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			return ImpulseTuning.XboxLike;
		}
	}

	// Puts a tuning on the sliders (a preset, or the file at start); a preset is written like a slider move.
	private void ShowTuning(ImpulseTuning t)
	{
		loadingTuning = true;
		minHz.Value = t.MinHz;
		maxHz.Value = t.MaxHz;
		frequencyCurve.Value = t.FrequencyCurve;
		amplitudeCurve.Value = t.AmplitudeCurve;
		attackMs.Value = t.AttackMs;
		decayMs.Value = t.DecayMs;
		loadingTuning = false;
		OnTuningSliderChanged();
	}

	private void OnTuningSliderChanged()
	{
		if (loadingTuning)
		{
			return;
		}
		ImpulseTuning next = new ImpulseTuning
		{
			MinHz = (int)minHz.Value,
			MaxHz = (int)maxHz.Value,
			FrequencyCurve = frequencyCurve.Value,
			AmplitudeCurve = amplitudeCurve.Value,
			AttackMs = attackMs.Value,
			DecayMs = decayMs.Value
		}.Normalized();
		if (next == tuning && IsHandleCreated)
		{
			return;
		}
		tuning = next;
		tuningDirty = IsHandleCreated;
		ShowMapping();
	}

	// What the app plays for the level slider (steady state, after spin-up), and a few reference levels.
	private void ShowMapping()
	{
		(int a, int hz) = tuning.Map(levelSlider.Value);
		string now = a == 0 ? $"{levelSlider.Value} % -> off" : $"{levelSlider.Value} % -> amplitude {a}/8 at {hz} Hz";
		string table = string.Join("  ", new[] { 10, 25, 50, 75, 100 }.Select(l => tuning.Map(l) is var (la, lh) ? $"{l}%:{la}@{lh}" : ""));
		mapping.Text = $"App plays (steady state): {now}{Environment.NewLine}{table}{Environment.NewLine}{tuning.Describe()}";
	}

	// At most ten writes a second while a slider is dragged; the app polls the file every 0.1 s.
	private void WriteTuningIfDirty()
	{
		if (!tuningDirty || DateTime.UtcNow - lastTuningWrite < TimeSpan.FromMilliseconds(100))
		{
			return;
		}
		tuningDirty = false;
		lastTuningWrite = DateTime.UtcNow;
		try
		{
			tuning.Save(ImpulseTuning.DefaultPath);
			Say($"tuning written: {tuning.Describe()}");
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			tuningDirty = true;
			Say($"tuning not written ({e.Message}), retrying");
		}
	}

	private double Level => levelSlider.Value / 100.0;

	private string LevelLabel() => $"{levelSlider.Value} %";

	// Which motors a button drives (1 = on); the slider's level is applied when it plays (At).
	private static GamepadVibration Shape(double lt = 0, double rt = 0, double lm = 0, double rm = 0) =>
		new() { LeftTrigger = lt, RightTrigger = rt, LeftMotor = lm, RightMotor = rm };

	private GamepadVibration At(GamepadVibration shape)
	{
		double l = Level;
		return new GamepadVibration
		{
			LeftTrigger = shape.LeftTrigger * l, RightTrigger = shape.RightTrigger * l, LeftMotor = shape.LeftMotor * l, RightMotor = shape.RightMotor * l
		};
	}

	// A button: with Hold on, its motors keep running at the slider's level until Stop (or Hold off); otherwise one step.
	private Task Play(string name, GamepadVibration shape)
	{
		if (!hold.Checked)
		{
			return Steps((name, shape));
		}
		StopPlaying(null);
		held = (name, shape);
		ApplyHeld("Hold");
		return Task.CompletedTask;
	}

	private void ApplyHeld(string why)
	{
		if (held is not { } h)
		{
			return;
		}
		if (pad is not { } target)
		{
			Say("vibration: no WGI gamepad");
			return;
		}
		GamepadVibration v = At(h.Shape);
		target.Vibration = v;
		Say($"vibration {h.Name} HELD at {levelSlider.Value} % ({why}; LT {v.LeftTrigger:0.00} RT {v.RightTrigger:0.00} LM {v.LeftMotor:0.00} " +
			$"RM {v.RightMotor:0.00}), in front: {GetForegroundWindow() == Handle}");
	}

	private void OnLevelChanged()
	{
		levelText.Text = LevelLabel();
		if (held is not { } h || pad is not { } target)
		{
			return;
		}
		target.Vibration = At(h.Shape);
		// Dragging fires many changes: the log gets at most five lines a second (the controller gets every change).
		if (DateTime.UtcNow - lastLevelLogged > TimeSpan.FromMilliseconds(200))
		{
			lastLevelLogged = DateTime.UtcNow;
			Say($"vibration {h.Name} level {levelSlider.Value} %");
		}
	}

	private Task Sequence() => Steps(
		("LeftTrigger", Shape(lt: 1)), ("RightTrigger", Shape(rt: 1)), ("LeftMotor", Shape(lm: 1)), ("RightMotor", Shape(rm: 1)), ("all four", Shape(1, 1, 1, 1)));

	// Each step: on for StepMs at the slider's level (read when the step starts), then off for GapMs. A new press stops
	// what was playing (a held level too).
	private async Task Steps(params (string Name, GamepadVibration Shape)[] steps)
	{
		if (pad is not { } target)
		{
			Say("vibration: no WGI gamepad");
			return;
		}
		StopPlaying(null);
		CancellationTokenSource cts = playing = new CancellationTokenSource();
		try
		{
			foreach ((string name, GamepadVibration shape) in steps)
			{
				GamepadVibration v = At(shape);
				Say($"vibration {name} ON (LT {v.LeftTrigger:0.00} RT {v.RightTrigger:0.00} LM {v.LeftMotor:0.00} RM {v.RightMotor:0.00}), in front: {GetForegroundWindow() == Handle}");
				target.Vibration = v;
				await Task.Delay(StepMs, cts.Token);
				target.Vibration = default;
				Say($"vibration {name} off");
				await Task.Delay(GapMs, cts.Token);
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			target.Vibration = default;
			if (playing == cts)
			{
				playing = null;
			}
		}
	}

	private void StopPlaying(string? why)
	{
		if (held != null)
		{
			held = null;
			if (why != null)
			{
				Say($"held vibration stopped ({why})");
			}
		}
		if (playing != null)
		{
			playing.Cancel();
			playing = null;
			if (why != null)
			{
				Say($"vibration stopped ({why})");
			}
		}
		if (pad != null)
		{
			pad.Vibration = default;
		}
	}

	private void AddButton(FlowLayoutPanel panel, string text, Func<Task> action)
	{
		Button b = new() { Text = text, AutoSize = true };
		b.Click += async (_, _) => await action();
		panel.Controls.Add(b);
	}

	private void LogXInputSlots()
	{
		Say("XInput slots (xinput1_4):");
		foreach (string line in XInputLines())
		{
			Say("  " + line);
		}
	}

	private static IEnumerable<string> XInputLines()
	{
		for (uint slot = 0; slot < 4; slot++)
		{
			uint result = XInputGetState(slot, out XInputState state);
			if (result != 0)
			{
				yield return $"XInput {slot}: empty (code {result})";
				continue;
			}
			string kind = XInputGetCapabilities(slot, 0, out XInputCapabilities caps) == 0
				? $"type {caps.Type} subtype {caps.SubType} flags 0x{caps.Flags:X4}"
				: "capabilities unknown";
			XInputGamepad g = state.Gamepad;
			yield return $"XInput {slot}: connected ({kind}), packet {state.PacketNumber}, buttons 0x{g.Buttons:X4}, LT {g.LeftTrigger}, RT {g.RightTrigger}, " +
				$"LX {g.ThumbLX}, LY {g.ThumbLY}, RX {g.ThumbRX}, RY {g.ThumbRY}";
		}
	}

	private void Say(string line, bool screen = true)
	{
		string stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
		if (screen)
		{
			log.AppendText(stamped + Environment.NewLine);
		}
		try
		{
			File.AppendAllText(logPath, stamped + Environment.NewLine);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static string Describe(RawGameController r) =>
		$"\"{r.DisplayName}\" VID {r.HardwareVendorId:X4} PID {r.HardwareProductId:X4}, buttons {r.ButtonCount}, axes {r.AxisCount}, switches {r.SwitchCount}, " +
		$"wireless {r.IsWireless}";

	private static string Format(GamepadReading r) =>
		$"buttons [{r.Buttons}]{Environment.NewLine}LT {r.LeftTrigger:0.000}  RT {r.RightTrigger:0.000}  " +
		$"L ({r.LeftThumbstickX:+0.000;-0.000}, {r.LeftThumbstickY:+0.000;-0.000})  R ({r.RightThumbstickX:+0.000;-0.000}, {r.RightThumbstickY:+0.000;-0.000})";

	private static bool Same(GamepadReading a, GamepadReading b) =>
		a.Buttons == b.Buttons && Near(a.LeftTrigger, b.LeftTrigger) && Near(a.RightTrigger, b.RightTrigger) && Near(a.LeftThumbstickX, b.LeftThumbstickX)
		&& Near(a.LeftThumbstickY, b.LeftThumbstickY) && Near(a.RightThumbstickX, b.RightThumbstickX) && Near(a.RightThumbstickY, b.RightThumbstickY);

	private static bool Near(double a, double b) => Math.Abs(a - b) < 0.02;

	[StructLayout(LayoutKind.Sequential)]
	private struct XInputGamepad
	{
		public ushort Buttons;
		public byte LeftTrigger;
		public byte RightTrigger;
		public short ThumbLX;
		public short ThumbLY;
		public short ThumbRX;
		public short ThumbRY;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XInputState
	{
		public uint PacketNumber;
		public XInputGamepad Gamepad;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XInputVibration
	{
		public ushort LeftMotorSpeed;
		public ushort RightMotorSpeed;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct XInputCapabilities
	{
		public byte Type;
		public byte SubType;
		public ushort Flags;
		public XInputGamepad Gamepad;
		public XInputVibration Vibration;
	}

	[DllImport("xinput1_4.dll")]
	private static extern uint XInputGetState(uint userIndex, out XInputState state);

	[DllImport("xinput1_4.dll")]
	private static extern uint XInputGetCapabilities(uint userIndex, uint flags, out XInputCapabilities capabilities);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();
}

// A labelled slider for one tuning value: the TrackBar holds value x scale (curves in hundredths), the label shows it.
internal sealed class TuningSlider
{
	private readonly TrackBar bar;
	private readonly Label text = new() { AutoSize = true, Padding = new Padding(0, 6, 8, 0), MinimumSize = new Size(110, 0) };
	private readonly string name;
	private readonly double scale;

	public TuningSlider(string name, int min, int max, double scale)
	{
		this.name = name;
		this.scale = scale;
		bar = new TrackBar { Minimum = min, Maximum = max, TickStyle = TickStyle.None, Width = 150, SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10) };
		bar.ValueChanged += (_, _) =>
		{
			ShowText();
			Changed?.Invoke();
		};
		Panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
		Panel.Controls.Add(bar);
		Panel.Controls.Add(text);
		ShowText();
	}

	public event Action? Changed;

	public Control Panel { get; }

	public double Value
	{
		get => bar.Value / scale;
		set => bar.Value = Math.Clamp((int)Math.Round(value * scale), bar.Minimum, bar.Maximum);
	}

	private void ShowText() => text.Text = string.Create(CultureInfo.InvariantCulture, $"{name} {Value:0.##}");
}