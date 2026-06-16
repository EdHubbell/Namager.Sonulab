using Sonulab.Core.Model;

namespace Sonulab.Core.Connection;

public enum CompatibilityStatus { Tested, UntestedNewer, Unknown, StructuralMismatch }

public sealed record TestedFirmware(string License, string Arch, string Version);
public sealed record DeviceInfo(string Name, string Id, string Version, string Arch, string License);
public sealed record CompatibilityResult(CompatibilityStatus Status, bool WritesAllowed, string Message, DeviceInfo Device);

public sealed class CompatibilityChecker
{
    // (path, expected size, expected item_type)
    private static readonly (string Path, int Size, string ItemType)[] Lists =
    {
        (@"root\presets", 8192, "pst_pst"),
        (@"root\amp", 12288, "vxamp"),
        (@"root\ir", 4096, "wav_44100"),
    };

    private readonly IReadOnlyList<TestedFirmware> _tested;
    public CompatibilityChecker(IReadOnlyList<TestedFirmware> tested) => _tested = tested;

    public async Task<CompatibilityResult> CheckAsync(SonuClient client, CancellationToken ct = default)
    {
        var device = new DeviceInfo(
            Name: await client.ReadValueAsync(@"root\sys\_name", ct) ?? "",
            Id: await client.ReadValueAsync(@"root\sys\_id", ct) ?? "",
            Version: await client.ReadValueAsync(@"root\sys\_ver", ct) ?? "",
            Arch: await client.ReadValueAsync(@"root\sys\_arch", ct) ?? "",
            License: await client.ReadValueAsync(@"root\sys\_license", ct) ?? "");

        // Structural preflight (version-independent). Browse each list node DIRECTLY — never
        // `browse root`, which over serial is a ~30 KB / multi-second dump whose truncation would
        // corrupt the next command's response. Each list-node browse is small and fast.
        foreach (var (path, size, itemType) in Lists)
        {
            var rec = (await client.BrowseRecordsAsync(path, ct)).FirstOrDefault(r => r.Path == path);
            if (rec is null)
                return Mismatch(device, $"List node {path} not found.");
            int? GetInt(string n) => rec.Json.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : null;
            string? GetStr(string n) => rec.Json.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            if (GetInt("count") != 30 || GetInt("chunk") != 128 || GetInt("size") != size || GetStr("item_type") != itemType)
                return Mismatch(device, $"Structural mismatch at {path} (count/chunk/size/item_type).");
        }

        // Version gate.
        var matching = _tested.Where(t => t.License == device.License && t.Arch == device.Arch).ToList();
        if (matching.Any(t => t.Version == device.Version))
            return new CompatibilityResult(CompatibilityStatus.Tested, true,
                $"Firmware {device.Version} is tested.", device);

        if (System.Version.TryParse(device.Version, out var dv) && matching.Count > 0)
        {
            System.Version? maxTested = null;
            foreach (var t in matching)
                if (System.Version.TryParse(t.Version, out var tv) && (maxTested is null || tv > maxTested))
                    maxTested = tv;
            if (maxTested is not null && dv > maxTested)
                return new CompatibilityResult(CompatibilityStatus.UntestedNewer, false,
                    $"Firmware {device.Version} is newer than the tested {maxTested}; writes disabled.", device);
        }
        return new CompatibilityResult(CompatibilityStatus.Unknown, false,
            $"Firmware {device.Version} has not been tested; writes disabled.", device);
    }

    private static CompatibilityResult Mismatch(DeviceInfo d, string msg) =>
        new(CompatibilityStatus.StructuralMismatch, false, msg, d);
}
