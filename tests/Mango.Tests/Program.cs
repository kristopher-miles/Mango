using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mango.Host;

var directory = Directory.CreateTempSubdirectory("mango-check-").FullName;
try
{
    TestDefaults();
    await TestHostAsync(directory);
    await ProviderChecks.RunAsync();
    await PeerChecks.RunAsync(directory);
    Console.WriteLine("Mango host checks passed.");
}
finally
{
    Directory.Delete(directory, recursive: true);
}

static void TestDefaults()
{
    const ulong gib = 1024L * 1024 * 1024;
    Check(LocalSettings.DefaultRamReserveMiB(8 * gib) == 2048, "RAM minimum");
    Check(LocalSettings.DefaultRamReserveMiB(16 * gib) == 2458, "15% RAM, rounded up to a whole MiB");
    Check(LocalSettings.DefaultRamReserveMiB(128 * gib) == 5120, "RAM maximum");
    Check(LocalSettings.DefaultVramReserveMiB == 1024, "Flat VRAM default");
    Check(new LocalSettings().CooldownSeconds == 300, "Five-minute cooldown");
}

static async Task TestHostAsync(string directory)
{
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    var path = Path.Combine(directory, "local-settings.json");
    using (var host = Launch(directory, "--headless"))
    {
        try
        {
            var url = await ReadUrlAsync(directory, host);
            Check(url.Host == "127.0.0.1", "Loopback URL");
            var token = url.Fragment["#session=".Length..];
            var endpoint = new Uri(url, "/api/local/settings");
            using var page = await http.GetAsync(new Uri(url, "/"));
            Check(page.IsSuccessStatusCode && (await page.Content.ReadAsStringAsync()).Contains("Local settings"), "Browser UI");
            Check((await LaunchCommandAsync(directory, "--headless")).Code == 0 && !host.HasExited,
                "Duplicate launch leaves one host");

            using var noSession = await http.GetAsync(endpoint);
            Check(noSession.StatusCode == HttpStatusCode.Unauthorized, "Session required");
            var modelList = new Uri(url, "/v1/models");
            using var noInferenceSession = await http.GetAsync(modelList);
            Check(noInferenceSession.StatusCode == HttpStatusCode.Unauthorized, "Local inference session required");
            using var crossOrigin = new HttpRequestMessage(HttpMethod.Put, endpoint);
            crossOrigin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            crossOrigin.Headers.Add("Origin", "https://unrelated.example");
            crossOrigin.Content = JsonContent.Create(new { });
            using var blocked = await http.SendAsync(crossOrigin);
            Check(blocked.StatusCode == HttpStatusCode.Forbidden, "Cross-origin update denied");
            using var wrongHost = new HttpRequestMessage(HttpMethod.Get, endpoint);
            wrongHost.Headers.Host = $"different.example:{url.Port}";
            using var rebinding = await http.SendAsync(wrongHost);
            Check(rebinding.StatusCode == HttpStatusCode.Forbidden, "Foreign Host header denied");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var noModels = await http.GetFromJsonAsync<JsonDocument>(modelList);
            Check(noModels!.RootElement.GetProperty("data").GetArrayLength() == 0, "No inference aliases before provider setup");

            var initial = await ReadSettingsAsync(http, endpoint);
            Check(initial.Role == LocalRole.Leech, "First run starts as leech");
            await SaveAsync(http, endpoint, initial with { SampleSeconds = 1 }, false, HttpStatusCode.BadRequest);
            var reduced = initial with { Role = LocalRole.Contributor, RamReserveMiB = 0, VramReserveMiB = 0 };
            await SaveAsync(http, endpoint, reduced with { ReducedReserveConsent = true }, false, HttpStatusCode.BadRequest);
            await SaveAsync(http, endpoint, reduced, true, HttpStatusCode.NoContent);
            var saved = await ReadSettingsAsync(http, endpoint);
            Check(saved is { ReducedReserveConsent: true, RamReserveMiB: 0, VramReserveMiB: 0 },
                "One-time consent and zero reserves saved");
            await SaveAsync(http, endpoint, saved with { SampleSeconds = 3, ReducedReserveConsent = false }, false,
                HttpStatusCode.NoContent);

            await StopAsync(directory, host);
        }
        finally
        {
            TerminateIfRunning(host);
        }
    }

    await File.WriteAllTextAsync(path + ".pending", "{");
    using (var host = Launch(directory, "--headless"))
    {
        try
        {
            var endpoint = await SettingsUrlAsync(http, directory, host);
            var settings = await ReadSettingsAsync(http, endpoint);
            Check(settings is { Role: LocalRole.Contributor, SampleSeconds: 3, ReducedReserveConsent: true },
                "Restart preserves latest complete state despite interrupted staging file");
            await StopAsync(directory, host);
        }
        finally
        {
            TerminateIfRunning(host);
        }
    }

    await File.WriteAllTextAsync(path, "{");
    using (var host = Launch(directory, "--headless"))
    {
        try
        {
            var endpoint = await SettingsUrlAsync(http, directory, host);
            using var snapshot = await http.GetFromJsonAsync<JsonDocument>(endpoint);
            Check(snapshot!.RootElement.GetProperty("recoveryWarning").ValueKind == JsonValueKind.String,
                "Invalid primary reported in the UI");
            var recovered = await ReadSettingsAsync(http, endpoint);
            Check(recovered is { Role: LocalRole.Contributor, SampleSeconds: 2, ReducedReserveConsent: true },
                "Last good snapshot loaded");
            Check(await File.ReadAllTextAsync(path) == "{", "Invalid primary retained during recovery");
            await SaveAsync(http, endpoint, recovered with { SampleSeconds = 4 }, false, HttpStatusCode.NoContent);
            var retained = Directory.GetFiles(directory, "local-settings.json.invalid-*").Single();
            Check(await File.ReadAllTextAsync(retained) == "{", "Explicit save preserves original invalid file");
            await StopAsync(directory, host);
        }
        finally
        {
            TerminateIfRunning(host);
        }
    }

    File.Delete(path + ".last-good");
    await File.WriteAllTextAsync(path, "{}");
    Check((await LaunchCommandAsync(directory, "--headless")).Code != 0, "Unrecoverable state prevents startup");
    Check(await File.ReadAllTextAsync(path) == "{}", "Unrecoverable settings are preserved");
    var diagnostic = Path.Combine(directory, "startup-error.txt");
    Check((await File.ReadAllTextAsync(diagnostic)).Contains("No usable snapshot"), "Invalid state startup diagnostic");

    File.Delete(path);
    var occupied = new TcpListener(IPAddress.Loopback, 0);
    occupied.Start();
    try
    {
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        Check((await LaunchCommandAsync(directory, "--headless", "--port", port.ToString())).Code != 0,
            "Binding conflict prevents startup");
        Check((await File.ReadAllTextAsync(diagnostic)).Contains($"127.0.0.1:{port}"), "Binding conflict startup diagnostic");
    }
    finally
    {
        occupied.Stop();
    }
}

