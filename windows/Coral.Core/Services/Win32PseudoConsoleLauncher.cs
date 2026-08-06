using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Coral.Core.Services;

/// <summary>
/// The only <see cref="IPseudoConsoleLauncher"/> that touches a real Windows
/// pseudo-console (ConPTY) — raw P/Invoke against kernel32, following the
/// sequence Microsoft's own ConPTY sample documents: two pipes (one for the
/// console's input, one for its output), <c>CreatePseudoConsole</c> over them,
/// an extended STARTUPINFO carrying the pseudo-console as a proc-thread
/// attribute, then <c>CreateProcess</c>.
///
/// UNLIKE <see cref="RealProcessLauncher"/> (a thin wrapper over the
/// cross-platform <see cref="System.Diagnostics.Process"/>, smoke-tested for
/// real on Linux by spawning `dotnet`), there is no cross-platform equivalent
/// to smoke-test here — ConPTY is a Windows-only kernel32 API with nothing
/// analogous available in this sandbox. This class compiles clean but has
/// NOT executed successfully anywhere yet. See windows/README.md
/// "Testing against the real `claude` CLI" for
/// <c>ManualPseudoConsoleSmokeTest</c> — the first real exercise of this code
/// needs to happen on a real Windows machine, the same way ClaudeRunner's did.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32PseudoConsoleLauncher : IPseudoConsoleLauncher
{
    public IPseudoConsoleSession Start(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string?> environmentOverrides,
        short columns = 160, short rows = 48)
    {
        // 1. A pipe pair for the console's INPUT: we write to inputWrite, the
        // pseudo-console reads from inputRead and hands it to the child as stdin.
        if (!NativeMethods.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
        {
            throw new InvalidOperationException("Failed to create the ConPTY input pipe.");
        }
        // 2. A pipe pair for the console's OUTPUT: the child's stdout/stderr land
        // in outputWrite via the pseudo-console, we read the terminal stream from
        // outputRead.
        if (!NativeMethods.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            inputRead.Dispose(); inputWrite.Dispose();
            throw new InvalidOperationException("Failed to create the ConPTY output pipe.");
        }

        var size = new NativeMethods.COORD { X = columns, Y = rows };
        var hr = NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var hPc);
        // CreatePseudoConsole duplicates the handles it needs; our copies of the
        // "far" ends are no longer ours to hold once it succeeds.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0)
        {
            inputWrite.Dispose(); outputRead.Dispose();
            throw new InvalidOperationException($"CreatePseudoConsole failed (HRESULT 0x{hr:X8}).");
        }

        IntPtr attributeList = IntPtr.Zero;
        try
        {
            attributeList = NativeMethods.CreateAndInitializeAttributeListForPseudoConsole(hPc);

            var startupInfo = new NativeMethods.STARTUPINFOEX
            {
                StartupInfo = new NativeMethods.STARTUPINFO { cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>() },
                lpAttributeList = attributeList,
            };

            var commandLine = new StringBuilder(NativeMethods.BuildCommandLine(fileName, arguments));
            var envBlock = NativeMethods.BuildEnvironmentBlock(environmentOverrides);
            var envHandle = envBlock is null ? default : GCHandle.Alloc(envBlock, GCHandleType.Pinned);
            try
            {
                var creationFlags = NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_UNICODE_ENVIRONMENT;
                var ok = NativeMethods.CreateProcess(
                    null, commandLine, IntPtr.Zero, IntPtr.Zero, false, creationFlags,
                    envBlock is null ? IntPtr.Zero : envHandle.AddrOfPinnedObject(),
                    workingDirectory, ref startupInfo, out var processInfo);
                if (!ok)
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException($"CreateProcess failed for '{fileName}' (Win32 error {error}).");
                }

                NativeMethods.CloseHandle(processInfo.hThread);
                return new Win32PseudoConsoleSession(hPc, attributeList, processInfo.hProcess,
                    processInfo.dwProcessId, inputWrite, outputRead);
            }
            finally
            {
                if (envHandle.IsAllocated) envHandle.Free();
            }
        }
        catch
        {
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            NativeMethods.ClosePseudoConsole(hPc);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class Win32PseudoConsoleSession : IPseudoConsoleSession
{
    private readonly IntPtr _hPc;
    private readonly IntPtr _attributeList;
    private readonly IntPtr _hProcess;
    private readonly int _processId;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private readonly FileStream _input;
    private readonly FileStream _output;

    public Win32PseudoConsoleSession(IntPtr hPc, IntPtr attributeList, IntPtr hProcess, int processId,
        SafeFileHandle inputWrite, SafeFileHandle outputRead)
    {
        _hPc = hPc;
        _attributeList = attributeList;
        _hProcess = hProcess;
        _processId = processId;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _input = new FileStream(_inputWrite, FileAccess.Write);
        _output = new FileStream(_outputRead, FileAccess.Read);
    }

    public async IAsyncEnumerable<string> ReadOutputLinesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(_output, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            yield return line;
        }
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line.TrimEnd('\r', '\n') + "\r\n");
        await _input.WriteAsync(bytes, ct);
        await _input.FlushAsync(ct);
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        // System.Diagnostics.Process has no "wrap an existing handle" API that
        // exposes WaitForExitAsync, so poll GetExitCodeProcess — the ConPTY
        // sample code does the same rather than duplicating a wait-handle wrapper.
        while (true)
        {
            if (NativeMethods.GetExitCodeProcess(_hProcess, out var exitCode)
                && exitCode != NativeMethods.STILL_ACTIVE)
            {
                return unchecked((int)exitCode);
            }
            ct.ThrowIfCancellationRequested();
            await Task.Delay(50, ct);
        }
    }

    public void Kill()
    {
        try { NativeMethods.TerminateProcess(_hProcess, 1); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
        if (_attributeList != IntPtr.Zero)
        {
            NativeMethods.DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
        }
        NativeMethods.ClosePseudoConsole(_hPc);
        if (_hProcess != IntPtr.Zero) NativeMethods.CloseHandle(_hProcess);
    }
}

/// <summary>Raw kernel32 P/Invoke declarations for ConPTY, and the small,
/// pure helpers (command-line quoting, environment block layout) that don't
/// need a real Windows process to be correct — kept separate so those two
/// could, in principle, be unit-tested apart from the P/Invoke calls
/// themselves.</summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    public const int STILL_ACTIVE = 259;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll")]
    public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput,
        uint dwFlags, out IntPtr phPc);

    [DllImport("kernel32.dll")]
    public static extern void ClosePseudoConsole(IntPtr hPc);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
        int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Allocates and populates a proc-thread attribute list carrying
    /// just the pseudo-console handle — the two-call InitializeProcThreadAttributeList
    /// dance (first to size the buffer, then to actually initialize it) that
    /// every ConPTY sample needs.</summary>
    public static IntPtr CreateAndInitializeAttributeListForPseudoConsole(IntPtr hPc)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(attributeList);
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed.");
        }

        var hPcPtr = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(hPcPtr, hPc);
        if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hPcPtr, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            Marshal.FreeHGlobal(hPcPtr);
            throw new InvalidOperationException("UpdateProcThreadAttribute failed.");
        }
        // Intentionally leaked (freed alongside attributeList in the session's
        // Dispose): CreateProcess reads through this pointer, so it must outlive
        // the call, and there's no attribute-list "teardown" callback to free it
        // from — same tradeoff the official ConPTY sample makes.
        return attributeList;
    }

    /// <summary>Quote each argument for CreateProcess's single command-line
    /// string per the documented Win32 rules (backslash/quote escaping ahead of
    /// a literal double quote), the same care <c>ArgumentList</c> takes for
    /// <see cref="System.Diagnostics.Process"/> — CreateProcess has no
    /// <c>ArgumentList</c> equivalent, so this reimplements the same contract.</summary>
    public static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        AppendArgument(sb, fileName);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            AppendArgument(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            sb.Append(argument);
            return;
        }
        sb.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == argument.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (argument[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(argument[i]);
            }
        }
        sb.Append('"');
    }

    /// <summary>A CreateProcess-shaped environment block: <c>KEY=value\0</c>
    /// entries, double-null-terminated, sorted (Windows requires a
    /// case-insensitive sorted block when <c>CREATE_UNICODE_ENVIRONMENT</c> is
    /// set). Merges <paramref name="overrides"/> on top of the CURRENT process's
    /// inherited environment — a null value removes that key.</summary>
    public static byte[]? BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            merged[(string)entry.Key] = (string?)entry.Value ?? "";
        }
        foreach (var (key, value) in overrides)
        {
            if (value is null) merged.Remove(key);
            else merged[key] = value;
        }

        var sb = new StringBuilder();
        foreach (var key in merged.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(key).Append('=').Append(merged[key]).Append('\0');
        }
        sb.Append('\0');
        return Encoding.Unicode.GetBytes(sb.ToString());
    }
}
