using System.Runtime.InteropServices;

namespace CopilotCliIde;

// P/Invoke wrapper for Windows ConPTY (pseudo-console) APIs.
// Requires Windows 10 1809+ (build 17763).
internal static class ConPty
{
	// --- Structures ---

	[StructLayout(LayoutKind.Sequential)]
	public struct COORD
	{
		public short X;
		public short Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct SECURITY_ATTRIBUTES
	{
		public int nLength;
		public IntPtr lpSecurityDescriptor;
		public int bInheritHandle;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct STARTUPINFO
	{
		public int cb;
		public string? lpReserved;
		public string? lpDesktop;
		public string? lpTitle;
		public int dwX;
		public int dwY;
		public int dwXSize;
		public int dwYSize;
		public int dwXCountChars;
		public int dwYCountChars;
		public int dwFillAttribute;
		public int dwFlags;
		public short wShowWindow;
		public short cbReserved2;
		public IntPtr lpReserved2;
		public IntPtr hStdInput;
		public IntPtr hStdOutput;
		public IntPtr hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct STARTUPINFOEX
	{
		public STARTUPINFO StartupInfo;
		public IntPtr lpAttributeList;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct PROCESS_INFORMATION
	{
		public IntPtr hProcess;
		public IntPtr hThread;
		public int dwProcessId;
		public int dwThreadId;
	}

	// --- Constants ---

	private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
	private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
	private const int STARTF_USESTDHANDLES = 0x00000100;

	// --- P/Invoke Declarations ---

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern void ClosePseudoConsole(IntPtr hPC);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr hObject);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

	// --- Session ---

	// Holds all handles for a ConPTY session. Dispose in order to avoid leaks.
	internal sealed class Session : IDisposable
	{
		public IntPtr PseudoConsoleHandle { get; set; }
		public IntPtr ProcessHandle { get; set; }
		public IntPtr ThreadHandle { get; set; }
		public IntPtr InputWriteHandle { get; set; }
		public IntPtr OutputReadHandle { get; set; }
		public int ProcessId { get; set; }

		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;

			// Order matters: close pseudo-console first (signals EOF to process),
			// then close pipes, then close process/thread handles.
			if (PseudoConsoleHandle != IntPtr.Zero)
				ClosePseudoConsole(PseudoConsoleHandle);

			if (InputWriteHandle != IntPtr.Zero)
				CloseHandle(InputWriteHandle);

			if (OutputReadHandle != IntPtr.Zero)
				CloseHandle(OutputReadHandle);

			if (ProcessHandle != IntPtr.Zero)
			{
				WaitForSingleObject(ProcessHandle, 3000);
				CloseHandle(ProcessHandle);
			}

			if (ThreadHandle != IntPtr.Zero)
				CloseHandle(ThreadHandle);
		}
	}

	// Creates a new ConPTY session: pseudo-console + child process.
	public static Session Create(string command, string? workingDirectory, short cols, short rows)
	{
		var sa = new SECURITY_ATTRIBUTES
		{
			nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
			bInheritHandle = 1
		};

		// Create input pipe (we write → process reads)
		if (!CreatePipe(out var inputReadHandle, out var inputWriteHandle, ref sa, 0))
			throw new InvalidOperationException($"CreatePipe (input) failed: {Marshal.GetLastWin32Error()}");

		// Create output pipe (process writes → we read)
		if (!CreatePipe(out var outputReadHandle, out var outputWriteHandle, ref sa, 0))
		{
			CloseHandle(inputReadHandle);
			CloseHandle(inputWriteHandle);
			throw new InvalidOperationException($"CreatePipe (output) failed: {Marshal.GetLastWin32Error()}");
		}

		// Create pseudo-console
		var size = new COORD { X = cols, Y = rows };
		var hr = CreatePseudoConsole(size, inputReadHandle, outputWriteHandle, 0, out var hPC);
		if (hr != 0)
		{
			CloseHandle(inputReadHandle);
			CloseHandle(inputWriteHandle);
			CloseHandle(outputReadHandle);
			CloseHandle(outputWriteHandle);
			throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");
		}

		// Close the pipe ends the pseudo-console now owns
		CloseHandle(inputReadHandle);
		CloseHandle(outputWriteHandle);

		// Set up process attribute list with pseudo-console
		var attrListSize = IntPtr.Zero;
		InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
		var attrList = Marshal.AllocHGlobal(attrListSize);

		try
		{
			if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
				throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

			if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
				throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");

			var startupInfo = new STARTUPINFOEX
			{
				StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
				lpAttributeList = attrList
			};

			if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDirectory, ref startupInfo, out var processInfo))
				throw new InvalidOperationException($"CreateProcessW failed: {Marshal.GetLastWin32Error()}");

			return new Session
			{
				PseudoConsoleHandle = hPC,
				ProcessHandle = processInfo.hProcess,
				ThreadHandle = processInfo.hThread,
				InputWriteHandle = inputWriteHandle,
				OutputReadHandle = outputReadHandle,
				ProcessId = processInfo.dwProcessId
			};
		}
		catch
		{
			ClosePseudoConsole(hPC);
			CloseHandle(inputWriteHandle);
			CloseHandle(outputReadHandle);
			throw;
		}
		finally
		{
			DeleteProcThreadAttributeList(attrList);
			Marshal.FreeHGlobal(attrList);
		}
	}

	// Reads from the pseudo-console output pipe. Returns bytes read, or 0 on EOF.
	public static int Read(IntPtr outputReadHandle, byte[] buffer)
	{
		if (!ReadFile(outputReadHandle, buffer, (uint)buffer.Length, out var bytesRead, IntPtr.Zero))
			return 0;
		return (int)bytesRead;
	}

	// Writes to the pseudo-console input pipe.
	public static void Write(IntPtr inputWriteHandle, byte[] data, int count)
	{
		WriteFile(inputWriteHandle, data, (uint)count, out _, IntPtr.Zero);
	}

	// Resizes the pseudo-console.
	public static void Resize(IntPtr pseudoConsoleHandle, short cols, short rows)
	{
		var size = new COORD { X = cols, Y = rows };
		ResizePseudoConsole(pseudoConsoleHandle, size);
	}
}
