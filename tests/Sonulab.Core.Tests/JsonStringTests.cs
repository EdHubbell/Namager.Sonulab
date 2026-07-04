using Sonulab.Core.Protocol;

public class JsonStringTests
{
    [Theory]
    [InlineData("Plexi", "\"Plexi\"")]
    [InlineData("", "\"\"")]
    [InlineData(null, "\"\"")]
    [InlineData("Say \"hi\"", "\"Say \\\"hi\\\"\"")]
    [InlineData(@"back\slash", "\"back\\\\slash\"")]
    public void Quote_escapes_quotes_and_backslashes(string? input, string expected) =>
        Assert.Equal(expected, JsonString.Quote(input));
}
