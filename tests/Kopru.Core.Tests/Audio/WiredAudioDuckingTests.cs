// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Reflection;
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// The wired render session opts out of Windows' communications ducking (it made USB haptics too weak to feel).
public class WiredAudioDuckingTests
{
	private sealed class FakeSession(int hr = 0, Exception? fail = null) : IDuckingPreference
	{
		public List<bool> Calls { get; } = [];

		public int SetDuckingPreference(bool optOut)
		{
			Calls.Add(optOut);
			if (fail != null)
			{
				throw fail;
			}
			return hr;
		}
	}

	[Fact]
	public void OptOut_AsksTheSessionToStayOutOfDucking_AndSaysSo()
	{
		FakeSession session = new();
		Assert.Equal(WiredAudioDucking.OptedOut, WiredAudioDucking.OptOut(() => session));
		Assert.Equal([true], session.Calls);
	}

	[Fact]
	public void OptOut_AFailedCall_IsReportedWithItsHresult_NeverThrown()
	{
		FakeSession session = new(unchecked((int)0x88890004));
		Assert.Equal("not opted out of Windows communications ducking (0x88890004)", WiredAudioDucking.OptOut(() => session));
	}

	[Fact]
	public void OptOut_NoSessionControl_OrAThrowingOne_IsReported_NeverThrown()
	{
		Assert.Equal("not opted out of Windows communications ducking (no session control)", WiredAudioDucking.OptOut(() => null));
		Assert.Equal("not opted out of Windows communications ducking (0x80004002)",
			WiredAudioDucking.OptOut(() => throw new COMException("no interface", unchecked((int)0x80004002))));
		Assert.Equal("not opted out of Windows communications ducking (InvalidCastException)",
			WiredAudioDucking.OptOut(() => new FakeSession(fail: new InvalidCastException("secret path"))));
	}

	// The COM declaration's slot order: SetDuckingPreference is the 14th method after IUnknown (9 IAudioSessionControl
	// methods, then GetSessionIdentifier, GetSessionInstanceIdentifier, GetProcessId, IsSystemSoundsSession).
	[Fact]
	public void SessionControl2_DeclaresSetDuckingPreferenceInItsVtableSlot()
	{
		Type? control = typeof(WiredAudioDucking).Assembly.GetType("Kopru.Core.Audio.WasapiRenderSink")?
			.GetNestedType("IAudioSessionControl2", BindingFlags.NonPublic);
		Assert.NotNull(control);
		string[] methods = [.. control!.GetMethods().OrderBy(m => m.MetadataToken).Select(m => m.Name)];
		Assert.Equal(14, methods.Length);
		Assert.Equal(["GetState", "GetDisplayName", "SetDisplayName", "GetIconPath", "SetIconPath", "GetGroupingParam",
			"SetGroupingParam", "RegisterAudioSessionNotification", "UnregisterAudioSessionNotification", "GetSessionIdentifier",
			"GetSessionInstanceIdentifier", "GetProcessId", "IsSystemSoundsSession", "SetDuckingPreference"], methods);
	}
}
