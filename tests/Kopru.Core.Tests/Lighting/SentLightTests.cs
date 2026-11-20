// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Lighting;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// The session's SentLight is the light last written to the controller, whoever wrote it: the app's light (LightSender),
// the game's light let through, Low Power's dark light bar; the change counter moves only when something changed.
public class SentLightTests
{
	[Fact]
	public void After_TakesOnlyFieldsWithTheirControlBit_AndKeepsTheInstanceWhenNothingChanges()
	{
		byte[] block = new byte[OutputBlock.Size];
		block[OutputBlock.Red] = 9; // no light bar bit: not applied
		LightOutput none = LightOutput.None;
		Assert.Same(none, none.After(block));

		OutputBlock.SetLightbar(block, 10, 20, 30);
		block[OutputBlock.Flag1] |= OutputBlock.PlayerLedControl;
		block[OutputBlock.PlayerLeds] = 0x24; // bit 5 (instant) dropped
		block[OutputBlock.Flag2] |= OutputBlock.LedBrightnessControl;
		block[OutputBlock.LedBrightness] = OutputBlock.LedBrightnessLow;
		LightOutput lit = none.After(block);
		Assert.Equal((10, 20, 30, 0x04), (lit.R, lit.G, lit.B, lit.PlayerLeds));
		Assert.Equal(0.4, lit.PlayerLedLevel);
		Assert.True(lit.Lit);
		Assert.Equal(1, lit.Changes);
		Assert.Same(lit, lit.After(block));
	}

	private sealed class Rig : IDisposable
	{
		public readonly FakeLink Link = new();
		public readonly GameClock Clock = new();
		public readonly BluetoothSession Session;
		public readonly VirtualDualSense Pad;
		public readonly ControllerBridge Bridge;
		public LightSettings Light = LightSettings.FromRgb(0xFF0000);
		public readonly LightSender Sender;

		public Rig()
		{
			Session = new BluetoothSession(Link);
			Pad = GameLightTests.NewPad();
			Bridge = new ControllerBridge(Session, Pad) { Time = Clock, LightLog = _ => { } };
			Bridge.ApplyProfile(new BridgeProfile(LightFromGame: true));
			Sender = new LightSender(Session.UpdateOutput, () => Light, () => null, () => Bridge.GameControlsLight, Clock)
			{
				GameLightWrites = () => Bridge.GameLightWrites
			};
		}

		public void Dispose()
		{
			Sender.Dispose();
			Bridge.Dispose();
			Pad.Dispose();
			Session.Dispose();
		}
	}

	[Fact]
	public void FollowsTheAppsLight_TheGamesLight_AndLowPower()
	{
		using Rig rig = new();
		Assert.False(rig.Session.SentLight.Lit);

		// The app's static colour.
		rig.Sender.Tick();
		LightOutput app = rig.Session.SentLight;
		Assert.Equal((255, 0, 0), (app.R, app.G, app.B));

		// The game's colour, let through (DualSense Mode).
		GameLightTests.SendOutput(rig.Pad, b => OutputBlock.SetLightbar(b, 0, 0x40, 0));
		LightOutput game = rig.Session.SentLight;
		Assert.Equal((0, 0x40, 0), (game.R, game.G, game.B));
		Assert.True(game.Changes > app.Changes);

		// A game report without the light bar bit changes nothing.
		long changes = game.Changes;
		GameLightTests.SendOutput(rig.Pad, b => b[OutputBlock.Red] = 0x7F);
		Assert.Equal(changes, rig.Session.SentLight.Changes);

		// The game lets go; Low Power (brightness 0): the light bar goes dark.
		rig.Bridge.EndGameSession("test");
		rig.Light = rig.Light with { Brightness = 0 };
		rig.Sender.Invalidate();
		rig.Clock.Advance(LightSender.SteadyInterval);
		rig.Sender.Tick();
		Assert.False(rig.Session.SentLight.Lit);
	}
}
