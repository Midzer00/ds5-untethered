// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;
using Xunit;

namespace Kopru.Core.Tests;

// Records what the session's haptics stream plays on the wired audio endpoint.
internal sealed class FakeWiredAudioSink : IWiredAudioSink
{
	public readonly List<(sbyte[] Left, sbyte[] Right)> Writes = [];
	public readonly List<byte[]> Speaker = [];
	public bool FailWrites;
	// Thrown by WriteHaptics when set (FailWrites throws an IOException).
	public Exception? FailWith;
	public bool Disposed;
	public int Idles;

	public void WriteHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
	{
		if (FailWith != null)
		{
			throw FailWith;
		}
		if (FailWrites)
		{
			throw new IOException("boom");
		}
		lock (Writes)
		{
			Writes.Add((left.ToArray(), right.ToArray()));
		}
	}

	public void PushSpeaker(ReadOnlySpan<byte> audio)
	{
		lock (Speaker)
		{
			Speaker.Add(audio.ToArray());
		}
	}

	public bool HasSpeakerAudio
	{
		get
		{
			lock (Speaker)
			{
				return Speaker.Count > 0;
			}
		}
	}

	// What SpeakerSounding answers (the speaker idle volume follows it).
	public volatile bool Sounding;

	public bool SpeakerSounding => Sounding;

	public void Idle() => Interlocked.Increment(ref Idles);

	public void Dispose() => Disposed = true;
}

