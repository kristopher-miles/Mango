using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mango.Host;

// The starter waits on stdin until it belongs to the job. Even a detached runtime inherits the job.
internal sealed class WindowsJob : IDisposable
{
    internal const string StarterArgument = "--strata-job-starter";
    private readonly SafeFileHandle handle = CreateJobObject(IntPtr.Zero, null);

    public WindowsJob()
    {
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!handle.IsInvalid && SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
            return;
        var error = new Win32Exception();
        handle.Dispose();
        throw error;
    }

    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.SafeHandle))
            throw new Win32Exception();
    }

    public unsafe int[] Processes()
    {
        for (var capacity = 16; ; capacity *= 2)
        {
            var buffer = new byte[8 + capacity * IntPtr.Size];
            fixed (byte* pointer = buffer)
            {
                if (QueryInformationJobObject(handle, 3, pointer, buffer.Length, IntPtr.Zero))
                {
                    var count = *(int*)(pointer + 4);
                    var result = new int[count];
                    for (var i = 0; i < count; i++)
                        result[i] = (int)((nuint*)(pointer + 8))[i];
                    return result;
                }
                if (Marshal.GetLastWin32Error() != 234 || capacity > 4096)
                    throw new Win32Exception();
            }
        }
    }

    public void Terminate()
    {
        if (!TerminateJobObject(handle, 1))
            throw new Win32Exception();
    }

    public void Dispose() => handle.Dispose();

    internal static async Task<int> StartChildAsync(string[] args)
    {
        if (args.Length < 3 || Console.OpenStandardInput().ReadByte() != '1')
            return 2;
        var start = new ProcessStartInfo(args[1])
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in args.AsSpan(2))
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new Win32Exception();
        await Task.WhenAll(child.WaitForExitAsync(), child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()),
            child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError()));
        return child.ExitCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool QueryInformationJobObject(SafeFileHandle job, int kind, byte* info, int length, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
