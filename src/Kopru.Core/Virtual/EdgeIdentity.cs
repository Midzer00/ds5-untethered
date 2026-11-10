// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Virtual;

// The DualSense Edge identity shipped inside the app (Virtual/Identity/dualsense-edge.json, built from public data; see
// the README there) and the choice of identity for the connected controller.
public static class EdgeIdentity
{
	public const string ResourceName = "Kopru.Core.Virtual.Identity.dualsense-edge.json";

	public const int ProductId = 0x0DF2;

	private static readonly Lazy<DeviceIdentity> Embedded = new(LoadEmbedded);

	public static DeviceIdentity Load() => Embedded.Value;

	// The identity games see: the Edge one for an Edge presented as an Edge, else the given (DualSense) identity.
	public static DeviceIdentity Choose(DeviceIdentity identity, bool controllerIsEdge, bool presentAsEdge) =>
		controllerIsEdge && presentAsEdge ? Load() : identity;

	private static DeviceIdentity LoadEmbedded()
	{
		using Stream stream = typeof(EdgeIdentity).Assembly.GetManifestResourceStream(ResourceName)
			?? throw new InvalidOperationException(ResourceName + " is missing from the assembly.");
		using StreamReader reader = new(stream);
		return DeviceIdentity.FromCaptureJson(reader.ReadToEnd());
	}
}
