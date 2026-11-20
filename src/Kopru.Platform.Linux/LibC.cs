// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kopru.Platform.Linux;

// The few libc calls the Linux platform needs (man pages read(2), write(2), ioctl(2), poll(2), geteuid(2), access(2)).
// "libc" is looked up as is first and then as "libc.so.6" (glibc's soname), since some systems have no plain libc.so
// that dlopen can load.
[SupportedOSPlatform("linux")]
internal static unsafe class LibC
{
	public const string Library = "libc";

	public const string GlibcSoname = "libc.so.6";

	public const short PollIn = 0x001;
	public const short PollErr = 0x008;
	public const short PollHup = 0x010;
	public const short PollNval = 0x020;

	public const int EINTR = 4;

	static LibC()
	{
		NativeLibrary.SetDllImportResolver(typeof(LibC).Assembly, Resolve);
	}

	private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
	{
		if (name != Library)
		{
			return IntPtr.Zero;
		}
		if (NativeLibrary.TryLoad(Library, out IntPtr handle) || NativeLibrary.TryLoad(GlibcSoname, out handle))
		{
			return handle;
		}
		return IntPtr.Zero;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct PollFd
	{
		public int Fd;
		public short Events;
		public short Revents;
	}

	[DllImport(Library, EntryPoint = "read", SetLastError = true)]
	public static extern nint Read(int fd, byte* buffer, nuint count);

	[DllImport(Library, EntryPoint = "write", SetLastError = true)]
	public static extern nint Write(int fd, byte* buffer, nuint count);

	[DllImport(Library, EntryPoint = "ioctl", SetLastError = true)]
	public static extern int Ioctl(int fd, nuint request, byte* argument);

	[DllImport(Library, EntryPoint = "poll", SetLastError = true)]
	public static extern int Poll(PollFd* fds, nuint count, int timeoutMs);

	[DllImport(Library, EntryPoint = "geteuid")]
	public static extern uint GetEffectiveUserId();

	public const int ReadOk = 4;
	public const int WriteOk = 2;

	// access(2) with the real user id: 0 when allowed.
	[DllImport(Library, EntryPoint = "access", SetLastError = true)]
	public static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
