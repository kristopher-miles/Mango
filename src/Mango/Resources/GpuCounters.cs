using System.Runtime.InteropServices;
using System.Text;
using Mango.Providers;

namespace Mango.Resources;

internal sealed unsafe class GpuCounters : IDisposable
{
    internal sealed record EngineUse(int Process, string Engine, double Percent);
    internal sealed record WorkerUse(int Process, string Device, long Bytes);
    internal sealed record Sample(DeviceMemory[] Devices, string? Hardware, EngineUse[]? Engines, WorkerUse[]? WorkerBytes,
        (int Process, long Bytes)[]? SharedBytes);
    private sealed record Adapter(string Luid, string Id, string Name, bool Integrated, long? Bytes, ulong Driver);
    private static readonly Guid FactoryId = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
    private static readonly Guid ListId = new("526c7776-40e9-459b-b711-f32ad76dfc28");
    private static readonly Guid AdapterId = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
    private static readonly Guid Graphics = new("8c47866b-7583-450d-f0f0-6bada895af4b");
    private readonly nint query;
    private readonly nint engines;
    private readonly nint memory;
    private readonly nint processes;
    private readonly nint shared;
    private readonly nint memoryQuery;
    private readonly nint freshMemory;
    private readonly object memoryLock = new();
    private Adapter[]? adapters;
    private long nextTopology;

    public GpuCounters()
    {
        if (PdhOpenQuery(0, 0, out query) != 0)
        {
            query = 0;
            return;
        }
        engines = Add(@"\GPU Engine(*)\Utilization Percentage");
        memory = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        processes = Add(@"\GPU Process Memory(*)\Dedicated Usage");
        shared = Add(@"\GPU Process Memory(*)\Shared Usage");
        if (PdhOpenQuery(0, 0, out memoryQuery) == 0 &&
            PdhAddEnglishCounter(memoryQuery, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out freshMemory) != 0)
            freshMemory = 0;
    }

    private nint Add(string path) => PdhAddEnglishCounter(query, path, 0, out var counter) == 0 ? counter : 0;

    public Sample Read()
    {
        if (Environment.TickCount64 >= nextTopology)
        {
            Volatile.Write(ref adapters, ReadAdapters());
            nextTopology = Environment.TickCount64 + 60_000;
        }
        var engineRows = default((string Name, double Value)[]);
        var memoryRows = default((string Name, double Value)[]);
        var processRows = default((string Name, double Value)[]);
        var sharedRows = default((string Name, double Value)[]);
        if (query != 0 && PdhCollectQueryData(query) == 0)
        {
            engineRows = ReadCounter(engines);
            memoryRows = ReadCounter(memory);
            processRows = ReadCounter(processes);
            sharedRows = ReadCounter(shared);
        }
        var devices = Devices(adapters ?? [], memoryRows);
        var hardware = adapters is null ? null : ProviderIdentity.Hash(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            string.Join("|", adapters.Select(adapter => $"{adapter.Id}:{adapter.Driver}:{adapter.Integrated}:{adapter.Bytes}").Order()));
        var external = engineRows?.Select(row => ProcessName(row.Name) is { } process
            ? new EngineUse(process.Id, process.Suffix, row.Value) : null).OfType<EngineUse>().ToArray();
        if (adapters is { Length: 0 })
            external = [];
        var deviceIds = (adapters ?? []).Where(adapter => !adapter.Integrated)
            .ToDictionary(adapter => adapter.Luid, adapter => adapter.Id, StringComparer.OrdinalIgnoreCase);
        var workerBytes = processRows?.Select(row => ProcessName(row.Name) is { } process &&
            deviceIds.TryGetValue(process.Suffix, out var device) ? new WorkerUse(process.Id, device, (long)row.Value) : null)
            .OfType<WorkerUse>().ToArray();
        var sharedBytes = sharedRows?.Where(row => ProcessName(row.Name) is not null)
            .Select(row => (ProcessName(row.Name)!.Value.Id, (long)row.Value)).ToArray();
        return new(devices, hardware, external, workerBytes, sharedBytes);
    }

    public DeviceMemory[]? ReadFreshMemory()
    {
        var devices = Volatile.Read(ref adapters);
        if (devices is null || !devices.Any(device => !device.Integrated) || freshMemory == 0)
            return null;
        lock (memoryLock)
        {
            if (PdhCollectQueryData(memoryQuery) != 0)
                return null;
            return Devices(devices, ReadCounter(freshMemory));
        }
    }

    private static DeviceMemory[] Devices(Adapter[] adapters, (string Name, double Value)[]? rows)
    {
        var usage = rows?.ToLookup(row => row.Name, StringComparer.OrdinalIgnoreCase);
        return adapters.Select(adapter =>
        {
            var counters = usage?[adapter.Luid];
            return new DeviceMemory(adapter.Id, adapter.Name, adapter.Integrated, adapter.Bytes,
                !adapter.Integrated && counters?.Any() == true ? (long)counters.Max(row => row.Value) : null);
        }).ToArray();
    }

