using System.Text.Json;
using Mango.Host;
using Mango.Peers;

internal static class WebUiChecks
{
    public static async Task RunAsync(string root)
    {
        var store = new SnapshotFile<LocalSettings>(Path.Combine(root, "ui-preferences.json"), LocalSettings.Validate);
        await store.LoadAsync(new());
        var baseline = store.Value;
        LocalSettingsPatch Patch(string name, object? value, object? expected, bool consent = false) => new(
            new() { [name] = JsonSerializer.SerializeToElement(value, SnapshotFile<LocalSettings>.Json) },
            new() { [name] = JsonSerializer.SerializeToElement(expected, SnapshotFile<LocalSettings>.Json) }, consent);
        await Task.WhenAll(
            store.UpdateAsync(current => current with { Role = LocalRole.Contributor }),
            store.UpdateAsync(current => Patch("sampleSeconds", 4, baseline.SampleSeconds).Apply(current, 2048)));
        Check(store.Value is { Role: LocalRole.Contributor, SampleSeconds: 4 }, "disjoint tray and UI changes survive");
        Reject(409, () => Patch("sampleSeconds", 6, 2).Apply(store.Value, 2048));
        Reject(400, () => Patch("sampleSeconds", 1, 4).Apply(store.Value, 2048));
        Reject(400, () => Patch("sampleSeconds", "wrong", 4).Apply(store.Value, 2048));
        Reject(400, () => Patch("reducedReserveConsent", true, false).Apply(store.Value, 2048));
        Reject(400, () => Patch("ramReserveMiB", 0, null).Apply(store.Value, 2048));
        var consented = Patch("ramReserveMiB", 0, null, true).Apply(store.Value, 2048);
        Check(consented is { RamReserveMiB: 0, ReducedReserveConsent: true }, "explicit reserve acknowledgement is retained");
        var defaulted = Patch("ramReserveMiB", null, 0).Apply(consented, 2048);
        Check(defaulted.RamReserveMiB is null && defaulted.EffectiveRamReserveMiB(2048) == 2048, "blank restores default reserve");
        await store.UpdateAsync(current => Patch("dailyUpdates", true, false).Apply(current, 2048));
        Check(store.Value is { DailyUpdates: true, Role: LocalRole.Contributor, SampleSeconds: 4 }, "maintenance update keeps local protection");
        Console.WriteLine("WebUI preference validation and concurrent-writer checks passed.");
    }

    private static void Reject(int status, Action work)
    {
        try { work(); }
        catch (PeerFailure error) when (error.Status == status) { return; }
        throw new InvalidOperationException($"Expected preference rejection {status}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
