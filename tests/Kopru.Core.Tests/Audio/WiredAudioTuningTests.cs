// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// Fix 8 (crackle while vibrating in wired mode): the environment switches of the wired haptics stream and the shaping
// they turn on (gain, soft limiter, fades, smooth steering inside the block, the speaker's sounding state).
public class WiredAudioTuningTests
{
	private static WiredAudioTuning Parse(params (string Name, string Value)[] vars) =>
		WiredAudioTuning.Parse(name => vars.FirstOrDefault(v => v.Name == name).Value);

	[Fact]
	public void NoVariables_AreTheDefaults_SmoothSteeringAndA2MsGapFadeOnly()
	{
		WiredAudioTuning t = Parse();
		Assert.True(t.IsDefault);
		Assert.Equal(WiredSteering.Smooth, t.Steering);
		Assert.Equal(96, t.GapFadeFrames);
		Assert.Equal(0, t.StartFadeFrames);
		Assert.Equal(0, t.ReleaseFadeFrames);
		Assert.Equal(1f, t.HapticsGain);
		Assert.Null(t.Limit);
		Assert.Equal(WiredRenderPump.CushionFrames, t.CushionFrames);
		Assert.Equal(WiredRenderPump.MaxQueuedFrames, t.MaxQueuedFrames);
		Assert.Null(t.SpeakerIdleVolume);
		Assert.False(t.EventLog);
		Assert.Empty(t.Notes);
		Assert.Equal("steer smooth, gap fade 2 ms, start fade 0 ms, release fade 0 ms, gain 1.00, limiter off, buffer 20 ms," +
			" speaker idle keep, event log off", t.Describe());
	}

	[Fact]
	public void EveryVariable_IsRead()
	{
		WiredAudioTuning t = Parse(
			(WiredAudioTuning.SteerVariable, " Legacy "),
			(WiredAudioTuning.FadeVariable, "3"),
			(WiredAudioTuning.GainVariable, "0,5"),
			(WiredAudioTuning.LimitVariable, "0.6"),
			(WiredAudioTuning.BufferVariable, "40"),
			(WiredAudioTuning.SpeakerIdleVariable, "mute"),
			(WiredAudioTuning.EventLogVariable, "1"));
		Assert.False(t.IsDefault);
		Assert.Equal(WiredSteering.Legacy, t.Steering);
		Assert.Equal(144, t.GapFadeFrames);
		Assert.Equal(144, t.StartFadeFrames);
		Assert.Equal(144, t.ReleaseFadeFrames);
		Assert.Equal(0.5f, t.HapticsGain);
		Assert.Equal(0.6f, t.Limit);
		Assert.Equal(1920, t.TargetFrames);
		Assert.Equal(1920, t.CushionFrames);
		Assert.Equal(1920 + WiredAudioTuning.HeadroomFrames, t.MaxQueuedFrames);
		Assert.Equal(0, t.SpeakerIdleVolume);
		Assert.True(t.EventLog);
		Assert.Empty(t.Notes);
		Assert.Equal("steer legacy, gap fade 3 ms, start fade 3 ms, release fade 3 ms, gain 0.50, limiter 0.60, buffer 40 ms," +
			" speaker idle mute, event log on", t.Describe());
	}

	[Fact]
	public void SpeakerIdle_TakesAPercent_OrKeep()
	{
		Assert.Equal(0.3, Parse((WiredAudioTuning.SpeakerIdleVariable, "30")).SpeakerIdleVolume!.Value, 6);
		Assert.Null(Parse((WiredAudioTuning.SpeakerIdleVariable, "keep")).SpeakerIdleVolume);
		Assert.Contains("speaker idle 30 %", Parse((WiredAudioTuning.SpeakerIdleVariable, "30")).Describe(), StringComparison.Ordinal);
	}

	[Fact]
	public void FadeZero_TurnsTheDefaultGapFadeOff_AndLimitOffIsNone()
	{
		WiredAudioTuning t = Parse((WiredAudioTuning.FadeVariable, "0"), (WiredAudioTuning.LimitVariable, "off"));
		Assert.Equal(0, t.GapFadeFrames);
		Assert.Null(t.Limit);
		Assert.False(t.IsDefault);
	}

