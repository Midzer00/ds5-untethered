// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;
using Kopru.Core.Platform;

namespace Kopru.App.Tests;

public class PlatformSelectionTests
{
	[Fact]
	public void Windows_GetsTheWindowsPlatform_AndNeverLoadsTheLinuxAssembly()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}
		Assert.Same(WindowsPlatform.Instance, PlatformSelection.ForThisSystem());
		// Nothing in the App tests names a Linux type, so only the selection could have loaded it.
		Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "Kopru.Platform.Linux");
	}
}
