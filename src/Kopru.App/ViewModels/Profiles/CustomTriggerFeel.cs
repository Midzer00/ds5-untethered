// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Controls;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One trigger's Custom Trigger block in a game's pop-up: the effect (Game's Effect, Resistance, Weapon, Vibration) and that
// effect's sliders: Resistance Start and Force; Weapon Start, End (always after Start: moving one pushes the other) and
// Force; Vibration Start, Force and Frequency. Positions in percent of the trigger's travel (a zone is 10 %). Reads and
// writes through the owner (ProfileFeelViewModel: the edited profile, live when it is the active one).
public sealed class CustomTriggerFeel : ObservableObject
{
	private const double ZonePercent = 10;
	private static readonly FallbackTrigger[] Order = [FallbackTrigger.Off, FallbackTrigger.Feedback, FallbackTrigger.Weapon, FallbackTrigger.Vibration];

	private readonly bool _left;
	private readonly Func<CustomTrigger> _get;
	private readonly Action<CustomTrigger> _set;

	public CustomTriggerFeel(bool left, Func<CustomTrigger> get, Action<CustomTrigger> set)
	{
		_left = left;
		_get = get;
		_set = set;
	}

	public bool IsLeft => _left;
	public string Label => _left ? Text.CustomTriggerLeft() : Text.CustomTriggerRight();
	public string Hint => Text.CustomTriggerHint();
	public string StartLabel => Text.CustomTriggerStart();
	public string EndLabel => Text.CustomTriggerEnd();
	public string ForceLabel => Text.CustomTriggerForce();
	public string FrequencyLabel => Text.FallbackFrequency();

	// The list stays the same on a language switch (only the names change).
	public IReadOnlyList<LabelledChoice> Choices { get; } =
		[new(() => Text.CustomTriggerGameEffect()), new(() => Text.CustomTriggerResistance()), new(() => Text.EffectWeapon()), new(() => Text.EffectVibration())];

	private CustomTrigger Value => (_get() ?? CustomTrigger.Default).Normalized();
	private FallbackShape Shape => Value.Shape;

	public FallbackTrigger Effect => Value.Effect;

	// -1 (nothing selected) is ignored.
	public int EffectIndex
	{
		get => Math.Max(0, Array.IndexOf(Order, Value.Effect));
		set
		{
			if (value >= 0 && value < Order.Length && Order[value] != Value.Effect)
			{
				Put(Value with { Effect = Order[value] });
			}
		}
	}

	public bool IsSet => Value.IsSet;
	public bool IsResistance => Value.Effect == FallbackTrigger.Feedback;
	public bool IsWeapon => Value.Effect == FallbackTrigger.Weapon;
	public bool IsVibration => Value.Effect == FallbackTrigger.Vibration;

	// The trigger's own card labels it by the button's name (L2 / R2), the same in every language.
	public string TriggerKey => _left ? "L2" : "R2";
	// The effect picker's label inside the card.
	public string EffectLabel => Text.CustomTriggerEffectLabel();
	// What "Oyunun Efekti" reads on the travel diagram when there is no custom effect.
	public string GameEffectWord => Text.CustomTriggerGameEffect();

	// The game panel's travel diagram (TriggerTravel) reads the shown effect and where it sits through these.
	public TriggerTravelEffect TravelEffect => Value.Effect switch
	{
		FallbackTrigger.Feedback => TriggerTravelEffect.Resistance,
		FallbackTrigger.Weapon => TriggerTravelEffect.Weapon,
		FallbackTrigger.Vibration => TriggerTravelEffect.Vibration,
		_ => TriggerTravelEffect.Game
	};

	// Where the shown effect starts; the weapon's block also ends at WeaponEnd, the rest run to the travel's end.
	public double ShownStart => Value.Effect switch
	{
		FallbackTrigger.Feedback => ResistanceStart,
		FallbackTrigger.Weapon => WeaponStart,
		FallbackTrigger.Vibration => VibrationStart,
		_ => 0
	};

	public double ShownEnd => IsWeapon ? WeaponEnd : 100;

	public double ResistanceStart
	{
		get => Shape.FeedbackStart * ZonePercent;
		set => PutShape(Shape with { FeedbackStart = Zone(value) });
	}

	public double WeaponStart
	{
		get => Shape.WeaponStart * ZonePercent;
		set => PutShape(Shape.WithWeaponStart(Zone(value)));
	}

	public double WeaponEnd
	{
		get => Shape.WeaponEnd * ZonePercent;
		set => PutShape(Shape.WithWeaponEnd(Zone(value)));
	}

	public double VibrationStart
	{
		get => Shape.VibrationStart * ZonePercent;
		set => PutShape(Shape with { VibrationStart = Zone(value) });
	}

	public double Frequency
	{
		get => Shape.VibrationFrequency;
		set => PutShape(Shape with { VibrationFrequency = double.IsFinite(value) ? (int)Math.Round(value) : FallbackShape.Default.VibrationFrequency });
	}

	// 0-100 %, one per trigger (shared by its three effects).
	public double Force
	{
		get => Shape.Force;
		set => PutShape(Shape with { Force = double.IsFinite(value) ? Math.Round(value) : FallbackShape.DefaultForce });
	}

	public string ResistanceStartText => Text.PercentValue(ResistanceStart);
	public string WeaponStartText => Text.PercentValue(WeaponStart);
	public string WeaponEndText => Text.PercentValue(WeaponEnd);
	public string VibrationStartText => Text.PercentValue(VibrationStart);
	public string ForceText => Text.PercentValue(Force);
	// The unit is the same in every language.
	public string FrequencyText => string.Create(CultureInfo.InvariantCulture, $"{Frequency:0} Hz");

	// The bytes this trigger gets (null: the game's own effect).
	public byte[]? Bytes() => Value.Bytes();

	public void Refresh() => OnPropertyChanged(string.Empty);

	private static int Zone(double percent) => double.IsFinite(percent) ? (int)Math.Round(percent / ZonePercent) : 0;

	private void PutShape(FallbackShape shape) => Put(Value with { Shape = shape.Normalized() });

	private void Put(CustomTrigger value)
	{
		CustomTrigger normalized = value.Normalized();
		if (normalized != Value)
		{
			_set(normalized);
		}
		Refresh();
	}
}