	[Fact]
	public void ValuesOutOfRange_AreClamped_AndOnesNotUnderstood_Ignored_BothNoted()
	{
		WiredAudioTuning t = Parse(
			(WiredAudioTuning.BufferVariable, "500"),
			(WiredAudioTuning.GainVariable, "lots"),
			(WiredAudioTuning.SteerVariable, "sideways"));
		Assert.Equal(WiredAudioTuning.MaxBufferMs * 48, t.TargetFrames);
		Assert.Equal(1f, t.HapticsGain);
		Assert.Equal(WiredSteering.Smooth, t.Steering);
		Assert.Equal(3, t.Notes.Count);
		Assert.EndsWith("(KOPRU_WIRED_STEER not understood; KOPRU_WIRED_GAIN not understood; KOPRU_WIRED_BUFFER_MS clamped)", t.Describe(), StringComparison.Ordinal);
		// The most queued (target + 40 ms) plus one stretched block stays inside the 100 ms endpoint buffer.
		Assert.True(t.MaxQueuedFrames + 512 + WiredAudioSteering.MaxFramesPerBlock < 4800);
	}

	[Fact]
	public void Limiter_NeverExceedsItsCeiling_IsOddAndMonotonic_AndNearlyLinearWellBelowIt()
	{
		Assert.Equal(0f, WiredHapticsLimiter.Apply(0, 0.5f));
		float last = -1;
		for (float x = -1; x <= 1; x += 0.01f)
		{
			float y = WiredHapticsLimiter.Apply(x, 0.5f);
			Assert.True(Math.Abs(y) < 0.5f);
			Assert.True(y > last);
			Assert.Equal(-y, WiredHapticsLimiter.Apply(-x, 0.5f), 6);
			last = y;
		}
		Assert.Equal(0.05f, WiredHapticsLimiter.Apply(0.05f, 0.5f), 3);
	}

	[Fact]
	public void GainAndLimiter_ShapeTheHapticsSamples()
	{
		WiredAudioFrames half = new(new WiredAudioTuning { HapticsGain = 0.5f });
		WiredAudioFrames limited = new(new WiredAudioTuning { Limit = 0.5f });
		float[] a = new float[16 * 4], b = new float[16 * 4];
		half.Build([(sbyte)127], [(sbyte)-127], a, 4);
		limited.Build([(sbyte)127], [(sbyte)-127], b, 4);
		Assert.Equal(0.5f, a[15 * 4 + 2], 4);
		Assert.Equal(-0.5f, a[15 * 4 + 3], 4);
		Assert.Equal(0.5f * MathF.Tanh(2f), b[15 * 4 + 2], 4);
		Assert.All(b, v => Assert.True(Math.Abs(v) < 0.5f));
	}

	[Fact]
	public void ABlockBuiltAtItsNormalLength_IsTheSameAsBefore()
	{
		sbyte[] l = [.. Enumerable.Range(0, 32).Select(i => (sbyte)(i * 7 - 100))];
		sbyte[] r = [.. l.Select(v => (sbyte)-v)];
		WiredAudioFrames a = new(), b = new();
		float[] oa = new float[512 * 4], ob = new float[512 * 4];
		a.Build(l, r, oa, 4);
		b.Build(l, r, ob, 4, 512);
		Assert.Equal(oa, ob);
		// The 16-frame ramp ends on each sample.
		Assert.Equal(l[0] / 127f, oa[15 * 4 + 2], 5);
		Assert.Equal(l[31] / 127f, oa[511 * 4 + 2], 5);
	}

