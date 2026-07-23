using Namager.Tone3000;

public class T3kJsonTests
{
    // Note: "author"/"page_url"/"downloads" below were the Task 1 ASSUMED field names before
    // the live API was probed (see docs/tone3000-api-findings.md). They are now genuinely
    // unknown/unused fields on T3kTone (the real names are "user.username", "url",
    // "downloads_count" — see Live_tone_search_shape_maps_via_convenience_accessors below).
    // This test still passes, but only because lenient parsing ignores unknown fields, not
    // because these names mean anything to the current model — kept as a regression guard on
    // "parsing tolerates unknown fields", not as documentation of the real wire shape.
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

    // Regression: sanitized snippet captured from a live T3kProbe run against
    // GET /api/v1/tones/search?query=deluxe&format=nam (2026-07-07; see
    // docs/tone3000-api-findings.md). Reality diverged from the Task 1 assumptions:
    // author lives at "user.username" (no top-level "author"), card art is a plural
    // "images" array (no "image_url"), the page link is "url" (no "page_url"), and
    // counts are "downloads_count"/"favorites_count" (not "downloads"/"stars").
    [Fact]
    public void Live_tone_search_shape_maps_via_convenience_accessors()
    {
        var json = """
        { "data": [ { "id": 27365, "title": "Deluxe 65'", "description": "Deluxe clean oxford",
              "images": [ "https://api.tone3000.com/storage/v1/object/public/images/70pu8jrjcll.jpg" ],
              "user_id": "239b4c29-9b16-4ccb-9820-4b056a651f1f",
              "user": { "username": "daweed", "id": "239b4c29-9b16-4ccb-9820-4b056a651f1f",
                        "url": "https://www.tone3000.com/daweed" },
              "url": "https://www.tone3000.com/tones/deluxe-65-27365",
              "downloads_count": 7943, "favorites_count": 130, "format": "nam", "gear": "amp-cab" } ],
          "page": 1, "page_size": 3, "total": 187, "total_pages": 63 }
        """;
        var page = T3kJson.ParsePage<T3kTone>(json);
        var tone = Assert.Single(page.Data);
        Assert.Equal(27365, tone.Id);
        Assert.Equal("daweed", tone.Author);
        Assert.Equal("https://api.tone3000.com/storage/v1/object/public/images/70pu8jrjcll.jpg", tone.ImageUrl);
        Assert.Equal("https://www.tone3000.com/tones/deluxe-65-27365", tone.PageUrl);
        Assert.Equal(7943, tone.Downloads);
        Assert.Equal(130, tone.Stars);
        Assert.Equal("nam", tone.Format);
    }

    // Regression: sanitized snippet from GET /api/v1/user (2026-07-07). The user id is a
    // UUID string, not an integer like tone/model ids - Task 1 assumed `long Id`.
    [Fact]
    public void Live_user_shape_has_string_id_not_long()
    {
        var user = T3kJson.Parse<T3kUser>("""
            { "id": "ca1c3703-e575-4a44-bb87-927172e9dcd7", "username": "edhubbell",
              "avatar_url": null, "bio": null }
            """);
        Assert.NotNull(user);
        Assert.Equal("ca1c3703-e575-4a44-bb87-927172e9dcd7", user!.Id);
        Assert.Equal("edhubbell", user.Username);
    }

    // Regression: sanitized snippet from GET /api/v1/models?tone_id= (2026-07-07). Confirms
    // model_url is an absolute, extension-bearing URL and that the endpoint does NOT return
    // a per-model "format" field (it stays null; format must come from the parent tone or
    // be inferred from the ModelUrl extension).
    [Fact]
    public void Live_model_shape_has_no_format_field()
    {
        var models = T3kJson.ParsePage<T3kModel>("""
            { "data": [ { "id": 105981, "tone_id": 27365, "name": "Deluxe 65' Grrrrr",
                  "model_url": "https://www.tone3000.com/api/v1/models/105981/download/6po4pf3n07x.nam",
                  "size": "standard", "architecture_version": "1" } ],
              "page": 1, "page_size": 10, "total": 6, "total_pages": 1 }
            """);
        var model = Assert.Single(models.Data);
        Assert.Equal("https://www.tone3000.com/api/v1/models/105981/download/6po4pf3n07x.nam", model.ModelUrl);
        Assert.Null(model.Format);
    }
}
