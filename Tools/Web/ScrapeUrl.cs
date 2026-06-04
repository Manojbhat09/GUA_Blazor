using LlmTornado.Common;
using System.Text.Json.Serialization;

namespace GUA_Blazor.Tools.Web;

public class ScrapeUrl : AITool<ScrapeUrlArguments>
{
    protected override async Task<object?> ExecuteAsync(ScrapeUrlArguments args)
    {
        if (string.IsNullOrWhiteSpace(args.Url))
            throw new Exception("URL is required.");

        var content = await WebScraper.ScrapeTextFromUrlAsync(args.Url);
        const int MaxChars = 4000;
        if (content is string s && s.Length > MaxChars)
            content = s[..MaxChars] + $"\n\n[TRUNCATED — {s.Length - MaxChars} more chars. Use browser_use to load and scroll.]";
        return content;
    }

    public override ToolFunction GetToolFunction() => new ToolFunction(
        "scrape_url",
        "Fetches and extracts readable text content from a URL. Supports HTML pages and PDFs. For PDFs, append #page=N to get a specific page.",
        new
        {
            type = "object",
            properties = new
            {
                url = new { type = "string", description = "The full URL to scrape, e.g. 'https://example.com' or 'https://example.com/file.pdf#page=2'" }
            },
            required = new List<string> { "url" }
        });
}

public class ScrapeUrlArguments
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}