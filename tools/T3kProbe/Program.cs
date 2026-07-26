// T3kProbe - DEV ONLY. Verifies the Tone3000 API contract assumptions (spec: "Assumptions
// to verify"). Reads %APPDATA%\Namager\tone3000.json directly, INCLUDING the
// secret key (the only code allowed to touch it; this tool never ships).
// Usage: dotnet run --project tools/T3kProbe [-- --user | <search-term>]
//   --user: probe GET /api/v1/user with an OAuth PKCE *user* access token (not the server
//   secret). This is the open question in docs/tone3000-api-findings.md: /user is confirmed to
//   work with the secret key, but whether a user token can read it decides whether NAMager can
//   use Tone3000 as an identity provider. Requires being signed in via the app first.
using System.Text.Json;
using Namager.Tone3000;

if (args.Length > 0 && args[0] == "--user") return await ProbeUserTokenAsync();

async Task<int> ProbeUserTokenAsync()
{
    // RedirectPort is only used by the browser sign-in leg; the refresh path ignores it, so a
    // missing config file is still usable here.
    var cfg = T3kConfig.TryLoad() ?? new T3kConfig(T3kConfig.EmbeddedPublishableKey, 0);
    var store = new T3kTokenStore();
    var auth = new T3kAuth(cfg, store);

    if (!auth.IsSignedIn)
    {
        Console.WriteLine("Not signed in: no refresh token at %APPDATA%\\Namager\\tone3000.token.");
        Console.WriteLine("Sign in through the app first, then re-run this probe.");
        return 1;
    }

    string accessToken;
    try { accessToken = await auth.GetAccessTokenAsync(); }
    catch (Exception e)
    {
        Console.WriteLine($"Could not obtain an access token: {e.GetType().Name}: {e.Message}");
        Console.WriteLine("A dead refresh token looks like this. Sign in again and re-run.");
        return 1;
    }
    Console.WriteLine($"user access token: {accessToken[..Math.Min(8, accessToken.Length)]}… (masked, {accessToken.Length} chars)");

    using var userHttp = new HttpClient { BaseAddress = new Uri(T3kConfig.DefaultBaseUrl) };
    userHttp.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

    Console.WriteLine("\n=== GET /api/v1/user  (Bearer = OAuth PKCE user token) ===");
    var resp = await userHttp.GetAsync("/api/v1/user");
    Console.WriteLine($"HTTP {(int)resp.StatusCode} {resp.StatusCode}");
    var body = await resp.Content.ReadAsStringAsync();
    try { body = JsonSerializer.Serialize(JsonDocument.Parse(body).RootElement, new JsonSerializerOptions { WriteIndented = true }); }
    catch (JsonException) { /* leave as-is */ }
    Console.WriteLine(body.Length > 4000 ? body[..4000] + "\n…(truncated)" : body);

    Console.WriteLine(resp.IsSuccessStatusCode
        ? "\nVERDICT: a user token CAN read /user - Tone3000 is usable as an identity provider."
        : "\nVERDICT: a user token CANNOT read /user - sign-in needs a different identity source.");
    return resp.IsSuccessStatusCode ? 0 : 2;
}

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
