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
    /// <summary>Optional step-by-step diagnostic sink for <see cref="Start"/> —
    /// set by a caller that wants visibility into exactly what each Win32 call
    /// returned (HRESULTs, handle values, Win32 errors) without a debugger. Null
    /// by default; every call site is a null-conditional, so there's no overhead
    /// when unset.</summary>
    public Action<string>? Diagnostics { get; set; }

    public IPseudoConsoleSession Start(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string?> environmentOverrides,
        short columns = 160, short rows = 48)
    {
        void Log(string s) => Diagnostics?.Invoke(s);

        // 1. A pipe pair for the console's INPUT: we write to inputWrite, the
        // pseudo-console reads from inputRead and hands it to the child as stdin.
        if (!NativeMethods.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
        {
            var err = Marshal.GetLastWin32Error();
            Log($"CreatePipe (input) FAILED: Win32 error {err}");
            throw new InvalidOperationException("Failed to create the ConPTY input pipe.");
        }
        Log($"Input pipe: read=0x{inputRead.DangerousGetHandle():X}, write=0x{inputWrite.DangerousGetHandle():X}");

        // 2. A pipe pair for the console's OUTPUT: the child's stdout/stderr land
        // in outputWrite via the pseudo-console, we read the terminal stream from
        // outputRead.
        if (!NativeMethods.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            var err = Marshal.GetLastWin32Error();
            Log($"CreatePipe (output) FAILED: Win32 error {err}");
            inputRead.Dispose(); inputWrite.Dispose();
            throw new InvalidOperationException("Failed to create the ConPTY output pipe.");
        }
        Log($"Output pipe: read=0x{outputRead.DangerousGetHandle():X}, write=0x{outputWrite.DangerousGetHandle():X}");

        var size = new NativeMethods.COORD { X = columns, Y = rows };
        var hr = NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var hPc);
        Log($"CreatePseudoConsole: hr=0x{hr:X8}, hPc=0x{hPc:X}, size={columns}x{rows}");
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
            Log($"Attribute list allocated at 0x{attributeList:X}, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE set to hPc=0x{hPc:X}");

            var startupInfo = new NativeMethods.STARTUPINFOEX
            {
                StartupInfo = new NativeMethods.STARTUPINFO { cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>() },
                lpAttributeList = attributeList,
            };
            Log($"STARTUPINFOEX.StartupInfo.cb = {startupInfo.StartupInfo.cb} (sizeof STARTUPINFOEX)");

            var commandLineText = NativeMethods.BuildCommandLine(fileName, arguments);
            Log($"Command line: {commandLineText}");
            var commandLine = new StringBuilder(commandLineText);
            var envBlock = NativeMethods.BuildEnvironmentBlock(environmentOverrides);
            Log($"Environment block: {(envBlock is null ? "null (inherit)" : $"{envBlock.Length} bytes")}");
            var envHandle = envBlock is null ? default : GCHandle.Alloc(envBlock, GCHandleType.Pinned);
            try
            {
                var creationFlags = NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_UNICODE_ENVIRONMENT;
                Log($"CreateProcess: workingDirectory={workingDirectory}, creationFlags=0x{creationFlags:X8}");
                var ok = NativeMethods.CreateProcess(
                    null, commandLine, IntPtr.Zero, IntPtr.Zero, false, creationFlags,
                    envBlock is null ? IntPtr.Zero : envHandle.AddrOfPinnedObject(),
                    workingDirectory, ref startupInfo, out var processInfo);
                if (!ok)
                {
                    var error = Marshal.GetLastWin32Error();
                    Log($"CreateProcess FAILED: Win32 error {error}");
                    throw new InvalidOperationException($"CreateProcess failed for '{fileName}' (Win32 error {error}).");
                }
                Log($"CreateProcess OK: pid={processInfo.dwProcessId}, hProcess=0x{processInfo.hProcess:X}");

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

    private int _pseudoConsoleClosed;

    /// <summary>The output pipe does NOT naturally EOF when the child process
    /// exits: conhost (which ConPTY spins up internally) keeps its write handle
    /// to the pipe open until <c>ClosePseudoConsole</c> is called — a pseudo
    /// console can outlive any one attached process, by design. Left alone, a
    /// plain "run a command and read its output" reader would hang forever
    /// after the child exits, waiting for a pipe close that never comes. So:
    /// race reading against the process actually exiting, and once it has,
    /// force the pseudo console closed (after a short grace delay to let
    /// already-buffered output drain) to unblock the pending read.</summary>
    public async IAsyncEnumerable<string> ReadOutputLinesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(_output, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        _ = WatchForExitAndUnblockReadAsync();

        while (true)
        {
            string? line = null;
            var unblockedByExit = false;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (IOException) when (Volatile.Read(ref _pseudoConsoleClosed) != 0)
            {
                unblockedByExit = true;
            }
            if (unblockedByExit || line is null) yield break;
            yield return line;
        }
    }

    private async Task WatchForExitAndUnblockReadAsync()
    {
        try
        {
            await WaitForExitAsync(CancellationToken.None);
            await Task.Delay(200);
        }
        catch
        {
            // Best-effort watcher — a failure here just means the read loop
            // relies on natural EOF (or the caller's own cancellation) instead.
            return;
        }
        CloseUnderlyingPseudoConsoleOnce();
    }

    private void CloseUnderlyingPseudoConsoleOnce()
    {
        if (Interlocked.Exchange(ref _pseudoConsoleClosed, 1) == 0)
        {
            NativeMethods.ClosePseudoConsole(_hPc);
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
        CloseUnderlyingPseudoConsoleOnce();
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

        // UpdateProcThreadAttribute's lpValue for PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE is
        // the HPCON handle VALUE itself, not a pointer to a variable holding it — this
        // is what Microsoft's own ConPTY sample (and every other correct binding, e.g.
        // hcsshim's Go port) does. HPCON is already pointer-sized, so passing a pointer
        // TO it (one extra level of indirection) hands the kernel a bogus pseudo-console
        // reference: CreateProcess still succeeds (the attribute list is structurally
        // valid), but the child fails during its own startup trying to attach console
        // I/O through it — this reproduces as STATUS_DLL_INIT_FAILED (0xC0000142).
        if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hPc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            throw new InvalidOperationException("UpdateProcThreadAttribute failed.");
        }
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
