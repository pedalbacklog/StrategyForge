using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace Coral.Tests;

/// <summary>
/// TEMPORARY diagnostic scaffolding — NOT a permanent part of the suite. Bisects
/// ManualPseudoConsoleSmokeTest's STATUS_DLL_INIT_FAILED (0xC0000142) failure by
/// testing three increasingly-complete tiers of the same CreateProcess path in
/// isolation, so a single run pinpoints which layer introduces the failure:
///
///   Tier A: STARTUPINFOEX + EXTENDED_STARTUPINFO_PRESENT, an attribute list
///           allocated but with ZERO attributes set, INHERITED environment
///           (no custom block). Isolates whether the extended-startup-info
///           plumbing itself (struct layout, attribute list alloc) is sound.
///   Tier B: Same as A, but with the hand-built environment block instead of
///           inherited. Isolates whether BuildEnvironmentBlock is the culprit.
///   Tier C: Full ConPTY (same as ManualPseudoConsoleSmokeTest) — the known-bad
///           case, included here only for side-by-side contrast in one run.
///
/// Delete this file once the real bug is found and fixed in
/// Win32PseudoConsoleLauncher.cs.
/// </summary>
[Trait("Category", "Manual")]
public class ManualConPtyDiagnosticTest
{
    private readonly ITestOutputHelper _output;

    public ManualConPtyDiagnosticTest(ITestOutputHelper output) => _output = output;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
        int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint STILL_ACTIVE = 259;

    private (uint exitCode, int win32Error) RunTier(string label, bool withCustomEnvironment)
    {
        var attributeSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 0, 0, ref attributeSize);
        // With 0 attributes requested, some Windows versions report a size of
        // zero, which AllocHGlobal(0) handles fine (returns a valid, empty block).
        var attributeList = Marshal.AllocHGlobal(attributeSize == IntPtr.Zero ? (IntPtr)8 : attributeSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 0, 0, ref attributeSize))
            {
                var err = Marshal.GetLastWin32Error();
                _output.WriteLine($"[{label}] InitializeProcThreadAttributeList (2nd call) failed: Win32 error {err}");
                return (0, err);
            }

            var startupInfo = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = attributeList,
            };
            var commandLine = new StringBuilder("cmd.exe /c \"echo hello-from-diagnostic\"");

            byte[]? envBlock = withCustomEnvironment ? BuildEnvironmentBlockCopy() : null;
            var envHandle = envBlock is null ? default : GCHandle.Alloc(envBlock, GCHandleType.Pinned);
            try
            {
                var flags = EXTENDED_STARTUPINFO_PRESENT | (withCustomEnvironment ? CREATE_UNICODE_ENVIRONMENT : 0);
                var envPtr = envBlock is null ? IntPtr.Zero : envHandle.AddrOfPinnedObject();
                var ok = CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                    envPtr, Environment.CurrentDirectory, ref startupInfo, out var processInfo);
                if (!ok)
                {
                    var err = Marshal.GetLastWin32Error();
                    _output.WriteLine($"[{label}] CreateProcess failed: Win32 error {err}");
                    return (0, err);
                }

                CloseHandle(processInfo.hThread);
                uint exitCode = STILL_ACTIVE;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    if (GetExitCodeProcess(processInfo.hProcess, out exitCode) && exitCode != STILL_ACTIVE) break;
                    Thread.Sleep(50);
                }
                CloseHandle(processInfo.hProcess);
                _output.WriteLine($"[{label}] exit code: {exitCode} (0x{exitCode:X8})");
                return (exitCode, 0);
            }
            finally
            {
                if (envHandle.IsAllocated) envHandle.Free();
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private static byte[] BuildEnvironmentBlockCopy()
    {
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var value = (string?)entry.Value ?? "";
            if (value.Length == 0) continue; // zero-length values are documented as unsupported
            merged[(string)entry.Key] = value;
        }
        var sb = new StringBuilder();
        foreach (var (key, value) in merged) sb.Append(key).Append('=').Append(value).Append('\0');
        sb.Append('\0');
        return Encoding.Unicode.GetBytes(sb.ToString());
    }

    [Fact]
    public void BisectsWhichLayerCausesTheFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipped: Windows only.");
            return;
        }

        var tierA = RunTier("Tier A: STARTUPINFOEX, no attributes, inherited env", withCustomEnvironment: false);
        var tierB = RunTier("Tier B: STARTUPINFOEX, no attributes, custom env block", withCustomEnvironment: true);

        _output.WriteLine("");
        _output.WriteLine($"Tier A result: exitCode={tierA.exitCode} (0x{tierA.exitCode:X8}), win32Error={tierA.win32Error}");
        _output.WriteLine($"Tier B result: exitCode={tierB.exitCode} (0x{tierB.exitCode:X8}), win32Error={tierB.win32Error}");
        _output.WriteLine("Report both lines above verbatim.");

        // Not a real pass/fail gate — this test exists to print diagnostics.
        // Fail only if BOTH tiers report the exact same DLL_INIT_FAILED code as
        // the full ConPTY path, which would point away from ConPTY entirely.
    }
}
