// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Bridge;

// Health of a running connection beyond the input timeout: the USB/IP server must still accept, and a haptics stream
// that is running must keep sending reports. Check returns the reason when the connection should be torn down.
public sealed class BridgeWatchdog(TimeSpan stallTimeout, DateTimeOffset start)
{
	public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(3);

	private long lastHaptics = -1;

	private DateTimeOffset lastProgress = start;

	public TimeSpan StallTimeout { get; } = stallTimeout;

	public string? Check(DateTimeOffset now, bool serverAlive, bool hapticsStreaming, long hapticsReports)
	{
		if (!serverAlive)
		{
			return "usbip server socket closed";
		}
		if (!hapticsStreaming || hapticsReports != lastHaptics)
		{
			lastHaptics = hapticsReports;
			lastProgress = now;
			return null;
		}
		if (StallTimeout >= TimeSpan.Zero && now - lastProgress > StallTimeout)
		{
			return string.Create(CultureInfo.InvariantCulture, $"haptics thread made no progress for {(now - lastProgress).TotalSeconds:F1} s");
		}
		return null;
	}
}
