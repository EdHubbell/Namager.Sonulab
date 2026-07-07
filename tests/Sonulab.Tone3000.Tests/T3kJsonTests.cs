using Sonulab.Tone3000;

public class T3kJsonTests
{
    [Fact]
    public void Parses_a_pagination_envelope_with_unknown_fields()
    {
        var json = """
        { "data": [ { "id": 42, "title": "65 Deluxe Reverb", "author": "fabiossousa",
                      "page_url": "https://www.tone3000.com/tones/42", "downloads": 4200,
                      "some_future_field": {"x": 1} } ],
          "page": 1, "page_size": 20, "total": 831, "total_pages": 42, "extra": true }
        """;
        var page = T3kJson.ParsePage<T3kTone>(json);
        Assert.Single(page.Data);
        Assert.Equal(42, page.Data[0].Id);
        Assert.Equal("65 Deluxe Reverb", page.Data[0].Title);
        Assert.Equal(831, page.Total);
        Assert.Equal(42, page.TotalPages);
    }

    [Fact]
    public void Missing_optional_fields_parse_as_null()
    {
        var tone = T3kJson.Parse<T3kTone>("""{ "id": 7 }""");
        Assert.NotNull(tone);
        Assert.Equal(7, tone!.Id);
        Assert.Null(tone.Title);
        Assert.Null(tone.ImageUrl);
        Assert.Null(tone.Downloads);
    }

    [Fact]
    public void Snake_case_maps_to_pascal_properties()
    {
        var model = T3kJson.Parse<T3kModel>(
            """{ "id": 9, "name": "Clean", "format": "nam", "model_url": "https://cdn.example/m/9" }""");
        Assert.Equal("https://cdn.example/m/9", model!.ModelUrl);
    }

    [Fact]
    public void Garbage_returns_null_not_throw() =>
        Assert.Null(T3kJson.Parse<T3kTone>("not json at all"));
}
