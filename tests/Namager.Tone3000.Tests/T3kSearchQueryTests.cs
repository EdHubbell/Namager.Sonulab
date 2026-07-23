using Namager.Tone3000;
using Xunit;

namespace Namager.Tone3000.Tests;

public class T3kSearchQueryTests
{
    [Theory]
    // Full slugged URL — the digits after the last hyphen are the id.
    [InlineData("https://www.tone3000.com/tones/1971-fender-super-six-reverb-74141", 74141)]
    [InlineData("https://www.tone3000.com/tones/deluxe-65-27365", 27365)]
    [InlineData("https://tone3000.com/tones/27365", 27365)]              // no www, slugless
    [InlineData("http://www.tone3000.com/tones/deluxe-65-27365", 27365)] // http scheme
    [InlineData("https://www.tone3000.com/tones/deluxe-65-27365/", 27365)] // trailing slash
    [InlineData("https://www.tone3000.com/tones/27365?utm_source=x", 27365)] // query string
    [InlineData("https://www.tone3000.com/tones/27365#top", 27365)]      // fragment
    [InlineData("www.tone3000.com/tones/deluxe-65-27365", 27365)]        // scheme-less paste
    [InlineData("  https://www.tone3000.com/tones/27365  ", 27365)]      // surrounding whitespace
    [InlineData("74141", 74141)]                                        // bare numeric id
    public void Recognizes_tone_ids(string input, long expected)
    {
        var q = T3kSearchQuery.Parse(input);
        Assert.Equal(T3kQueryKind.ToneId, q.Kind);
        Assert.Equal(expected, q.ToneId);
    }

    [Theory]
    [InlineData("https://www.tone3000.com/daweed")]                     // profile, not a tone
    [InlineData("https://example.com/tones/5")]                         // wrong host
    [InlineData("https://www.tone3000.com/tones/no-digits")]            // slug has no trailing number
    [InlineData("https://www.tone3000.com/tones/")]                     // empty tone segment
    [InlineData("http://tone3000.com/")]                                // no path
    public void Rejects_non_tone_links(string input) =>
        Assert.Equal(T3kQueryKind.BadLink, T3kSearchQuery.Parse(input).Kind);

    [Theory]
    [InlineData("deluxe reverb")]
    [InlineData("fender 65 deluxe")]     // contains digits but is a phrase, not a bare id
    public void Falls_through_to_text(string input)
    {
        var q = T3kSearchQuery.Parse(input);
        Assert.Equal(T3kQueryKind.Text, q.Kind);
        Assert.Equal(input, q.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_is_text_with_no_payload(string? input)
    {
        var q = T3kSearchQuery.Parse(input);
        Assert.Equal(T3kQueryKind.Text, q.Kind);
        Assert.Null(q.Text);
    }
}
