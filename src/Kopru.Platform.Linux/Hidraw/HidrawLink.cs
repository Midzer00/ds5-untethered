// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kopru.Core.Hid;
using Microsoft.Win32.SafeHandles;

namespace Kopru.Platform.Linux.Hidraw;

// An open DualSense through /dev/hidrawN (kernel Documentation/hid/hidraw.rst): read(2) returns one input report (with
// its report ID byte), write(2) sends one output report, and the HIDIOCGFEATURE/HIDIOCSFEATURE ioctls get and set feature
// reports. Reads and writes use separate descriptors, so a blocking read never holds up a write (as on Windows).
//
// Output reports: the shared code builds them at the device's longest output length (Windows needs that). hidraw sends
// exactly the bytes it is given, so writes are cut to the length the report descriptor declares for that report ID
// (TrimOutput; off sends the whole buffer as Windows does, for comparing on hardware). A write that sends fewer bytes
// than asked fails (IOException); it is never resent in parts, which would make two broken reports.
[SupportedOSPlatform("linux")]
public sealed class HidrawLink : IHidLink
{
	// A blocked read wakes this often to see whether it was cancelled.
	private const int PollMs = 100;

	private readonly HidrawNode node;

	private readonly SafeFileHandle readHandle;

	private readonly SafeFileHandle? writeHandle;

	private readonly object writeGate = new();

	private readonly object readGate = new();

	private int cancelRequested;

	private int disposed;

	private long writes, writeFailures, trimmedWrites;

	private HidrawLink(HidrawNode node, SafeFileHandle readHandle, SafeFileHandle? writeHandle)
	{
		this.node = node;
		this.readHandle = readHandle;
		this.writeHandle = writeHandle;
		Device = node.ToHidDevice();
	}

	public HidDevice Device { get; }

	public HidrawNode Node => node;

	public bool TrimOutput { get; set; } = true;

	public long Writes => Interlocked.Read(ref writes);

	public long WriteFailures => Interlocked.Read(ref writeFailures);

	public long TrimmedWrites => Interlocked.Read(ref trimmedWrites);

	// Throws UnauthorizedAccessException when the node is not readable and writable by this user (udev rule missing).
	public static HidrawLink Open(HidrawNode node)
	{
		SafeFileHandle read = OpenHandle(node.DevicePath, FileAccess.Read);
		try
		{
			return new HidrawLink(node, read, OpenHandle(node.DevicePath, FileAccess.Write));
		}
		catch
		{
			read.Dispose();
			throw;
		}
	}

	// Input reports and feature reads only; Write throws.
	public static HidrawLink OpenReadOnly(HidrawNode node) => new(node, OpenHandle(node.DevicePath, FileAccess.Read), null);

	private static SafeFileHandle OpenHandle(string path, FileAccess access) => File.OpenHandle(path, FileMode.Open, access, FileShare.ReadWrite);

	// Blocks until the next input report; returns its length (report ID first). Throws once cancelled or closed.
	public int Read(Span<byte> buffer)
	{
		while (true)
		{
			lock (readGate)
			{
				ThrowIfClosedOrCancelled();
				if (WaitReadable())
				{
					return ReadOnce(buffer);
				}
			}
		}
	}

	private unsafe int ReadOnce(Span<byte> buffer)
	{
		bool added = false;
		try
		{
			readHandle.DangerousAddRef(ref added);
			int fd = (int)readHandle.DangerousGetHandle();
			while (true)
			{
				nint read;
				fixed (byte* p = buffer)
				{
					read = LibC.Read(fd, p, (nuint)buffer.Length);
				}
				if (read > 0)
				{
					return (int)read;
				}
				if (read == 0)
				{
					throw new IOException("hidraw: end of file (device removed)");
				}
				int errno = Marshal.GetLastPInvokeError();
				if (errno != LibC.EINTR)
				{
					throw new IOException($"hidraw: read failed (errno {errno})");
				}
			}
		}
		finally
		{
			if (added)
			{
				readHandle.DangerousRelease();
			}
		}
	}

