// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Input;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

public class EdgeIdentityTests
{
	private static DeviceIdentity DualSense() => new EmbeddedIdentitySource().Load()!;

	private static string RepoFile(params string[] parts)
	{
		for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
		{
			string path = Path.Combine([dir.FullName, .. parts]);
			if (File.Exists(path))
			{
				return path;
			}
		}
		throw new FileNotFoundException(Path.Combine(parts));
	}

	private static SubmitResult Run(VirtualDualSense pad, byte[] setup, byte[]? data = null)
	{
		SubmitResult? result = null;
		data ??= [];
		pad.Submit(new SubmitCommand(1, 0, data.Length > 0 ? 0u : 1u, 0, 0, data.Length > 0 ? data.Length : 255, 0, -1, 1, setup, data, []), r => result = r);
		return result!;
	}

	[Fact]
	public void Loads_WithEdgeIdsAndTheEdgeDescriptor()
	{
		DeviceIdentity edge = EdgeIdentity.Load();
		Assert.Equal(new byte[] { 0x4C, 0x05, 0xF2, 0x0D }, edge.DeviceDescriptor[8..12]);
		Assert.Equal(0, edge.DeviceDescriptor[16]);
		Assert.Equal("DualSense Edge Wireless Controller", edge.Strings[2]);
		Assert.Equal(405, edge.HidReportDescriptor.Length);
		// The public dump in references/edge, byte for byte.
		string dump = File.ReadLines(RepoFile("references", "edge", "usb_descriptor.txt")).First();
		Assert.Equal(Convert.FromHexString(string.Concat(dump.Split(' ', StringSplitOptions.RemoveEmptyEntries))), edge.HidReportDescriptor);
		// The first 109 bytes (input/output layout) are the DualSense's.
		Assert.Equal(DualSense().HidReportDescriptor[..109], edge.HidReportDescriptor[..109]);
	}

	[Fact]
	public void Configuration_IsTheDualSenseOneWithTheEdgeDescriptorLength()
	{
		DeviceIdentity edge = EdgeIdentity.Load(), ds = DualSense();
		byte[] hid = edge.HidDescriptor();
		Assert.Equal(9, hid.Length);
		Assert.Equal(0x22, hid[6]);
		Assert.Equal(edge.HidReportDescriptor.Length, hid[7] | (hid[8] << 8));
		Assert.Equal(ds.HidReportDescriptor.Length, ds.HidDescriptor()[7] | (ds.HidDescriptor()[8] << 8));
		Assert.Equal(edge.ConfigurationDescriptor.Length, edge.ConfigurationDescriptor[2] | (edge.ConfigurationDescriptor[3] << 8));
		Assert.Equal(ds.ConfigurationDescriptor.Length, edge.ConfigurationDescriptor.Length);
		int differences = Enumerable.Range(0, ds.ConfigurationDescriptor.Length).Count(i => ds.ConfigurationDescriptor[i] != edge.ConfigurationDescriptor[i]);
		Assert.Equal(1, differences); // wDescriptorLength 0x0121 -> 0x0195: only the low byte changes
		Assert.Equal(ds.Interfaces(), edge.Interfaces());
	}

	[Fact]
	public void FeatureReports_ReuseTheDualSenseOnesAndHaveNoEdgeProfiles()
	{
		DeviceIdentity edge = EdgeIdentity.Load(), ds = DualSense();
		Assert.Equal(ds.FeatureReports.Keys.Order(), edge.FeatureReports.Keys.Order());
		Assert.DoesNotContain(edge.FeatureReports.Keys, id => id is >= 0x60 and <= 0x7B);
		Assert.False(edge.FeatureReports.ContainsKey(CalibrationOverlay.Feature));
		Assert.False(edge.FeatureReports.ContainsKey(0x22));
		Assert.Equal(DeviceIdentity.VirtualAddress, edge.FeatureReports[DeviceIdentity.PairingFeature][1..7]);
		// Every feature report the identity answers is declared by the Edge descriptor (85 id ... b1 = Feature).
		foreach (byte id in edge.FeatureReports.Keys)
		{
			Assert.True(edge.HidReportDescriptor.AsSpan().IndexOf(new byte[] { 0x85, id }) >= 0, $"0x{id:X2}");
		}
	}

