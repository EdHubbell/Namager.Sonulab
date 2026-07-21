// T3kProbe - DEV ONLY. Verifies the Tone3000 API contract assumptions (spec: "Assumptions
// to verify"). Reads %APPDATA%\ToneManager\tone3000.json directly, INCLUDING the
// secret key (the only code allowed to touch it; this tool never ships).
// Usage: dotnet run --project tools/T3kProbe [-- <search-term>]
using System.Text.Json;
using ToneManager.Tone3000;

var cfgPath = T3kConfig.DefaultPath;
using var cfgDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
string? secret = cfgDoc.RootElement.TryGetProperty("secret_key", out var s) ? s.GetString() : null;
if (string.IsNullOrWhiteSpace(secret)) { Console.WriteLine($"No secret_key in {cfgPath} - the probe uses the server credential."); return 1; }
Console.WriteLine($"config: {cfgPath}  secret: {secret[..7]}…{secret[^2..]} (masked)");

var http = new HttpClient { BaseAddress = new Uri(T3kConfig.DefaultBaseUrl) };
http.DefaultRequestHeaders.Authorization = new("Bearer", secret);
string term = args.Length > 0 ? args[0] : "deluxe";

async Task<string> GetAsync(string path)
{
    Console.WriteLine($"\n=== GET {path} ===");
    var resp = await http.GetAsync(path);
    Console.WriteLine($"HTTP {(int)resp.StatusCode} {resp.StatusCode}");
    var body = await resp.Content.ReadAsStringAsync();
    // Pretty-print up to 4000 chars so field names are readable in the findings doc.
    try { body = JsonSerializer.Serialize(JsonDocument.Parse(body).RootElement, new JsonSerializerOptions { WriteIndented = true }); }
    catch (JsonException) { /* leave as-is */ }
    Console.WriteLine(body.Length > 4000 ? body[..4000] + "\n…(truncated)" : body);
    return body;
}

await GetAsync("/api/v1/user");
var namSearch = await GetAsync($"/api/v1/tones/search?query={Uri.EscapeDataString(term)}&format=nam&page=1&page_size=3");
await GetAsync($"/api/v1/tones/search?query={Uri.EscapeDataString(term)}&format=ir&page=1&page_size=3");

// Drill into the first NAM result: tone detail -> models -> download the first model.
var page = T3kJson.ParsePage<T3kTone>(namSearch);
if (page.Data.Count > 0)
{
    long id = page.Data[0].Id;
    await GetAsync($"/api/v1/tones/{id}");
    var modelsJson = await GetAsync($"/api/v1/models?tone_id={id}");
    var models = T3kJson.ParsePage<T3kModel>(modelsJson);
    var m = models.Data.FirstOrDefault(x => x.ModelUrl is not null);
    if (m?.ModelUrl is { } url)
    {
        Console.WriteLine($"\n=== DOWNLOAD {url} ===");
        var resp = await http.GetAsync(url);
        Console.WriteLine($"HTTP {(int)resp.StatusCode}, Content-Type={resp.Content.Headers.ContentType}, {resp.Content.Headers.ContentLength} bytes");
        var tmp = Path.Combine(Path.GetTempPath(), "t3kprobe-model.bin");
        await File.WriteAllBytesAsync(tmp, await resp.Content.ReadAsByteArrayAsync());
        Console.WriteLine($"wrote {tmp}; first bytes: {Convert.ToHexString((await File.ReadAllBytesAsync(tmp))[..Math.Min(16, (int)new FileInfo(tmp).Length)])}");
    }
    else Console.WriteLine("no model with model_url found - RECORD THIS in the findings doc");
}
else Console.WriteLine("search returned no data - RECORD THIS in the findings doc");
Console.WriteLine("\nProbe complete. Transcribe findings into docs/tone3000-api-findings.md");
return 0;