    private static (int Id, string Suffix)? ProcessName(string name)
    {
        if (!name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
            return null;
        var end = name.IndexOf('_', 4);
        return end > 4 && int.TryParse(name.AsSpan(4, end - 4), out var id) ? (id, name[(end + 1)..]) : null;
    }

    private static (string Name, double Value)[]? ReadCounter(nint counter)
    {
        if (counter == 0)
            return null;
        const uint format = 0x200 | 0x8000;
        uint bytes = 0;
        var status = PdhGetFormattedCounterArray(counter, format, ref bytes, out var count, null);
        if (status == 0 && count == 0)
            return [];
        if (status != 0x800007D2 || bytes is 0 or > 4 * 1024 * 1024)
            return null;
        var buffer = new byte[bytes];
        fixed (byte* data = buffer)
        {
            if (PdhGetFormattedCounterArray(counter, format, ref bytes, out count, data) != 0 ||
                count > bytes / sizeof(CounterItem))
                return null;
            var rows = new List<(string, double)>();
            for (var index = 0; index < count; index++)
            {
                var item = ((CounterItem*)data)[index];
                if (item.Status <= 1 && double.IsFinite(item.Value) && item.Value >= 0 &&
                    Marshal.PtrToStringUni(item.Name) is { } name)
                    rows.Add((name, item.Value));
            }
            return rows.Count == 0 && count != 0 ? null : rows.ToArray();
        }
    }

    private static Adapter[]? ReadAdapters()
    {
        var factoryId = FactoryId;
        var listId = ListId;
        var adapterId = AdapterId;
        var graphics = Graphics;
        nint factory = 0, list = 0;
        try
        {
            if (DXCoreCreateAdapterFactory(&factoryId, &factory) < 0 || factory == 0)
                return null;
            var createList = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, Guid*, nint*, int>)Table(factory)[3];
            if (createList(factory, 1, &graphics, &listId, &list) < 0 || list == 0)
                return null;
            var adapterCount = ((delegate* unmanaged[Stdcall]<nint, uint>)Table(list)[4])(list);
            var getAdapter = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Table(list)[3];
            if (adapterCount > 32)
                return null;
            var devices = new List<Adapter>();
            var description = stackalloc byte[256];
            for (uint index = 0; index < adapterCount; index++)
            {
                nint adapter = 0;
                if (getAdapter(list, index, &adapterId, &adapter) < 0 || adapter == 0)
                    return null;
                try
                {
                    var property = (delegate* unmanaged[Stdcall]<nint, uint, nuint, void*, int>)Table(adapter)[6];
                    byte hardware = 0, integrated = 0;
                    ulong luid = 0, dedicated = 0, driver = 0;
                    uint physical = 0;
                    HardwareId pci = default;
                    if (property(adapter, 11, 1, &hardware) < 0)
                        return null;
                    if (hardware == 0)
                        continue;
                    if (property(adapter, 12, 1, &integrated) < 0 || property(adapter, 0, 8, &luid) < 0 ||
                        property(adapter, 3, (nuint)sizeof(HardwareId), &pci) < 0)
                        return null;
                    var driverKnown = property(adapter, 1, 8, &driver) >= 0;
                    var memoryKnown = property(adapter, 7, 8, &dedicated) >= 0;
                    if (property(adapter, 15, 4, &physical) < 0)
                        physical = 1;
                    new Span<byte>(description, 256).Clear();
                    var label = property(adapter, 2, 256, description) >= 0
                        ? Encoding.UTF8.GetString(new ReadOnlySpan<byte>(description, 256)).TrimEnd('\0') : "GPU";
                    var key = $"{pci.Vendor:x4}:{pci.Device:x4}:{pci.Subsystem:x8}:{pci.Revision:x2}";
                    devices.Add(new($"luid_0x{luid >> 32:x8}_0x{(uint)luid:x8}_phys_0",
                        key, label, integrated != 0,
                        integrated == 0 && memoryKnown && physical == 1 && dedicated is > 0 and <= long.MaxValue ? (long)dedicated : null,
                        driverKnown ? driver : 0));
                }
                finally { Release(adapter); }
            }
            return devices.GroupBy(device => device.Id)
                .SelectMany(group => group.Count() == 1 ? group : group.Select(device => device with { Id = $"{device.Id}/{device.Luid}" }))
                .ToArray();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Release(list);
            Release(factory);
        }
    }

    private static nint* Table(nint value) => *(nint**)value;
    private static void Release(nint value)
    {
        if (value != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Table(value)[2])(value);
    }

    public void Dispose()
    {
        if (query != 0)
            PdhCloseQuery(query);
        lock (memoryLock)
            if (memoryQuery != 0)
                PdhCloseQuery(memoryQuery);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public nint Name; public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareId { public uint Vendor; public uint Device; public uint Subsystem; public uint Revision; }

    [DllImport("dxcore.dll")]
    private static extern int DXCoreCreateAdapterFactory(Guid* id, nint* factory);
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(nint source, nuint data, out nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nuint data, out nint counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, out uint count, byte* buffer);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
