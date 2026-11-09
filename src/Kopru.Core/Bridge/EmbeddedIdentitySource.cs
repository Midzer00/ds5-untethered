// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// The DualSense identity shipped inside the app (Virtual/Identity/dualsense.json): works on any PC without a capture.
// TODO: there is no DualSense Edge capture yet, so Edge controllers are presented with this DualSense identity too.
public sealed class EmbeddedIdentitySource : IIdentitySource
{
	public const string ResourceName = "Kopru.Core.Virtual.Identity.dualsense.json";

	public DeviceIdentity? Load()
	{
		using Stream stream = typeof(EmbeddedIdentitySource).Assembly.GetManifestResourceStream(ResourceName)
			?? throw new InvalidOperationException(ResourceName + " is missing from the assembly.");
		using StreamReader reader = new(stream);
		return DeviceIdentity.FromCaptureJson(reader.ReadToEnd());
	}
}

// The first source that has an identity.
public sealed class FirstIdentitySource(params IIdentitySource[] sources) : IIdentitySource
{
	public IReadOnlyList<IIdentitySource> Sources { get; } = sources;

	public DeviceIdentity? Load()
	{
		foreach (IIdentitySource source in Sources)
		{
			DeviceIdentity? identity = source.Load();
			if (identity != null)
			{
				return identity;
			}
		}
		return null;
	}
}

public static class IdentitySources
{
	// The user's own capture in %LOCALAPPDATA%\Untethered\captures when there is one, else the built-in identity.
	public static IIdentitySource Default() => new FirstIdentitySource(new CaptureIdentitySource(AppPaths.Captures), new EmbeddedIdentitySource());
}
