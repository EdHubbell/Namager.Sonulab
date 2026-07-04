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

    [Fact]
    public void Save_escapes_quotes_in_name()
    {
        var cmd = Sonulab.Core.Protocol.SonuCommands.Save(@"root\app\preset", "Say \"hi\"");
        Assert.Contains("\"value\":\"Say \\\"hi\\\"\"", cmd);
        Assert.Contains("\"save\":\"save\"", cmd);
    }

    [Fact] public void DRead_builds_command() =>
        Assert.Equal(@"dread root\presets:{""index"":4,""chunk"":1}", SonuCommands.DRead(@"root\presets", 4, 1));

    [Fact] public void DWrite_builds_command() =>
        Assert.Equal(@"dwrite root\presets:{""index"":4,""chunk"":-1,""value"":""4142""}",
            SonuCommands.DWrite(@"root\presets", 4, -1, "4142"));
}
