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
	public bool Disposed;
	public int Idles;

	public void WriteHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
	{
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

	public void Idle() => Interlocked.Increment(ref Idles);

	public void Dispose() => Disposed = true;
}

// EXPERIMENTAL wired mode: a USB link of the same controller carries input (0x01) and output (0x02) while Bluetooth
// stays connected; a failed USB read or write falls the session back to Bluetooth by itself.
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

	[Fact]
	public void Attach_ResendsTheStateOnUsb_AndOutputWhileWired_GoesOnlyToUsb()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		session.UpdateOutput(b => OutputBlock.SetRumble(b, 200, 100));
		long btWrites = bt.Writes.Count;

		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);

		// The attach resend: report 0x02 carrying the block, the rumble bits cleared and the same haptics-mode
		// controls set as over Bluetooth (haptics un-muted, no power save, full motor power).
		byte[] resent = usb.Writes.Last();
		Assert.Equal(UsbReports.OutputSize, resent.Length);
		Assert.Equal(UsbReports.OutputId, resent[0]);
		Assert.Equal(0, resent[1 + OutputBlock.Flag0] & (OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect));
		Assert.Equal(OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl,
			resent[1 + OutputBlock.Flag1] & (OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl));
		Assert.Equal(0, resent[1 + OutputBlock.PowerSave]);
		Assert.Equal(0, resent[1 + OutputBlock.MotorPowerReduction]);
		Assert.Equal((100, 200), (resent[1 + OutputBlock.MotorRight], resent[1 + OutputBlock.MotorLeft]));

		session.UpdateOutput(b => OutputBlock.SetLightbar(b, 0, 200, 40));

		Assert.Equal(btWrites, bt.Writes.Count);
		byte[] last = usb.Writes.Last();
		Assert.Equal(UsbReports.OutputId, last[0]);
		Assert.Equal((200, 40), (last[1 + OutputBlock.Red + 1], last[1 + OutputBlock.Red + 2]));
		Assert.Equal(OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl,
			last[1 + OutputBlock.Flag1] & (OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl));
	}

	// In haptics mode the mute and power-save bytes the game may have left in the block are un-set over USB too.
	[Fact]
	public void WiredOutput_InHapticsMode_UnmutesHaptics_WhateverTheBlockSays()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		session.UpdateOutput(b =>
		{
			b[OutputBlock.PowerSave] = 0x84;
			b[OutputBlock.MotorPowerReduction] = 0x22;
		});

		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);

		byte[] resent = usb.Writes.Last();
		Assert.Equal(UsbReports.OutputId, resent[0]);
		Assert.Equal(0, resent[1 + OutputBlock.PowerSave]);
		Assert.Equal(0, resent[1 + OutputBlock.MotorPowerReduction]);
		Assert.Equal(OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl,
			resent[1 + OutputBlock.Flag1] & (OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl));
	}

	// Without haptics mode the USB report is the game's block as sent: rumble kept, no haptics-mode controls added.
	[Fact]
	public void WiredOutput_WithoutHapticsMode_KeepsRumble_AndAddsNoHapticsControls()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt) { HapticsMode = false };
		session.Start();
		session.UpdateOutput(b => OutputBlock.SetRumble(b, 200, 100));

		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);

		byte[] resent = usb.Writes.Last();
		Assert.Equal(UsbReports.OutputId, resent[0]);
		Assert.Equal(OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect,
			resent[1 + OutputBlock.Flag0] & (OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect));
		Assert.Equal((100, 200), (resent[1 + OutputBlock.MotorRight], resent[1 + OutputBlock.MotorLeft]));
		Assert.Equal(0, resent[1 + OutputBlock.Flag1] &
			(OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl));
	}

	[Fact]
	public void Detach_SendsOutputOnBluetoothAgain_AndPublishesBluetoothInput()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);
		Assert.True(session.IsWired);

		Assert.True(session.DetachWired("test"));

		Assert.False(session.IsWired);
		byte[] resent = bt.Writes.Last();
		Assert.Equal(BluetoothReports.OutputId, resent[0]);
		Assert.Equal(Crc.Compute(Crc.OutputSeed, resent.AsSpan(0, 74)), BinaryPrimitives.ReadUInt32LittleEndian(resent.AsSpan(74)));

		session.UpdateOutput(b => OutputBlock.SetLightbar(b, 1, 2, 3));
		byte[] last = bt.Writes.Last();
		Assert.Equal(BluetoothReports.OutputId, last[0]);
		Assert.Equal(Crc.Compute(Crc.OutputSeed, last.AsSpan(0, 74)), BinaryPrimitives.ReadUInt32LittleEndian(last.AsSpan(74)));

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

	[Fact]
	public void UsbReadsFallingBack_RaisesWiredLost_AndResendsTheStateOnBluetooth()
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
		long btWrites = bt.Writes.Count;

		usb.FailReads = true;

		WaitUntil(() => !session.IsWired);
		WaitUntil(() => { lock (lost) return lost.Count == 1; });
		Assert.Equal("usb reads failed", lost[0]);
		Assert.True(bt.Writes.Count > btWrites);
		Assert.Equal(BluetoothReports.OutputId, bt.Writes.Last()[0]);
	}

	[Fact]
	public void AUsbWriteThatFails_FallsBack_AndSendsThatUpdateOnBluetooth()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt);
		session.Start();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		session.AttachWired(usb);
		List<string> lost = [];
		session.WiredLost += r => lost.Add(r);
		usb.FailWrites = true;
		long btWrites = bt.Writes.Count;

		session.UpdateOutput(b => OutputBlock.SetLightbar(b, 9, 8, 7));

		Assert.False(session.IsWired);
		Assert.Equal("usb write failed", Assert.Single(lost));
		Assert.True(bt.Writes.Count > btWrites);
		byte[] last = bt.Writes.Last();
		Assert.Equal(BluetoothReports.OutputId, last[0]);
		Assert.Equal(9, last[3 + OutputBlock.Red]);
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

	// A USB link whose reads never return must not freeze input: Bluetooth keeps carrying it until the first USB
	// report, and once WiredSilentTimeout has passed the next Bluetooth report falls the session back.
	[Fact]
	public void ASilentUsbLink_BluetoothInputKeepsFlowing_ThenFallsBackAfterTheTimeout()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt) { WiredSilentTimeout = TimeSpan.FromSeconds(10) };
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
		List<string> lost = [];
		session.WiredLost += r =>
		{
			lock (lost)
			{
				lost.Add(r);
			}
		};
		long btWrites = bt.Writes.Count;

		// No USB report yet: a Bluetooth report is published (Bluetooth carries input until the link starts).
		bt.Feed(FakeLink.Input(77, 2));
		WaitUntil(() => { lock (bodies) return bodies.Count == 1; });
		Assert.Equal(77, bodies[0][4]);
		Assert.True(session.IsWired);

		// Past the timeout the next Bluetooth report falls the session back: the report is published and the
		// output state is resent on Bluetooth.
		session.WiredSilentTimeout = TimeSpan.FromMilliseconds(50);
		Thread.Sleep(80);
		bt.Feed(FakeLink.Input(88, 3));
		WaitUntil(() => !session.IsWired);
		WaitUntil(() => { lock (bodies) return bodies.Count == 2; });
		Assert.Equal(88, bodies[1][4]);
		WaitUntil(() => { lock (lost) return lost.Count == 1; });
		Assert.Equal("usb silent", lost[0]);
		WaitUntil(() => bt.Writes.Count > btWrites);
		Assert.Equal(BluetoothReports.OutputId, bt.Writes.Last()[0]);
	}

	// A USB link that starts late: Bluetooth carries input until the first USB report; Bluetooth reports after it
	// are counted and dropped.
	[Fact]
	public void ALateUsbLink_BluetoothCarriesInput_UntilTheFirstUsbReport()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt) { WiredSilentTimeout = TimeSpan.FromSeconds(10) };
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

	// A wired link that worked and then went silent: the next Bluetooth report after the timeout falls back.
	[Fact]
	public void AUsbLinkGoneSilent_FallsBack_OnTheNextBluetoothReportAfterTheTimeout()
	{
		FakeLink bt = new();
		using BluetoothSession session = new(bt) { WiredSilentTimeout = TimeSpan.FromMilliseconds(50) };
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
		List<string> lost = [];
		session.WiredLost += r =>
		{
			lock (lost)
			{
				lost.Add(r);
			}
		};

		usb.Feed(UsbInput(55, 3));
		WaitUntil(() => { lock (bodies) return bodies.Count == 1; });
		Assert.Equal(55, bodies[0][4]);
		Assert.True(session.IsWired);

		Thread.Sleep(80);
		bt.Feed(FakeLink.Input(99, 4));
		WaitUntil(() => !session.IsWired);
		WaitUntil(() => { lock (bodies) return bodies.Count == 2; });
		Assert.Equal(99, bodies[1][4]);
		WaitUntil(() => { lock (lost) return lost.Count == 1; });
		Assert.Equal("usb silent", lost[0]);
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

	// Once the endpoint plays, the controller gets the output state over USB again: right at the run's first
	// block and once more at the WiredOutputResendBlock-th write (the haptics-mode flags reach it while the
	// USB audio endpoint is surely streaming); each is logged once.
	[Fact]
	public void WiredAudio_WhenTheStreamPlaysOnTheSink_TheOutputStateGoesOutAgainOverUsb()
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
			Assert.Equal(1, session.WiredOutputLogs);
			int outputs = usb.Writes.Count(w => w[0] == UsbReports.OutputId);

			// The mic keeps the stream running (and the sink writing) while nothing is pushed.
			session.MicStreamEnabled = true;
			WaitUntil(() => { lock (sink.Writes) return sink.Writes.Count >= 17; });
			// Exactly two more 0x02 reports went out during this run (write 1 and write 16), both flagged.
			WaitUntil(() => usb.Writes.Count(w => w[0] == UsbReports.OutputId) == outputs + 2);
			WaitUntil(() => session.WiredOutputLogs == 2);
			byte[][] resends = [.. usb.Writes.Where(w => w[0] == UsbReports.OutputId).TakeLast(2)];
			Assert.All(resends, r => Assert.Equal(
				OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl,
				r[1 + OutputBlock.Flag1] & (OutputBlock.PowerSaveControl | OutputBlock.HapticsLowPass | OutputBlock.MotorPowerControl)));
			// No further resend while the same run keeps writing.
			Thread.Sleep(150);
			Assert.Equal(outputs + 2, usb.Writes.Count(w => w[0] == UsbReports.OutputId));
			List<string> wired;
			lock (lines)
			{
				wired = [.. lines];
			}
			Assert.Contains(wired, l => l.Contains("(haptics playing)", StringComparison.Ordinal));
		}
		finally
		{
			Diagnostics.AppLog.Written -= Watch;
		}
	}
}
