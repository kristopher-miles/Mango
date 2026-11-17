using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mango.Host;
using Mango.Providers;
using Mango.Resources;

internal static class StrataChecks
{
    public static async Task RunAsync(string directory)
    {
        var state = Directory.CreateDirectory(Path.Combine(directory, "strata-checks")).FullName;
        await using var catalog = new ProviderCatalog(state);
        await catalog.InitializeAsync();
        var update = new EndpointUpdate(null, ProviderProfile.Strata, ProviderMode.Managed, null, null, false, null, null,
            StrataControl: Path.Combine(state, "missing-control"));
        foreach (var invalid in new[]
        {
            update with { Mode = ProviderMode.Attached },
            update with { Address = "https://example.invalid" },
            update with { StrataControl = "relative" },
            update with { Token = "manual-credential" }
        })
        {
            try { await catalog.ConfigureAsync(invalid, CancellationToken.None); }
            catch (ProviderFailure error) when (error.Status == 400) { continue; }
            throw new InvalidOperationException("Strata accepted an invalid or unowned registration.");
        }

        var strata = await catalog.ConfigureAsync(update, CancellationToken.None);
        Check(strata.ExclusiveHost && !strata.CanLoad && strata.Error?.Contains("bundled Control") == true &&
            strata.NetworkProfile == ProviderProfile.Generic, "Missing Control stays visible and unlaunchable with a compatible peer profile.");
        var attached = strata with { Id = Guid.NewGuid(), Profile = ProviderProfile.Generic, Mode = ProviderMode.Attached };
        var instance = new ProviderInstance("attached", 4096, 1, "fixture");
        var model = new ProviderModel("fixture", "attached", "attached", "Attached", null, ["text"], ["text"],
            [InferenceOperation.Chat], [instance], null, false, false, null, []);
        var resident = new Deployment(attached.Id, model, new(null, [], null, null, null, null, 0), true, false, null);
        Check(ProviderCatalog.ExclusiveBlocker(new([strata, attached], [resident], null, null), strata)?.Contains("attached provider") == true,
            "A resident attached endpoint blocks Strata.");
        Check(ProviderCatalog.ExclusiveBlocker(new([strata with { HostReserved = true }, attached], [], null, null), attached) is not null,
            "Strata's reservation blocks another endpoint even before model readiness.");

        const long gib = 1024L * 1024 * 1024;
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(state, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings { Role = LocalRole.Contributor, RamReserveMiB = 2048, ReducedReserveConsent = true });
        var observed = new ResourceObservation(DateTimeOffset.UtcNow, 64 * gib, 24 * gib, 0, 0, [], "fixture", [], true, null,
            FreeCommit: 3 * gib, ExclusiveEndpoint: strata.Id);
        using var resources = new ResourceProtection(state, settings, (_, _) => observed);
        await resources.InitializeAsync();
        resources.ApplyObservation(observed);
        Check(resources.Status.State == ContributionState.Contributor, "Commit headroom uses the confirmed Mango RAM reserve.");
        resources.ApplyObservation(observed with { FreeCommit = gib });
        Check(resources.Status.State == ContributionState.Withdrawing, "Commit below the configured reserve releases Strata.");

        Check(StrataAdapter.AllowsClientTools(default), "Cold Strata can route client tools before its template is loaded.");
        using var compatible = JsonDocument.Parse("""{"chat_template_caps":{"supports_tools":true,"supports_tool_calls":true}}""");
        using var noToolHistory = JsonDocument.Parse("""{"chat_template_caps":{"supports_tools":true,"supports_tool_calls":false}}""");
        using var noDeclarations = JsonDocument.Parse("""{"chat_template_caps":{"supports_tools":false}}""");
        Check(StrataAdapter.AllowsClientTools(compatible.RootElement) && !StrataAdapter.AllowsClientTools(noToolHistory.RootElement) &&
            !StrataAdapter.AllowsClientTools(noDeclarations.RootElement), "An observed Strata template must support declarations and tool calls.");
        using var http = new HttpClient();
        var adapter = new StrataAdapter(http, new(strata.Id, ProviderProfile.Strata, ProviderMode.Managed,
            "http://127.0.0.1:1", null, null, null), state, () => { });
        var toolRequest = JsonNode.Parse("""{"strata_mcp":true,"tools":[{"type":"function","function":{"name":"lookup"}}]}""")!.AsObject();
        adapter.Prepare(InferenceOperation.Chat, toolRequest);
        Check(!toolRequest.ContainsKey("strata_mcp") && toolRequest["tools"] is JsonArray { Count: 1 },
            "Mango forwards client function declarations without enabling Strata-side MCP.");

        await CheckJobAsync();
        Console.WriteLine("Strata registration, host exclusivity, and Windows job checks passed.");
    }

    private static async Task CheckJobAsync()
    {
        using var job = new WindowsJob();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { typeof(WindowsJob).Assembly.Location, WindowsJob.StarterArgument, powerShell, "-NoProfile",
            "-Command", "$child = Start-Process -WindowStyle Hidden -PassThru -FilePath (Join-Path $PSHOME powershell.exe) -ArgumentList '-NoProfile','-Command','Start-Sleep 30'; $child.Id; $child.WaitForExit()" })
            start.ArgumentList.Add(argument);
        using var starter = Process.Start(start)!;
        job.Add(starter);
        Check(job.Processes() is [var pid] && pid == starter.Id, "The starter is held before launching Control.");
        await starter.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'1' }, timeout.Token);
        starter.StandardInput.Close();
        var childId = int.Parse((await starter.StandardOutput.ReadLineAsync(timeout.Token))!);
        using var child = Process.GetProcessById(childId);
        Check(job.Processes().Contains(childId), "Windows job includes a new process started by Control's descendant.");
        job.Terminate();
        await Task.WhenAll(starter.WaitForExitAsync(timeout.Token), child.WaitForExitAsync(timeout.Token));
        Check(job.Processes().Length == 0, "Windows job confirms release after termination.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