	[Fact]
	public void VirtualEdge_Enumerates_AndStallsTheProfileReports()
	{
		using VirtualDualSense pad = new(CalibrationOverlay.Apply(EdgeIdentity.Load(), new FeatureLink(() => new byte[64])));
		Assert.Equal((0x054C, 0x0DF2), (pad.Info.VendorId, pad.Info.ProductId));
		byte[] device = Run(pad, [0x80, 6, 0, 1, 0, 0, 18, 0]).Data;
		Assert.Equal(EdgeIdentity.Load().DeviceDescriptor, device);
		byte[] config = Run(pad, [0x80, 6, 0, 2, 0, 0, 255, 0]).Data;
		Assert.Equal(config[2] | (config[3] << 8), config.Length);
		// HID report descriptor (GET_DESCRIPTOR 0x22 on interface 3): the full 405 bytes.
		Assert.Equal(405, Run(pad, [0x81, 6, 0, 0x22, 3, 0, 0xFF, 0x01]).Data.Length);
		// Product string.
		byte[] product = Run(pad, [0x80, 6, 2, 3, 0x09, 0x04, 255, 0]).Data;
		Assert.Equal("DualSense Edge Wireless Controller", System.Text.Encoding.Unicode.GetString(product, 2, product.Length - 2));
		// Calibration overlay and common feature reports as on the DualSense.
		Assert.Equal(CalibrationOverlay.UsbLength, Run(pad, [0xA1, 1, 0x05, 3, 3, 0, 64, 0]).Data.Length);
		Assert.Equal(DeviceIdentity.VirtualAddress, Run(pad, [0xA1, 1, 0x09, 3, 3, 0, 64, 0]).Data[1..7]);
		Assert.Equal(0x20, Run(pad, [0xA1, 1, 0x20, 3, 3, 0, 64, 0]).Data[0]);
		// Edge-only profile reports: GET stalls, SET is accepted and dropped.
		foreach (byte id in new byte[] { 0x60, 0x65, 0x68, 0x70, 0x7B })
		{
			Assert.Equal(UsbIpCodes.BrokenPipe, Run(pad, [0xA1, 1, id, 3, 3, 0, 64, 0]).Status);
		}
		Assert.Equal(0, Run(pad, [0x21, 9, 0x60, 3, 3, 0, 64, 0], new byte[64]).Status);
	}

	[Fact]
	public void Choose_PresentsAnEdgeAsAnEdgeOnlyWhenAsked()
	{
		DeviceIdentity ds = DualSense();
		Assert.Same(EdgeIdentity.Load(), EdgeIdentity.Choose(ds, controllerIsEdge: true, presentAsEdge: true));
		Assert.Same(ds, EdgeIdentity.Choose(ds, controllerIsEdge: true, presentAsEdge: false));
		Assert.Same(ds, EdgeIdentity.Choose(ds, controllerIsEdge: false, presentAsEdge: true));
		Assert.True(RemapSettings.Default.PresentAsEdge);
	}
}

public class EdgePaddleRemapTests
{
	private static byte[] Body(byte buttons2)
	{
		var b = new byte[63];
		b[0] = b[1] = b[2] = b[3] = 128;
		b[7] = ButtonRemap.HatReleased;
		b[9] = buttons2;
		return b;
	}

	private static byte[] Apply(RemapSettings settings, byte buttons2)
	{
		byte[] body = Body(buttons2);
		new ButtonRemap(settings).Apply(body);
		return body;
	}

	[Fact]
	public void PresentedAsEdge_PaddlesPassThroughByDefault()
	{
		Assert.True(RemapSettings.Default.IsIdentity);
		Assert.Equal(DsButton.LeftPaddle, RemapSettings.Default.TargetOf(DsButton.LeftPaddle));
		Assert.Equal(0xC0, Apply(RemapSettings.Default, 0xC0)[9]);
	}