static async Task<Uri> SettingsUrlAsync(HttpClient http, string directory, Process host)
{
    var url = await ReadUrlAsync(directory, host);
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", url.Fragment["#session=".Length..]);
    return new Uri(url, "/api/local/settings");
}

static async Task<LocalSettings> ReadSettingsAsync(HttpClient http, Uri endpoint)
{
    using var result = await http.GetFromJsonAsync<JsonDocument>(endpoint);
    return result!.RootElement.GetProperty("settings").Deserialize<LocalSettings>(SnapshotFile<LocalSettings>.Json)!;
}

static async Task SaveAsync(HttpClient http, Uri endpoint, LocalSettings settings, bool accept, HttpStatusCode expected)
{
    using var response = await http.PutAsJsonAsync(endpoint, new { settings, acceptReducedReserve = accept },
        SnapshotFile<LocalSettings>.Json);
    Check(response.StatusCode == expected, $"Save expected {expected}, received {response.StatusCode}");
}

static async Task<Uri> ReadUrlAsync(string directory, Process host)
{
    for (var attempt = 0; attempt < 25; attempt++)
    {
        if (host.HasExited)
            throw new InvalidOperationException($"Mango exited during startup. {await host.StandardError.ReadToEndAsync()}");
        var (code, output) = await LaunchCommandAsync(directory, "--url");
        if (code == 0 && Uri.TryCreate(output.Trim(), UriKind.Absolute, out var url))
            return url;
        await Task.Delay(100);
    }
    throw new InvalidOperationException("The local host did not publish a browser URL.");
}

static async Task StopAsync(string directory, Process host)
{
    Check((await LaunchCommandAsync(directory, "--quit")).Code == 0, "Local quit command");
    await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(host.ExitCode == 0, "Graceful headless exit");
}

static Process Launch(string directory, params string[] arguments)
{
    var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
    var process = new ProcessStartInfo(dotnet)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    process.ArgumentList.Add(typeof(LocalSettings).Assembly.Location);
    process.ArgumentList.Add("--data-dir");
    process.ArgumentList.Add(directory);
    foreach (var argument in arguments)
        process.ArgumentList.Add(argument);
    return Process.Start(process)!;
}

static async Task<(int Code, string Output)> LaunchCommandAsync(string directory, params string[] arguments)
{
    using var process = Launch(directory, arguments);
    try
    {
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
        await errors;
        return (process.ExitCode, await output);
    }
    finally
    {
        TerminateIfRunning(process);
    }
}

static void TerminateIfRunning(Process process)
{
    if (process.HasExited)
        return;
    process.Kill();
    process.WaitForExit();
}

static void Check(bool passed, string check)
{
    if (!passed)
        throw new InvalidOperationException($"Check failed: {check}");
}