	[Theory]
	[InlineData(508)]
	[InlineData(516)]
	public void AStretchedOrSqueezedBlock_EndsOnItsLastSample_AndStaysContinuous(int frames)
	{
		WiredAudioFrames f = new();
		float[] o = new float[frames * 4];
		float prev = 0;
		float worst = 0;
		for (int block = 0; block < 10; block++)
		{
			sbyte[] l = [.. Enumerable.Range(0, 32).Select(i => (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * 160 * (block * 32 + i) / 3000.0)))];
			Assert.Equal(frames, f.Build(l, l, o, 4, frames));
			for (int j = 0; j < frames; j++)
			{
				worst = Math.Max(worst, Math.Abs(o[j * 4 + 2] - prev));
				prev = o[j * 4 + 2];
			}
			Assert.Equal(l[31] / 127f, o[(frames - 1) * 4 + 2], 5);
		}
		// One 3 kHz step of a 160 Hz tone at 100/127 is at most ~0.27, spread over ~16 frames.
		Assert.True(worst < 0.02f, $"worst step {worst}");
	}

	[Fact]
	public void FadeIn_ScalesTheNextFramesUpFromSilence_OnEveryChannel()
	{
		WiredAudioFrames f = new();
		float[] o = new float[32 * 4];
		f.Build([(sbyte)127], [(sbyte)127], new float[16 * 4], 4);
		f.FadeIn(20);
		f.Build([(sbyte)127, 127], [(sbyte)127, 127], o, 4);
		for (int j = 0; j < 20; j++)
		{
			Assert.Equal((j + 1) / 20f, o[j * 4 + 2], 4);
		}
		Assert.Equal(1f, o[25 * 4 + 2], 4);
	}

	[Fact]
	public void ReleaseFade_LetsTheHapticsDecayOverItsLength_AcrossBlocks_ThenSilence()
	{
		WiredAudioFrames f = new(new WiredAudioTuning { ReleaseFadeFrames = 24 });
		float[] o = new float[16 * 4];
		f.Build([(sbyte)127], [(sbyte)-127], o, 4);
		f.Build([(sbyte)0], [(sbyte)0], o, 4);
		for (int j = 0; j < 16; j++)
		{
			Assert.Equal(1f - (j + 1) / 24f, o[j * 4 + 2], 4);
			Assert.Equal(-1f + (j + 1) / 24f, o[j * 4 + 3], 4);
		}
		f.Build([(sbyte)0], [(sbyte)0], o, 4);
		for (int j = 0; j < 8; j++)
		{
			Assert.Equal(1f - (j + 17) / 24f, o[j * 4 + 2], 4);
		}
		Assert.All(Enumerable.Range(8, 8), j => Assert.Equal(0f, o[j * 4 + 2], 6));
		f.Build([(sbyte)0], [(sbyte)0], o, 4);
		Assert.All(o, v => Assert.Equal(0f, v));
	}

	[Fact]
	public void SoundDuringARelease_RampsOnFromWhereTheFadeWas()
	{
		WiredAudioFrames f = new(new WiredAudioTuning { ReleaseFadeFrames = 32 });
		float[] o = new float[16 * 4];
		f.Build([(sbyte)127], [(sbyte)127], o, 4);
		f.Build([(sbyte)0], [(sbyte)0], o, 4);
		float faded = o[15 * 4 + 2];
		Assert.Equal(0.5f, faded, 4);
		f.Build([(sbyte)127], [(sbyte)127], o, 4);
		Assert.Equal(faded + (1f - faded) / 16, o[2], 4);
	}

	[Fact]
	public void WithoutReleaseFade_SilenceRampsDownIn16Frames_AsBefore()
	{
		WiredAudioFrames f = new();
		float[] o = new float[16 * 4];
		f.Build([(sbyte)127], [(sbyte)127], o, 4);
		f.Build([(sbyte)0], [(sbyte)0], o, 4);
		Assert.Equal(0f, o[15 * 4 + 2], 6);
	}

	private static byte[] FourChannel(int frames, short left, short right)
	{
		byte[] audio = new byte[frames * 8];
		for (int i = 0; i < frames; i++)
		{
			BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(i * 8), left);
			BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(i * 8 + 2), right);
		}
		return audio;
	}

	[Fact]
	public void SpeakerSounding_FollowsAudibleSpeakerSound_NotSilentGameAudio()
	{
		WiredAudioFrames f = new();
		float[] o = new float[512 * 4];
		sbyte[] quiet = new sbyte[32];
		Assert.False(f.SpeakerSounding);

		// Silent game audio streaming: still quiet.
		f.PushSpeaker(FourChannel(4800, 0, 0));
		Assert.False(f.SpeakerSounding);
		f.Build(quiet, quiet, o, 4);
		Assert.False(f.SpeakerSounding);

		// Sound pushed: sounding at once (it plays behind the cushion), and while it plays.
		f.PushSpeaker(FourChannel(4800, 1000, 0));
		Assert.True(f.SpeakerSounding);
		for (int i = 0; i < 15; i++)
		{
			f.Build(quiet, quiet, o, 4);
		}
		Assert.True(f.SpeakerSounding);

		// The rest of the sound, then over half a second of built frames without it: quiet again.
		for (int i = 0; i < 60; i++)
		{
			f.Build(quiet, quiet, o, 4);
		}
		Assert.False(f.SpeakerSounding);

		f.PushSpeaker(FourChannel(10, -2000, 0));
		f.Reset();
		Assert.False(f.SpeakerSounding);
	}
}
