using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mango.Peers;

namespace Mango.Host;

internal enum LocalRole { Leech, Contributor }
internal enum BenchmarkPolicy { Never, Now, WhenConvenient }

internal sealed record LocalSettings
{
    public const int MinimumSampleSeconds = 2;
    public const int MaximumSampleSeconds = 3600;
    public const int MaximumCooldownSeconds = 86400;
    public const int MaximumReserveMiB = 1024 * 1024;
    public const int DefaultVramReserveMiB = 1024;

    [JsonRequired] public LocalRole Role { get; init; } = LocalRole.Leech;
    [JsonRequired] public bool ActivityMonitoring { get; init; } = true;
    [JsonRequired] public int SampleSeconds { get; init; } = MinimumSampleSeconds;
    [JsonRequired] public int CpuCutoffPercent { get; init; } = 80;
    [JsonRequired] public int GpuCutoffPercent { get; init; } = 80;
    [JsonRequired] public int RamCutoffPercent { get; init; } = 90;
    [JsonRequired] public int VramCutoffPercent { get; init; } = 90;
    [JsonRequired] public int CooldownSeconds { get; init; } = 300;
    public int? RamReserveMiB { get; init; }
    public int? VramReserveMiB { get; init; }
    [JsonRequired] public bool ReducedReserveConsent { get; init; }
    [JsonRequired] public BenchmarkPolicy BenchmarkPolicy { get; init; } = BenchmarkPolicy.Never;
    public bool MaintenanceDeferred { get; init; }
    public bool DailyUpdates { get; init; }
    public int? NodePort { get; init; }

    public static string? Validate(LocalSettings settings)
    {
        if (!Enum.IsDefined(settings.Role) || !Enum.IsDefined(settings.BenchmarkPolicy))
            return "Choose a valid local role and benchmark policy.";
        if (settings.SampleSeconds is < MinimumSampleSeconds or > MaximumSampleSeconds)
            return $"Sampling interval must be between {MinimumSampleSeconds} and {MaximumSampleSeconds} seconds.";
        if (settings.CooldownSeconds is < 1 or > MaximumCooldownSeconds)
            return $"Quiet cooldown must be between 1 and {MaximumCooldownSeconds} seconds.";
        if (!ValidPercent(settings.CpuCutoffPercent) || !ValidPercent(settings.GpuCutoffPercent) ||
            !ValidPercent(settings.RamCutoffPercent) || !ValidPercent(settings.VramCutoffPercent))
            return "Activity cutoffs must be between 1 and 100 percent.";
        if (settings.RamReserveMiB is < 0 or > MaximumReserveMiB ||
            settings.VramReserveMiB is < 0 or > MaximumReserveMiB)
            return $"Memory reserves must be between 0 and {MaximumReserveMiB} MiB.";
        if (settings.NodePort is < 1 or > 65535)
            return "The node port must be between 1 and 65535.";
        return null;
    }

    public static int DefaultRamReserveMiB(ulong physicalBytes) =>
        (int)Math.Clamp(Math.Ceiling(physicalBytes * 0.15 / (1024 * 1024)), 2048, 5120);

    public bool HasReducedReserve(int defaultRamMiB) =>
        RamReserveMiB < defaultRamMiB || VramReserveMiB < DefaultVramReserveMiB;

    public int EffectiveRamReserveMiB(int defaultRamMiB) =>
        RamReserveMiB is { } custom && (custom >= defaultRamMiB || ReducedReserveConsent) ? custom : defaultRamMiB;

    public int EffectiveVramReserveMiB =>
        VramReserveMiB is { } custom && (custom >= DefaultVramReserveMiB || ReducedReserveConsent)
            ? custom : DefaultVramReserveMiB;

    private static bool ValidPercent(int percent) => percent is >= 1 and <= 100;
}

internal sealed record LocalSettingsPatch(Dictionary<string, JsonElement>? Changes,
    Dictionary<string, JsonElement>? Expected, bool AcceptReducedReserve = false)
{
    public LocalSettings Apply(LocalSettings current, int defaultRamMiB)
    {
        if (Changes is not { Count: > 0 and <= 14 } || Expected is null || Expected.Count != Changes.Count)
            throw new PeerFailure(400, "Supply changed preferences and their previous values.");
        var snapshot = JsonSerializer.SerializeToElement(current, SnapshotFile<LocalSettings>.Json);
        var next = current;
        foreach (var (name, value) in Changes)
        {
            if (name == "reducedReserveConsent" || !snapshot.TryGetProperty(name, out var before))
                throw new PeerFailure(400, $"Unknown or protected preference: {name}.");
            if (!Expected.TryGetValue(name, out var expected) || !JsonElement.DeepEquals(before, expected))
                throw new PeerFailure(409, $"The {name} preference changed elsewhere. Review its current value.", "conflict");
            try
            {
                T Read<T>() => value.Deserialize<T>(SnapshotFile<LocalSettings>.Json)!;
                next = name switch
                {
                    "role" => next with { Role = Read<LocalRole>() },
                    "activityMonitoring" => next with { ActivityMonitoring = Read<bool>() },
                    "sampleSeconds" => next with { SampleSeconds = Read<int>() },
                    "cooldownSeconds" => next with { CooldownSeconds = Read<int>() },
                    "cpuCutoffPercent" => next with { CpuCutoffPercent = Read<int>() },
                    "gpuCutoffPercent" => next with { GpuCutoffPercent = Read<int>() },
                    "ramCutoffPercent" => next with { RamCutoffPercent = Read<int>() },
                    "vramCutoffPercent" => next with { VramCutoffPercent = Read<int>() },
                    "ramReserveMiB" => next with { RamReserveMiB = Read<int?>() },
                    "vramReserveMiB" => next with { VramReserveMiB = Read<int?>() },
                    "benchmarkPolicy" => next with { BenchmarkPolicy = Read<BenchmarkPolicy>() },
                    "maintenanceDeferred" => next with { MaintenanceDeferred = Read<bool>() },
                    "dailyUpdates" => next with { DailyUpdates = Read<bool>() },
                    _ => throw new PeerFailure(400, $"Unknown preference: {name}.")
                };
            }
            catch (JsonException) { throw new PeerFailure(400, $"Enter a valid value for {name}."); }
        }
        if (LocalSettings.Validate(next) is { } invalid) throw new PeerFailure(400, invalid);
        var reduced = next.HasReducedReserve(defaultRamMiB);
        if (reduced && !current.ReducedReserveConsent && !AcceptReducedReserve)
            throw new PeerFailure(400, "Acknowledge the reduced memory reserve risk first.", "reserve_consent");
        return next with { ReducedReserveConsent = current.ReducedReserveConsent || reduced && AcceptReducedReserve };
    }
}

internal static class WindowsMemory
{
    public static ulong PhysicalBytes() => Read().Total;

    public static (ulong Total, ulong Available) Read()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read physical memory.");
        return (memory.TotalPhysical, memory.AvailablePhysical);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
