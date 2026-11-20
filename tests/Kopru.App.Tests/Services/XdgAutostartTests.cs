// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;

namespace Kopru.App.Tests;

public class XdgAutostartTests
{
	[Fact]
	public void On_WritesTheDesktopEntry_Off_RemovesIt()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-autostart-" + Guid.NewGuid().ToString("N"));
		try
		{
			XdgAutostartRegistration startup = new(dir, () => "/opt/ds5 untethered/Kopru.App");
			startup.Set(true);
			string text = File.ReadAllText(startup.FilePath);
			Assert.Contains("[Desktop Entry]", text);
			Assert.Contains("Exec=\"/opt/ds5 untethered/Kopru.App\" --minimized", text);
			Assert.Contains("Name=DS5 Untethered", text);
			startup.Set(false);
			Assert.False(File.Exists(startup.FilePath));
			// Off again with nothing there is fine.
			startup.Set(false);
		}
		finally
		{
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, true);
			}
		}
	}

	[Fact]
	public void Quote_EscapesTheSpecialCharacters()
	{
		Assert.Equal("\"/a/\\$b\\\"c\"", XdgAutostartRegistration.Quote("/a/$b\"c"));
		// A lone % would start a field code.
		Assert.Equal("\"/games/100%%/Kopru.App\"", XdgAutostartRegistration.Quote("/games/100%/Kopru.App"));
	}
}
