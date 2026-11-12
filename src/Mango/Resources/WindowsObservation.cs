using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Mango.Host;
using Mango.Providers;

namespace Mango.Resources;

internal sealed class WindowsObservation : IDisposable
{
    private readonly GpuCounters gpu = new();
    private readonly Dictionary<(int Id, long Start), long> previousWorkers = [];
    private readonly bool singleProcessorGroup = GetActiveProcessorGroupCount() == 1;
    private (ulong Total, ulong Busy)? previousCpu;

    public DeviceMemory[]? ReadFreshGpuMemory() => gpu.ReadFreshMemory();

    public ResourceObservation Sample(CatalogView view, DateTimeOffset now)
    {
        var memory = WindowsMemory.Read();
        var endpoints = view.Endpoints.Where(endpoint => endpoint.Mode == ProviderMode.Managed &&
            (endpoint.Online || endpoint.OwnedProcessId.HasValue)).ToArray();
        var roots = new Dictionary<Guid, int>();
        var listeners = endpoints.Any(endpoint => !endpoint.OwnedProcessId.HasValue) ? ReadListeners() : [];
        foreach (var endpoint in endpoints)
        {
            if (endpoint.OwnedProcessId is { } owned)
            {
                roots[endpoint.Id] = owned;
                continue;
            }
            var address = new Uri(endpoint.Address);
            if (!address.IsLoopback)
                continue;
            var host = IPAddress.TryParse(address.DnsSafeHost, out var ip) ? ip : null;
            var matches = listeners.Where(listener => listener.Port == address.Port &&
                (host is null ? IPAddress.IsLoopback(listener.Address) || IsAny(listener.Address)
                    : listener.Address.AddressFamily == host.AddressFamily &&
                        (listener.Address.Equals(host) || IsAny(listener.Address))))
                .Select(listener => listener.Process).Distinct().ToArray();
            if (matches.Length == 1)
                roots[endpoint.Id] = matches[0];
        }

        var attributed = roots.Count == endpoints.Length;
        var parents = roots.Count != 0 ? ReadParents() : [];
        attributed &= parents is not null;
        var workers = new Dictionary<Guid, WorkerMemory>();
        var allIds = new HashSet<int>();
        var currentWorkers = new Dictionary<(int Id, long Start), long>();
        var gpuSample = gpu.Read();
        foreach (var (endpoint, root) in roots)
        {
            var tree = new Dictionary<int, ProcessSample>();
            if (ReadProcess(root) is not { } process)
            {
                attributed = false;
                continue;
            }
            tree[root] = process;
            if (parents is not null)
            {
                var queue = new Queue<int>();
                queue.Enqueue(root);
                while (queue.TryDequeue(out var parent) && parents.TryGetValue(parent, out var children))
                    foreach (var child in children)
                    {
                        if (tree.ContainsKey(child))
                            continue;
                        if (ReadProcess(child) is { } sample && sample.Start >= tree[parent].Start)
                        {
                            tree[child] = sample;
                            queue.Enqueue(child);
                        }
                        else
                            attributed = false;
                    }
            }
            foreach (var (id, sample) in tree)
            {
                allIds.Add(id);
                currentWorkers[(id, sample.Start)] = sample.Cpu;
            }
            var deviceBytes = gpuSample.WorkerBytes?.Where(item => tree.ContainsKey(item.Process))
                .GroupBy(item => item.Device).ToDictionary(group => group.Key, group => group.Sum(item => item.Bytes));
            workers[endpoint] = new(tree.Keys.Order().ToArray(), tree.Values.Sum(item => item.Resident),
                deviceBytes, tree.Values.Sum(item => item.Peak),
                gpuSample.SharedBytes?.Where(item => tree.ContainsKey(item.Process)).Sum(item => item.Bytes));
        }

        double? externalCpu = null;
        double? managedCpu = null;
        if (singleProcessorGroup && GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user;
            var busy = total - Math.Min(idle, total);
            if (previousCpu is { } previous && total > previous.Total && busy >= previous.Busy && attributed)
            {
                var own = currentWorkers.Sum(item => previousWorkers.TryGetValue(item.Key, out var before)
                    ? Math.Max(0, item.Value - before) : 0);
                managedCpu = Math.Clamp(own * 100.0 / (total - previous.Total), 0, 100);
                externalCpu = Math.Clamp(((double)(busy - previous.Busy) - own) * 100 / (total - previous.Total), 0, 100);
            }
            previousCpu = (total, busy);
        }
        previousWorkers.Clear();
        foreach (var sample in currentWorkers)
            previousWorkers.Add(sample.Key, sample.Value);

        var externalGpu = attributed && gpuSample.Engines is { } engines
            ? engines.Where(item => !allIds.Contains(item.Process)).GroupBy(item => item.Engine)
                .Select(group => Math.Clamp(group.Sum(item => item.Percent), 0, 100)).DefaultIfEmpty().Max() : (double?)null;
        var limitation = !attributed ? "Windows could not attribute every managed provider worker."
            : !singleProcessorGroup ? "CPU attribution is unavailable across multiple Windows processor groups."
            : gpuSample.Hardware is null || externalGpu is null ? "Windows GPU activity or device topology is unavailable."
            : gpuSample.Devices.Any(item => !item.Integrated && item.UsedBytes is null)
                ? "Windows did not report dedicated memory usage for every GPU." : null;
        return new(now, checked((long)memory.Total), checked((long)memory.Available), externalCpu, externalGpu,
            gpuSample.Devices, gpuSample.Hardware, workers, attributed, limitation, managedCpu);
    }