	[Fact]
	public void PresentedAsDualSense_UnmappedPaddlesDoNothing_FnIsKept()
	{
		RemapSettings ds = RemapSettings.Default with { PresentAsEdge = false };
		Assert.False(ds.IsIdentity);
		Assert.Equal(DsButton.None, ds.TargetOf(DsButton.RightPaddle));
		byte[] body = Apply(ds, 0xC0 | EdgePaddleRemap.LeftFunctionBit | EdgePaddleRemap.RightFunctionBit | 0x01);
		Assert.Equal(0x31, body[9]);
		Assert.Equal(ButtonRemap.HatReleased, body[7]);
		Assert.Equal(0, body[8]);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void MappedPaddles_PressTheirTarget(bool presentAsEdge)
	{
		RemapSettings s = (RemapSettings.Default with { PresentAsEdge = presentAsEdge })
			.With(DsButton.LeftPaddle, DsButton.Cross).With(DsButton.RightPaddle, DsButton.R2);
		byte[] body = Apply(s, EdgePaddleRemap.LeftBit | EdgePaddleRemap.RightBit);
		Assert.True(ButtonRemap.IsPressed(body, DsButton.Cross));
		Assert.True(ButtonRemap.IsPressed(body, DsButton.R2));
		Assert.Equal(255, body[5]);
		Assert.Equal(0, body[9] & 0xC0);
		// Released: nothing.
		byte[] idle = Apply(s, 0);
		Assert.False(ButtonRemap.IsPressed(idle, DsButton.Cross));
		Assert.Equal(0, idle[5]);
	}

	[Fact]
	public void PaddleOnDpad_MergesWithTheHat()
	{
		RemapSettings s = RemapSettings.Default.With(DsButton.LeftPaddle, DsButton.DpadUp).With(DsButton.RightPaddle, DsButton.DpadRight);
		byte[] body = Apply(s, EdgePaddleRemap.LeftBit | EdgePaddleRemap.RightBit);
		Assert.Equal(1, body[7] & ButtonRemap.HatMask); // up-right
	}

	[Fact]
	public void PaddleMapping_RunsAfterTheStandardRemap()
	{
		// Cross -> Circle must not turn the paddle's Cross into Circle.
		RemapSettings s = RemapSettings.Default.With(DsButton.Cross, DsButton.Circle).With(DsButton.LeftPaddle, DsButton.Cross);
		byte[] body = Apply(s, EdgePaddleRemap.LeftBit);
		Assert.True(ButtonRemap.IsPressed(body, DsButton.Cross));
		Assert.False(ButtonRemap.IsPressed(body, DsButton.Circle));
	}

	[Fact]
	public void With_DropsDefaultEntries_AndEqualityCoversPaddlesAndPresentation()
	{
		RemapSettings mapped = RemapSettings.Default.With(DsButton.LeftPaddle, DsButton.L1);
		Assert.NotEqual(RemapSettings.Default, mapped);
		Assert.Equal(RemapSettings.Default, mapped.With(DsButton.LeftPaddle, DsButton.LeftPaddle));
		Assert.Empty(mapped.With(DsButton.LeftPaddle, DsButton.LeftPaddle).Map);
		RemapSettings ds = RemapSettings.Default with { PresentAsEdge = false };
		Assert.NotEqual(RemapSettings.Default, ds);
		Assert.Empty(ds.With(DsButton.RightPaddle, DsButton.None).Map);
		Assert.Equal(ButtonRemap.Location(DsButton.LeftPaddle), (ButtonRemap.Buttons2, EdgePaddleRemap.LeftBit));
	}

	[Fact]
	public void Settings_RoundTripThroughJson()
	{
		var options = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
		RemapSettings s = (RemapSettings.Default with { PresentAsEdge = false }).With(DsButton.RightPaddle, DsButton.Triangle);
		RemapSettings back = System.Text.Json.JsonSerializer.Deserialize<RemapSettings>(System.Text.Json.JsonSerializer.Serialize(s, options), options)!;
		Assert.Equal(s, back);
		Assert.True(System.Text.Json.JsonSerializer.Deserialize<RemapSettings>("{}", options)!.PresentAsEdge);
	}
}