// EXPERIMENTAL wired mode: a USB link of the same controller carries input (0x01) and output (0x02); the host then
// disconnects Bluetooth, whose reader ends quietly. A failed USB link falls back to Bluetooth only while Bluetooth is
// still connected; after the hand-off it is the controller lost.
[Trait("Category", "RealTime")]
[Collection("RealTime")]
public class BluetoothSessionWiredTests
{
	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 3000), "timed out");

	private static byte[] UsbInput(byte l2, byte sequence)
	{
		byte[] report = new byte[UsbReports.InputSize];
		report[0] = UsbReports.InputId;
		report[1 + 4] = l2;
		report[1 + 6] = sequence;
		report[1 + 52] = 0x08;
		return report;
	}

	[Fact]
	public void WiredInput_IsPublished_BluetoothInputIsCountedAndDroppedWhileWired()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		List<byte[]> bodies = [];
		session.InputBodyReceived += b =>
		{
			lock (bodies)
			{
				bodies.Add(b.ToArray());
			}
		};
		session.Start();

		bt.Feed(FakeLink.Input(77, 2));
		WaitUntil(() => { lock (bodies) return bodies.Count == 1; });
		Assert.Equal(77, bodies[0][4]);

		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);
		Assert.True(session.IsWired);

		usb.Feed(UsbInput(55, 3));
		WaitUntil(() => { lock (bodies) return bodies.Count == 2; });
		Assert.Equal(55, bodies[1][4]);
		Assert.Equal(1, session.WiredInputReports);

		bt.Feed(FakeLink.Input(99, 4));
		WaitUntil(() => session.IgnoredBluetoothInputs == 1);
		lock (bodies)
		{
			Assert.Equal(2, bodies.Count);
		}
	}

	private static byte[] LastBluetoothOutput(FakeLink bt)
	{
		byte[] last = bt.Writes.Last(w => w[0] == BluetoothReports.OutputId);
		Assert.Equal(Crc.Compute(Crc.OutputSeed, last.AsSpan(0, 74)), BinaryPrimitives.ReadUInt32LittleEndian(last.AsSpan(74)));
		return last;
	}

	private static byte[] LastUsbOutput(FakeLink usb)
	{
		byte[] last = usb.Writes.Last(w => w[0] == UsbReports.OutputId);
		Assert.Equal(UsbReports.OutputSize, last.Length);
		return last;
	}

	private const byte HapticsControls = OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl;

	[Fact]
	public void Attach_SendsTheStateOverUsb_AndOutputWhileWired_GoesOnlyToUsb()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		session.UpdateOutput(b => OutputBlock.SetRumble(b, 200, 100));
		long btWrites = bt.Writes.Count;

		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);

		// The attach sends the block as report 0x02, as a wired game does: rumble bits cleared (USB audio haptics keep
		// playing), none of the Bluetooth-only haptics controls added.
		byte[] sent = LastUsbOutput(usb);
		Assert.Equal(0, sent[1 + OutputBlock.Flag0] & (OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect));
		Assert.Equal(0, sent[1 + OutputBlock.Flag1] & HapticsControls);
		Assert.Equal((100, 200), (sent[1 + OutputBlock.MotorRight], sent[1 + OutputBlock.MotorLeft]));

		session.UpdateOutput(b => OutputBlock.SetLightbar(b, 0, 200, 40));
		session.UpdateOutput(b => Audio.AudioOutput.SetRoute(b, false, 0.5));

		Assert.Equal(btWrites, bt.Writes.Count);
		byte[] last = LastUsbOutput(usb);
		Assert.Equal((200, 40), (last[1 + OutputBlock.Red + 1], last[1 + OutputBlock.Red + 2]));
		Assert.Equal(Audio.AudioOutput.PathSpeaker, last[1 + Audio.AudioOutput.AudioControl] & Audio.AudioOutput.PathMask);
		Assert.NotEqual(0, last[1 + Audio.AudioOutput.SpeakerVolume]);
	}

	// Without haptics mode the USB report is the game's block as sent: rumble kept.
	[Fact]
	public void WiredOutput_WithoutHapticsMode_KeepsRumble()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt) { HapticsMode = false };
		session.Start();
		session.UpdateOutput(b => OutputBlock.SetRumble(b, 200, 100));

		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);

		byte[] sent = LastUsbOutput(usb);
		Assert.Equal(OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect,
			sent[1 + OutputBlock.Flag0] & (OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect));
		Assert.Equal((100, 200), (sent[1 + OutputBlock.MotorRight], sent[1 + OutputBlock.MotorLeft]));
	}

	// Back on Bluetooth (the mode changed while Bluetooth was still connected): the state is sent over it again, with
	// the Bluetooth haptics-mode controls.
	[Fact]
	public void Detach_SendsOutputOnBluetoothAgain_AndPublishesBluetoothInput()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);
		Assert.True(session.IsWired);
		long btWrites = bt.Writes.Count;

		Assert.True(session.DetachWired("test"));

		Assert.False(session.IsWired);
		Assert.Equal(btWrites + 1, bt.Writes.Count);
		Assert.Equal(HapticsControls, LastBluetoothOutput(bt)[3 + OutputBlock.Flag1] & HapticsControls);
		session.UpdateOutput(b => OutputBlock.SetLightbar(b, 1, 2, 3));
		Assert.Equal(1, LastBluetoothOutput(bt)[3 + OutputBlock.Red]);

		List<byte[]> bodies = [];
		session.InputBodyReceived += b =>
		{
			lock (bodies)
			{
				bodies.Add(b.ToArray());
			}
		};
		bt.Feed(FakeLink.Input(42, 5));
		WaitUntil(() => { lock (bodies) return bodies.Count == 1; });
		Assert.False(session.DetachWired("test"));
	}

	// While Bluetooth is still connected (before the host's hand-off, or when the radio refused it), failed USB reads
	// fall the session back to Bluetooth.
	[Fact]
	public void UsbReadsFailing_WithBluetoothConnected_FallBackToBluetooth()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);
		List<string> lost = [];
		session.WiredLost += r =>
		{
			lock (lost)
			{
				lost.Add(r);
			}
		};
		bool controllerLost = false;
		session.Lost += () => controllerLost = true;

		usb.FailReads = true;

		WaitUntil(() => !session.IsWired);
		WaitUntil(() => { lock (lost) return lost.Count == 1; });
		Assert.Equal("usb reads failed", lost[0]);
		Assert.False(controllerLost);
		session.UpdateOutput(b => OutputBlock.SetLightbar(b, 9, 8, 7));
		Assert.Equal(9, LastBluetoothOutput(bt)[3 + OutputBlock.Red]);
	}

	// Bluetooth disconnected on purpose while wired: its reader ends quietly (no Lost), input and output stay on USB.
	// Then the cable is pulled: with no Bluetooth left the controller is lost, as a switched-off one is.
	[Fact]
	public void BluetoothGoneWhileWired_IsNoLoss_ThenAPulledCable_IsTheControllerLost()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		List<byte[]> bodies = [];
		session.InputBodyReceived += b =>
		{
			lock (bodies)
			{
				bodies.Add(b.ToArray());
			}
		};
		int controllerLost = 0;
		session.Lost += () => Interlocked.Increment(ref controllerLost);
		List<string> wiredLost = [];
		session.WiredLost += r =>
		{
			lock (wiredLost)
			{
				wiredLost.Add(r);
			}
		};
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);
		usb.Feed(UsbInput(55, 3));
		WaitUntil(() => session.IsWiredInputLive);

		bt.FailReads = true;
		WaitUntil(() => session.IsBluetoothGone);
		Thread.Sleep(100);
		Assert.Equal(0, Volatile.Read(ref controllerLost));
		Assert.True(session.IsWired);
		usb.Feed(UsbInput(66, 4));
		WaitUntil(() => { lock (bodies) return bodies.Count == 2; });
		Assert.Equal(66, bodies[1][4]);
		session.UpdateOutput(b => OutputBlock.SetLightbar(b, 4, 5, 6));
		Assert.Equal(4, LastUsbOutput(usb)[1 + OutputBlock.Red]);

		usb.FailReads = true;
		WaitUntil(() => Volatile.Read(ref controllerLost) == 1);
		Assert.False(session.IsWired);
		lock (wiredLost)
		{
			Assert.Empty(wiredLost);
		}
	}

	// Leaving wired mode after Bluetooth was disconnected: nothing carries the controller, so it counts as lost.
	[Fact]
	public void DetachWired_AfterBluetoothWasDisconnected_IsTheControllerLost()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		int controllerLost = 0;
		session.Lost += () => Interlocked.Increment(ref controllerLost);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);
		bt.FailReads = true;
		WaitUntil(() => session.IsBluetoothGone);

		Assert.True(session.DetachWired("charge only chosen"));
		Assert.Equal(1, Volatile.Read(ref controllerLost));
	}

	// A failed USB write is left to the wired reader (a pulled cable fails its reads too): no fallback by itself.
	[Fact]
	public void AUsbWriteThatFails_IsLeftToTheReader_TheSessionStaysWired()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize) { FailWrites = true };
		session.AttachWired(usb);
		List<string> lost = [];
		session.WiredLost += r => lost.Add(r);
		long btWrites = bt.Writes.Count;

		session.UpdateOutput(b => OutputBlock.SetLightbar(b, 9, 8, 7));

		Assert.True(session.IsWired);
		Assert.Empty(lost);
		Assert.Equal(btWrites, bt.Writes.Count);
	}

	[Fact]
	public void AttachWired_RejectsBluetooth_AndASecondAttach()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);

		Assert.Throws<ArgumentException>(() => session.AttachWired(new FakeLink()));

		session.AttachWired(usb);
		Assert.True(session.IsWired);
		using FakeLink second = new(Transport.Usb, UsbReports.OutputSize);
		Assert.Throws<InvalidOperationException>(() => session.AttachWired(second));
	}

	[Fact]
	public void WiredAudio_HapticsGoToTheSink_NotToBluetooth()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		FakeWiredAudioSink sink = new();
		session.AttachWired(usb, sink);
		Assert.True(session.IsWiredAudio);
		int btWrites = bt.Writes.Count;

		for (int i = 0; i < 64; i++)
		{
			session.Haptics.Push(50, -50);
		}
		session.WakeHaptics();

		WaitUntil(() => { lock (sink.Writes) return sink.Writes.Count >= 2; });
		(sbyte[] left, sbyte[] right) = sink.Writes[0];
		Assert.Equal(BluetoothReports.HapticsFrames, left.Length);
		Assert.All(left, v => Assert.Equal(50, v));
		Assert.All(right, v => Assert.Equal(-50, v));
		// No 0x32 / 0x36 report goes to the Bluetooth link while the sink plays.
		Assert.DoesNotContain(bt.Writes.Skip(btWrites), w => w[0] is 0x32 or 0x36);
		WaitUntil(() => session.HapticsReports >= 2);
	}

	// Fix 8 diagnostic (KOPRU_WIRED_SPEAKER_IDLE): while the sink's channels 1-2 are quiet the controller's speaker is
	// muted over USB; sound brings the user's volume back, and leaving wired mode restores it too.
	[Fact]
	public void WiredAudio_SpeakerIdleVolume_MutesTheQuietSpeaker_AndSoundBringsTheVolumeBack()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt) { WiredTuning = new WiredAudioTuning { SpeakerIdleVolume = 0 } };
		session.Start();
		session.Speaker = new SpeakerAudioSender(new FakeOpusEncoder()) { Volume = 1 };
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		FakeWiredAudioSink sink = new();
		session.AttachWired(usb, sink);

		byte[] report = LastUsbOutput(usb);
		Assert.Equal(0, report[1 + AudioOutput.SpeakerVolume]);
		Assert.Equal(AudioOutput.PathSpeaker, report[1 + AudioOutput.AudioControl] & AudioOutput.PathMask);
		Assert.Equal(0, report[1 + AudioOutput.AudioControl2] & AudioOutput.PreampMask);
		// The game's own output keeps the idle route too.
		byte[] game = new byte[OutputBlock.Size];
		session.WriteSpeakerRoute(game);
		Assert.Equal(0, game[AudioOutput.SpeakerVolume]);

		sink.Sounding = true;
		for (int i = 0; i < 64; i++)
		{
			session.Haptics.Push(50, -50);
		}
		session.WakeHaptics();
		WaitUntil(() => LastUsbOutput(usb)[1 + AudioOutput.SpeakerVolume] == AudioOutput.MaxSpeakerVolume);
		Assert.Equal(AudioOutput.DefaultPreamp, LastUsbOutput(usb)[1 + AudioOutput.AudioControl2] & AudioOutput.PreampMask);

		sink.Sounding = false;
		for (int i = 0; i < 64; i++)
		{
			session.Haptics.Push(50, -50);
		}
		session.WakeHaptics();
		WaitUntil(() => LastUsbOutput(usb)[1 + AudioOutput.SpeakerVolume] == 0);
	}

	[Fact]
	public void WiredAudio_ByDefault_TheSpeakerKeepsTheUsersVolume()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt) { WiredTuning = WiredAudioTuning.Default };
		session.Start();
		session.Speaker = new SpeakerAudioSender(new FakeOpusEncoder()) { Volume = 1 };
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb, new FakeWiredAudioSink());
		Assert.Equal(AudioOutput.MaxSpeakerVolume, LastUsbOutput(usb)[1 + AudioOutput.SpeakerVolume]);
	}

	[Fact]
	public void WiredAudio_SpeakerAudioGoesToTheSink_NotToBluetooth()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		session.Speaker = new SpeakerAudioSender(new FakeOpusEncoder()) { Volume = 1 };
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		FakeWiredAudioSink sink = new();
		session.AttachWired(usb, sink);
		int btWrites = bt.Writes.Count;

		byte[] audio = new byte[512 * 8];
		session.PushSpeakerAudio(audio);

		WaitUntil(() => { lock (sink.Speaker) return sink.Speaker.Count == 1; });
		Assert.Equal(audio, sink.Speaker[0]);
		Assert.DoesNotContain(bt.Writes.Skip(btWrites), w => w[0] is 0x32 or 0x36);
		Assert.Equal(0, session.SpeakerReports);
	}

	[Fact]
	public void WiredAudio_AFailingWrite_DropsTheSink_AndHapticsAreBluetoothReportsAgain()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		FakeWiredAudioSink sink = new();
		session.AttachWired(usb, sink);
		List<string> lost = [];
		session.WiredAudioLost += r =>
		{
			lock (lost)
			{
				lost.Add(r);
			}
		};
		sink.FailWrites = true;
		session.Haptics.Push(40, 40);
		session.WakeHaptics();

		WaitUntil(() => { lock (lost) return lost.Count == 1; });
		Assert.Equal("usb audio write failed", lost[0]);
		Assert.True(sink.Disposed);
		Assert.False(session.IsWiredAudio);
		// Still wired (the USB link carries input/output); only audio went back.
		Assert.True(session.IsWired);
		WaitUntil(() => bt.Writes.Any(w => w[0] == 0x32));
	}

	// A sink whose device clock never moved (WiredRenderPump's stall check) is dropped with its own reason and a loud
	// log line: haptics go back to Bluetooth instead of being written into a dead stream.
	[Fact]
	public void WiredAudio_AStreamThatIsNotPlaying_IsDroppedLoudly_AndHapticsAreBluetoothReportsAgain()
	{
		List<string> lines = [];
		void Watch(string line)
		{
			lock (lines)
			{
				lines.Add(line);
			}
		}
		Diagnostics.AppLog.Written += Watch;
		try
		{
			FakeLink bt = new();
			using BluetoothSession session = new(bt);
			session.Start();
			FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
			FakeWiredAudioSink sink = new() { FailWith = new WiredAudioStalledException("device clock still 0 after 1.0 s written, queue 960 frames") };
			session.AttachWired(usb, sink);
			List<string> lost = [];
			session.WiredAudioLost += r =>
			{
				lock (lost)
				{
					lost.Add(r);
				}
			};
			session.Haptics.Push(40, 40);
			session.WakeHaptics();

			WaitUntil(() => { lock (lost) return lost.Count == 1; });
			Assert.Equal("usb audio not playing", lost[0]);
			Assert.True(sink.Disposed);
			Assert.True(session.IsWired);
			WaitUntil(() => bt.Writes.Any(w => w[0] == 0x32));
			lock (lines)
			{
				Assert.Contains("wired: USB AUDIO NOT PLAYING: device clock still 0 after 1.0 s written, queue 960 frames; haptics and speaker back on Bluetooth", lines);
			}
		}
		finally
		{
			Diagnostics.AppLog.Written -= Watch;
		}
	}

	[Fact]
	public void DetachWired_DisposesTheSink_AndHapticsAreBluetoothReportsAgain()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		FakeWiredAudioSink sink = new();
		session.AttachWired(usb, sink);

		Assert.True(session.DetachWired("test"));

		Assert.True(sink.Disposed);
		Assert.False(session.IsWiredAudio);
		session.Haptics.Push(40, 40);
		session.WakeHaptics();
		WaitUntil(() => bt.Writes.Any(w => w[0] == 0x32));
	}

	// The Bluetooth mic still rides the stream while the endpoint plays haptics: the controller gets a 0x32 report
	// with the mic-on control and all-zero samples; the sink gets the real frames.
	[Fact]
	public void WiredAudio_TheMicKeepsBluetoothStreamReports_WithSilentSamples()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		session.MicStreamEnabled = true;
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		FakeWiredAudioSink sink = new();
		session.AttachWired(usb, sink);
		int btWrites = bt.Writes.Count;
		session.Haptics.Push(50, -50);
		session.WakeHaptics();

		// The stream already ran for the mic before the push: the sink sees silent blocks first, then the real ones.
		WaitUntil(() => { lock (sink.Writes) return sink.Writes.Any(w => w.Left.Contains((sbyte)50)); });
		WaitUntil(() => bt.Writes.Skip(btWrites).Any(w => w[0] == 0x32));
		byte[] micReport = bt.Writes.Skip(btWrites).First(w => w[0] == 0x32);
		Assert.Equal(MicPackets.StreamMicOn, micReport[MicPackets.StreamControlOffset]);
		Assert.All(micReport.AsSpan(BluetoothReports.HapticsSamplesOffset, BluetoothReports.HapticsFrames * 2).ToArray(),
			b => Assert.Equal(0, b));
		lock (sink.Writes)
		{
			Assert.Contains(sink.Writes, w => w.Left.Contains((sbyte)50) && w.Right.Contains((sbyte)-50));
		}
		// One count per stream tick: the sink write and the Bluetooth mic report of the same tick count as one
		// (each tick does exactly one of each while the mic is on).
		long r0 = session.HapticsReports;
		int s0, b0;
		lock (sink.Writes)
		{
			s0 = sink.Writes.Count;
		}
		b0 = bt.Writes.Skip(btWrites).Count(w => w[0] == 0x32);
		WaitUntil(() =>
		{
			long reports = session.HapticsReports - r0;
			lock (sink.Writes)
			{
				return reports > 0 && reports == sink.Writes.Count - s0 &&
					reports == bt.Writes.Skip(btWrites).Count(w => w[0] == 0x32) - b0;
			}
		});
	}

	[Fact]
	public void WiredAudio_IdleIsCalled_WhenTheStreamStops()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		FakeWiredAudioSink sink = new();
		session.AttachWired(usb, sink);
		session.Haptics.Push(50, 50);
		session.WakeHaptics();

		WaitUntil(() => { lock (sink.Writes) return sink.Writes.Count >= 1; });
		WaitUntil(() => !session.IsHapticsStreaming);
		Assert.True(sink.Idles >= 1);
	}

	[Fact]
	public void AttachWired_WithoutAudio_KeepsHapticsOnBluetooth()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);
		Assert.False(session.IsWiredAudio);

		session.Haptics.Push(50, -50);
		session.WakeHaptics();

		WaitUntil(() => bt.Writes.Any(w => w[0] == 0x32));
	}

	// A USB link whose reads never return does not freeze input: Bluetooth keeps carrying it until the first USB
	// report. There is no silent-link fallback any more (wired mode disconnects Bluetooth instead).
	[Fact]
	public void ASilentUsbLink_BluetoothKeepsCarryingInput_AndTheSessionStaysWired()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		List<byte[]> bodies = [];
		session.InputBodyReceived += b =>
		{
			lock (bodies)
			{
				bodies.Add(b.ToArray());
			}
		};
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);

		bt.Feed(FakeLink.Input(77, 2));
		WaitUntil(() => { lock (bodies) return bodies.Count == 1; });
		Thread.Sleep(200);
		bt.Feed(FakeLink.Input(88, 3));
		WaitUntil(() => { lock (bodies) return bodies.Count == 2; });
		Assert.Equal(88, bodies[1][4]);
		Assert.True(session.IsWired);
		Assert.False(session.IsWiredInputLive);
	}

	// A USB link that starts late: Bluetooth carries input until the first USB report; Bluetooth reports after it
	// are counted and dropped.
	[Fact]
	public void ALateUsbLink_BluetoothCarriesInput_UntilTheFirstUsbReport()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		List<byte[]> bodies = [];
		session.InputBodyReceived += b =>
		{
			lock (bodies)
			{
				bodies.Add(b.ToArray());
			}
		};
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);

		bt.Feed(FakeLink.Input(77, 2));
		WaitUntil(() => { lock (bodies) return bodies.Count == 1; });
		Assert.Equal(77, bodies[0][4]);
		bt.Feed(FakeLink.Input(78, 3));
		WaitUntil(() => { lock (bodies) return bodies.Count == 2; });
		Assert.Equal(78, bodies[1][4]);

		usb.Feed(UsbInput(55, 4));
		WaitUntil(() => { lock (bodies) return bodies.Count == 3; });
		Assert.Equal(55, bodies[2][4]);
		Assert.Equal(1, session.WiredInputReports);

		bt.Feed(FakeLink.Input(99, 5));
		WaitUntil(() => session.IgnoredBluetoothInputs == 1);
		lock (bodies)
		{
			Assert.Equal(3, bodies.Count);
		}
	}

	// The link's USB audio endpoint can arrive after the link itself (or an earlier sink was lost): it attaches to
	// the running link without reopening it. A call without a matching link or with a sink already attached is
	// refused and disposes the sink it was given.
	[Fact]
	public void AttachWiredAudio_AfterTheLink_CarriesHaptics_AndIsRefusedWithoutAMatchingLinkOrWithASinkAlreadyThere()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);

		// Not wired: refused and disposed.
		FakeWiredAudioSink early = new();
		Assert.False(session.AttachWiredAudio(usb.Device, early));
		Assert.True(early.Disposed);

		session.AttachWired(usb);
		Assert.True(session.IsWired);
		Assert.False(session.IsWiredAudio);

		FakeWiredAudioSink sink = new();
		Assert.True(session.AttachWiredAudio(usb.Device, sink));
		Assert.True(session.IsWiredAudio);

		session.Haptics.Push(50, -50);
		session.WakeHaptics();
		WaitUntil(() => { lock (sink.Writes) return sink.Writes.Count >= 1; });

		// A second sink is refused and disposed; the first keeps playing.
		FakeWiredAudioSink second = new();
		Assert.False(session.AttachWiredAudio(usb.Device, second));
		Assert.True(second.Disposed);
		Assert.False(sink.Disposed);
		Assert.True(session.IsWiredAudio);

		// A device that is not the wired link's is refused and disposed.
		FakeWiredAudioSink other = new();
		HidDevice another = new("other-usb", 0x054C, 0x0CE6, 64, 48, 64);
		Assert.False(session.AttachWiredAudio(another, other));
		Assert.True(other.Disposed);
		Assert.False(sink.Disposed);

		Assert.True(session.DetachWired("test"));
		Assert.True(sink.Disposed);
		Assert.False(session.IsWiredAudio);
	}

	// The stream playing on the USB audio endpoint: haptics go to the sink, not to Bluetooth, and the output state
	// goes out over USB; the attach says where everything goes.
	[Fact]
	public void WiredAudio_TheStreamPlaysOnTheSink_AndTheOutputGoesOverUsb()
	{
		List<string> lines = [];
		void Watch(string line)
		{
			if (line.StartsWith("wired", StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		Diagnostics.AppLog.Written += Watch;
		try
		{
			FakeLink bt = new();
			using BluetoothSession session = new(bt);
			session.Start();
			FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
			FakeWiredAudioSink sink = new();
			session.AttachWired(usb, sink);
			Assert.True(session.IsWiredAudio);
			long btWrites = bt.Writes.Count;
			session.UpdateOutput(b => Audio.AudioOutput.SetRoute(b, false, 1));

			session.Haptics.Push(50, -50);
			session.WakeHaptics();
			WaitUntil(() => { lock (sink.Writes) return sink.Writes.Count >= 1; });
			Thread.Sleep(50);

			Assert.Equal(btWrites, bt.Writes.Count);
			Assert.Equal(Audio.AudioOutput.MaxSpeakerVolume, LastUsbOutput(usb)[1 + Audio.AudioOutput.SpeakerVolume]);
			lock (lines)
			{
				Assert.Contains("wired: input and output over USB, haptics/speaker over USB audio", lines);
			}
		}
		finally
		{
			Diagnostics.AppLog.Written -= Watch;
		}
	}
}