    private static bool IsAny(IPAddress address) => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    private sealed record ProcessSample(long Start, long Cpu, long Resident, long Peak);
    private static ProcessSample? ReadProcess(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return new(process.StartTime.ToUniversalTime().Ticks, process.TotalProcessorTime.Ticks,
                process.WorkingSet64, process.PeakWorkingSet64);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    internal static bool ProcessExists(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }

    private static Dictionary<int, List<int>>? ReadParents()
    {
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == -1)
            return null;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(handle, ref entry))
                return null;
            var parents = new Dictionary<int, List<int>>();
            do
            {
                if (!parents.TryGetValue(entry.ParentId, out var children))
                    parents[entry.ParentId] = children = [];
                children.Add(entry.ProcessId);
            } while (Process32Next(handle, ref entry));
            return parents;
        }
        finally { CloseHandle(handle); }
    }

    private sealed record Listener(IPAddress Address, int Port, int Process);
    private static unsafe Listener[] ReadListeners()
    {
        var listeners = new List<Listener>();
        foreach (var family in new[] { 2, 23 })
        {
            uint bytes = 0;
            if (GetExtendedTcpTable(null, ref bytes, false, family, 3, 0) != 122 ||
                bytes is < 4 or > 4 * 1024 * 1024)
                continue;
            var buffer = new byte[bytes];
            fixed (byte* table = buffer)
            {
                if (GetExtendedTcpTable(table, ref bytes, false, family, 3, 0) != 0)
                    continue;
                var size = family == 2 ? 24 : 56;
                var count = Math.Min(*(uint*)table, (bytes - 4) / size);
                for (var i = 0; i < count; i++)
                {
                    var row = table + 4 + i * size;
                    var port = row + (family == 2 ? 8 : 20);
                    var address = family == 2 ? new IPAddress(new ReadOnlySpan<byte>(row + 4, 4))
                        : new IPAddress(new ReadOnlySpan<byte>(row, 16), *(uint*)(row + 16));
                    listeners.Add(new(address, port[0] * 256 + port[1], *(int*)(row + size - 4)));
                }
            }
        }
        return listeners.ToArray();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public int ProcessId;
        public nuint Heap;
        public uint ModuleId;
        public uint Threads;
        public int ParentId;
        public int BasePriority;
        public uint Flags;
        public fixed char Name[260];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("iphlpapi.dll")]
    private static extern unsafe uint GetExtendedTcpTable(byte* table, ref uint length,
        [MarshalAs(UnmanagedType.Bool)] bool sort, int family, int kind, uint reserved);

    public void Dispose() => gpu.Dispose();
}