	private void ThrowIfClosedOrCancelled()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
		if (Interlocked.Exchange(ref cancelRequested, 0) != 0)
		{
			throw new OperationCanceledException("hidraw: read cancelled");
		}
	}

	// True when a report can be read now; false after PollMs without one.
	private unsafe bool WaitReadable()
	{
		bool added = false;
		try
		{
			readHandle.DangerousAddRef(ref added);
			LibC.PollFd fd = new() { Fd = (int)readHandle.DangerousGetHandle(), Events = LibC.PollIn };
			int ready = LibC.Poll(&fd, 1, PollMs);
			if (ready < 0)
			{
				int errno = Marshal.GetLastPInvokeError();
				if (errno == LibC.EINTR)
				{
					return false;
				}
				throw new IOException($"hidraw: poll failed (errno {errno})");
			}
			if (ready == 0)
			{
				return false;
			}
			if ((fd.Revents & (LibC.PollErr | LibC.PollHup | LibC.PollNval)) != 0 && (fd.Revents & LibC.PollIn) == 0)
			{
				throw new IOException("hidraw: device gone");
			}
			return true;
		}
		finally
		{
			if (added)
			{
				readHandle.DangerousRelease();
			}
		}
	}

	public void Write(ReadOnlySpan<byte> report)
	{
		if (writeHandle == null)
		{
			throw new NotSupportedException("Opened read-only");
		}
		ReadOnlySpan<byte> send = report;
		if (TrimOutput && report.Length > 0 && node.Descriptor?.ReportLength(HidReportKind.Output, report[0]) is int declared && declared < report.Length)
		{
			send = report[..declared];
			Interlocked.Increment(ref trimmedWrites);
		}
		lock (writeGate)
		{
			try
			{
				WriteOnce(writeHandle, send);
				Interlocked.Increment(ref writes);
			}
			catch (Exception)
			{
				Interlocked.Increment(ref writeFailures);
				throw;
			}
		}
	}

	private static unsafe void WriteOnce(SafeFileHandle handle, ReadOnlySpan<byte> report)
	{
		bool added = false;
		try
		{
			handle.DangerousAddRef(ref added);
			int fd = (int)handle.DangerousGetHandle();
			while (true)
			{
				nint written;
				fixed (byte* p = report)
				{
					written = LibC.Write(fd, p, (nuint)report.Length);
				}
				if (written == report.Length)
				{
					return;
				}
				if (written >= 0)
				{
					throw new IOException($"hidraw: short write ({written} of {report.Length} bytes)");
				}
				int errno = Marshal.GetLastPInvokeError();
				if (errno != LibC.EINTR)
				{
					throw new IOException($"hidraw: write failed (errno {errno})");
				}
			}
		}
		finally
		{
			if (added)
			{
				handle.DangerousRelease();
			}
		}
	}

	// Like HidD_GetFeature on Windows: a buffer of the device's feature length, report ID first, the rest zero.
	public byte[] GetFeature(byte reportId)
	{
		int length = node.Descriptor?.ReportLength(HidReportKind.Feature, reportId) ?? Math.Max(Device.FeatureLength, HidrawNode.DefaultFeatureLength);
		byte[] buffer = new byte[Math.Max(length, 2)];
		buffer[0] = reportId;
		int got = FeatureIoctl(HidrawIoctl.GetFeature(buffer.Length), buffer);
		byte[] result = new byte[Math.Max(Device.FeatureLength, got)];
		buffer.AsSpan(0, Math.Min(got, buffer.Length)).CopyTo(result);
		return result;
	}

	// Sends a feature report (report ID first). Not part of IHidLink; for tools and later use.
	public void SetFeature(ReadOnlySpan<byte> report)
	{
		byte[] buffer = report.ToArray();
		FeatureIoctl(HidrawIoctl.SetFeature(buffer.Length), buffer);
	}

	private unsafe int FeatureIoctl(uint request, byte[] buffer)
	{
		// The write descriptor when there is one (feature calls are serialised with output reports), else the read
		// descriptor: the kernel allows feature reads on a node opened read-only.
		SafeFileHandle handle = writeHandle ?? readHandle;
		object gate = writeHandle != null ? writeGate : readGate;
		lock (gate)
		{
			bool added = false;
			try
			{
				handle.DangerousAddRef(ref added);
				fixed (byte* p = buffer)
				{
					int result = LibC.Ioctl((int)handle.DangerousGetHandle(), request, p);
					if (result < 0)
					{
						int errno = Marshal.GetLastPInvokeError();
						throw new Win32Exception(errno, $"hidraw: feature ioctl 0x{request:X8} failed (errno {errno})");
					}
					return result;
				}
			}
			finally
			{
				if (added)
				{
					handle.DangerousRelease();
				}
			}
		}
	}

	// Unblocks a pending Read (it throws); a Read that starts right after throws too. No-op once disposed.
	public void CancelRead()
	{
		if (Volatile.Read(ref disposed) == 0)
		{
			Volatile.Write(ref cancelRequested, 1);
		}
	}

	// Safe to call more than once. Waits for a pending read to notice (at most PollMs).
	public void Dispose()
	{
		CancelRead();
		if (Interlocked.Exchange(ref disposed, 1) != 0)
		{
			return;
		}
		lock (readGate)
		{
			readHandle.Dispose();
		}
		lock (writeGate)
		{
			writeHandle?.Dispose();
		}
	}
}
