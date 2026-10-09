using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sintonia.Infrastructure.Diagnostics;

/// <summary>Own validation descendants even when the command's root process exits first.</summary>
internal sealed class ValidationProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    public ValidationProcessJob()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A execução de validação com controle de filhos requer Windows.");
        _handle = CreateJobObject(0, null);
        if (_handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); _handle.Dispose(); throw new Win32Exception(error); }
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(_handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        { var error = Marshal.GetLastWin32Error(); _handle.Dispose(); throw new Win32Exception(error); }
    }
    public async Task AttachAsync(Process process)
    {
        if (AssignProcessToJobObject(_handle, process.SafeHandle)) return;
        var error = Marshal.GetLastWin32Error();
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        throw new Win32Exception(error, "Não foi possível assumir o controle dos processos de validação.");
    }
    public async Task StopAsync()
    {
        if (_handle.IsClosed) return;
        if (!TerminateJobObject(_handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (!QueryInformationJobObject(_handle, 1, out var accounting, Marshal.SizeOf<BasicAccounting>(), 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (accounting.ActiveProcesses == 0) { Dispose(); return; }
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("Os processos filhos da validação ainda estão encerrando. Confira a pasta preservada.");
            await Task.Delay(25).ConfigureAwait(false);
        }
    }
    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessUserTime, JobUserTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out BasicAccounting accounting, int size, nint returnedSize);
}
