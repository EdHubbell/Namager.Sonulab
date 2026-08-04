using System;
using System.Linq;

/// <summary>The real `browse root\app\mod` response from a StompStation on fw 2.5.1, captured with
/// `dotnet run --project tools/HwCheck -- --browse root\app\mod`. Used verbatim so the editor's
/// nesting, ordering and label behaviour is tested against what the device actually says rather
/// than against a hand-written idea of it.</summary>
public static class ModBrowseFixture
{
    public static string[] Records { get; } =
    {
        "root\\app\\mod:{\"desc\":\"Mod\",\"value\":\"\",\"type\":\"item\",\"item_type\":\"hfolder\",\"def\":\"\"}",
        "root\\app\\mod\\on_off:{\"desc\":\"Enable\",\"value\":\"OFF\",\"type\":\"enum\",\"def\":\"OFF\",\"options\":[\"ON\",\"OFF\"]}",
        "root\\app\\mod\\mode:{\"desc\":\"Mode\",\"value\":\"Chorus\",\"type\":\"enum\",\"def\":\"Chorus\",\"options\":[\"Chorus\",\"Flanger\",\"Phaser\"]}",
        "root\\app\\mod\\rate:{\"desc\":\"Rate\",\"value\":\"partempo\",\"type\":\"item\",\"item_type\":\"module\",\"def\":\"partempo\"}",
        "root\\app\\mod\\dpth:{\"desc\":\"Depth\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":50.0,\"unit\":\"%\",\"dec\":0}",
        "root\\app\\mod\\mix:{\"desc\":\"Dry-Wet\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":50.0,\"unit\":\"%\",\"dec\":0}",
        "root\\app\\mod\\tcfolder:{\"desc\":\"Tone and Character\",\"value\":\"\",\"type\":\"item\",\"item_type\":\"vfolder\",\"def\":\"\"}",
        "root\\app\\mod\\trfolder:{\"desc\":\"Tremolo\",\"value\":\"\",\"type\":\"item\",\"item_type\":\"vfolder\",\"def\":\"\"}",
        "root\\app\\mod\\rate\\rawdata:{\"desc\":\"Rate\",\"value\":1.0,\"type\":\"float\",\"min\":0.05,\"max\":8.0,\"def\":1.0,\"unit\":\"Hz\"}",
        "root\\app\\mod\\rate\\sbdv:{\"desc\":\"Time Subdivision\",\"value\":\"1/4\",\"type\":\"enum\",\"def\":\"1/4\",\"options\":[\"4/4\",\"2/4\",\"1/4\",\"Dotted 8th\",\"1/8\",\"1/16\",\"Triplet\"]}",
        "root\\app\\mod\\rate\\lock:{\"desc\":\"Lock Options\",\"value\":\"Unlocked\",\"type\":\"enum\",\"def\":\"Unlocked\",\"options\":[\"Unlocked\",\"Global\",\"Preset\"]}",
        "root\\app\\mod\\tcfolder\\emp:{\"desc\":\"Emphasis\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":50.0,\"unit\":\"%\",\"dec\":0}",
        "root\\app\\mod\\tcfolder\\shape:{\"desc\":\"Shape\",\"value\":\"Triang\",\"type\":\"enum\",\"def\":\"Triang\",\"options\":[\"Triang\",\"Sin\",\"Square\"]}",
        "root\\app\\mod\\tcfolder\\hicut:{\"desc\":\"Hi-Cut\",\"value\":18000.0,\"type\":\"float\",\"min\":900.0,\"max\":20000.0,\"def\":18000.0,\"unit\":\"Hz\",\"dec\":0}",
        "root\\app\\mod\\tcfolder\\locut:{\"desc\":\"Lo-Cut\",\"value\":20.0,\"type\":\"float\",\"min\":20.0,\"max\":1200.0,\"def\":20.0,\"unit\":\"Hz\",\"dec\":0}",
        "root\\app\\mod\\tcfolder\\sphase:{\"desc\":\"Stereo Phase\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":180.0,\"def\":0.0,\"unit\":\"deg\",\"dec\":0}",
        "root\\app\\mod\\trfolder\\on_off:{\"desc\":\"Enable\",\"value\":\"OFF\",\"type\":\"enum\",\"def\":\"OFF\",\"options\":[\"ON\",\"OFF\"]}",
        "root\\app\\mod\\trfolder\\rate:{\"desc\":\"Rate\",\"value\":\"partempo\",\"type\":\"item\",\"item_type\":\"module\",\"def\":\"partempo\"}",
        "root\\app\\mod\\trfolder\\dpt:{\"desc\":\"Depth\",\"value\":25.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":25.0,\"unit\":\"%\",\"dec\":0}",
        "root\\app\\mod\\trfolder\\wave:{\"desc\":\"Waveform\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":0.0,\"unit\":\"%\",\"dec\":0}",
        "root\\app\\mod\\trfolder\\sphase:{\"desc\":\"Stereo Phase\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":180.0,\"def\":0.0,\"unit\":\"deg\",\"dec\":0}",
        "root\\app\\mod\\trfolder\\rate\\rawdata:{\"desc\":\"Rate\",\"value\":4.0,\"type\":\"float\",\"min\":0.7,\"max\":15.0,\"def\":4.0,\"unit\":\"Hz\"}",
        "root\\app\\mod\\trfolder\\rate\\lock:{\"desc\":\"Lock Options\",\"value\":\"Unlocked\",\"type\":\"enum\",\"def\":\"Unlocked\",\"options\":[\"Unlocked\",\"Global\",\"Preset\"]}",
        "root\\app\\mod\\trfolder\\rate\\sbdv:{\"desc\":\"Time Subdivision\",\"value\":\"1/4\",\"type\":\"enum\",\"def\":\"1/4\",\"options\":[\"4/4\",\"2/4\",\"1/4\",\"Dotted 8th\",\"1/8\",\"1/16\",\"1/32\",\"Triplet\"]}",
    };

    /// <summary>The same records with `trfolder\on_off` (Tremolo's own enable) flipped to ON, for
    /// the auto-open cases. `mod\on_off` is left OFF — a test that needs the BLOCK on flips it
    /// itself, so the two switches never move together by accident.</summary>
    public static string[] WithTremoloOn() =>
        Records.Select(r => r.StartsWith("root\\app\\mod\\trfolder\\on_off:", StringComparison.Ordinal)
                                ? r.Replace("\"value\":\"OFF\"", "\"value\":\"ON\"")
                                : r)
               .ToArray();
}
