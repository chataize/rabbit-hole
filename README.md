# Rabbit Hole

Rabbit Hole is a small, deterministic web text scraper for .NET. It discovers links within a root URL and extracts readable text from HTML pages. The output is a Markdown-like string suited for indexing, summarization, or offline processing.

## Use cases

- Build a lightweight search index for a site
- Feed content into an LLM or summarization pipeline
- Snapshot documentation pages for offline use
- Validate a sitemap against actual in-page links

## Features

- Async breadth-first link discovery with de-duplication
- Scope control to the root URL prefix
- Skips common non-HTML assets by extension
- HTML-only parsing based on Content-Type
- Metadata extraction: title, meta description, meta keywords
- Markdown-like content output for headings, paragraphs, and lists
- Inline links and images preserved in the output
- Cancellation support for long-running crawls

## Requirements

- .NET 10 (net10.0)

## Install

```bash
dotnet add package ChatAIze.RabbitHole
```

## Quick start

```csharp
using ChatAIze.RabbitHole;

var scraper = new WebsiteScraper();

await foreach (var link in scraper.ScrapeLinksAsync("https://example.com", depth: 2))
{
    Console.WriteLine(link);
}

var page = await scraper.ScrapeContentAsync("https://example.com");
Console.WriteLine(page.Title);
Console.WriteLine(page.Content);
```

## Usage patterns

### Crawl links, then fetch content

```csharp
using ChatAIze.RabbitHole;

var scraper = new WebsiteScraper();

await foreach (var link in scraper.ScrapeLinksAsync("https://example.com", depth: 3))
{
    var page = await scraper.ScrapeContentAsync(link);
    Console.WriteLine($"{page.Url} -> {page.Title}");
}
```

### Cancel a long crawl

```csharp
using ChatAIze.RabbitHole;

var scraper = new WebsiteScraper();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

await foreach (var link in scraper.ScrapeLinksAsync("https://example.com", depth: 3, cts.Token))
{
    Console.WriteLine(link);
}
```

### Filter links before scraping content

```csharp
using ChatAIze.RabbitHole;

var scraper = new WebsiteScraper();

await foreach (var link in scraper.ScrapeLinksAsync("https://example.com", depth: 3))
{
    if (!link.Contains("/docs/"))
    {
        continue;
    }

    var page = await scraper.ScrapeContentAsync(link);
    Console.WriteLine(page.Content);
}
```

## Link discovery details

- The root URL is always yielded first.
- The crawl is breadth-first; the root is depth 1.
- Links discovered on a page are yielded immediately.
- Pages are only fetched if their depth is strictly less than the `depth` parameter.
  - Example: `depth: 2` fetches the root page and yields its links, but does not fetch those links.
  - Example: `depth: 3` fetches the root page and each linked page once, but does not go deeper.
- URLs are normalized by trimming, lowercasing, and removing query strings and fragments.
- Only URLs that start with the root URL prefix are considered in-scope.
- Root-relative links (starting with `/`) are resolved against the root host.
- Relative links without a leading slash are ignored.
- The crawler ignores `mailto:`, `tel:`, and anchor-only (`#...`) links.
- Responses are only parsed when the Content-Type is `text/html`.
- Non-HTML assets are filtered by extension (see `WebsiteScraper` for the list).

## Content extraction details

- Non-HTML responses return a `PageDetails` instance with null metadata and content.
- Standard metadata is extracted when available:
  - `<title>`
  - `<meta name="description">`
  - `<meta name="keywords">`
- Content is selected from `article`, `main`, or `div.content`, falling back to the entire document.
- Output is a Markdown-like text representation:
  - Headings `h1`-`h6` map to `#`-style headings
  - Paragraphs become plain text with inline links and images preserved
  - Lists become `-` or numbered list items
- Whitespace is collapsed to keep the output readable.

## Output format

The output is Markdown-like and optimized for readability, not strict Markdown compliance.

```text
# Welcome

This is a [link](https://example.com/about).

- First item
- Second item
```

## Error handling and resiliency

- `ScrapeLinksAsync` performs best-effort crawling and skips pages that fail to load or parse.
- `ScrapeContentAsync` throws `HttpRequestException` for non-success status codes.
- Cancellation is honored during link crawling and during content fetches.

## Limitations and notes

- No JavaScript rendering; content must be present in the HTML response.
- No robots.txt handling or rate limiting is built in. Be mindful when crawling.
- Lowercasing and query/fragment removal may collapse distinct URLs on case-sensitive servers.
- In-scope checks use a simple string prefix; paths like `/docs` and `/docs-old` are both treated as in-scope.
- Root-relative URLs are resolved with scheme and host only, which drops non-default ports.
- Only anchor tags (`<a href=...>`) are used for link discovery.

## API reference

### `WebsiteScraper`

```csharp
public async IAsyncEnumerable<string> ScrapeLinksAsync(
    string url,
    int depth = 2,
    CancellationToken cancellationToken = default)

public async ValueTask<PageDetails> ScrapeContentAsync(
    string url,
    CancellationToken cancellationToken = default)
```

### `PageDetails`

```csharp
public sealed record PageDetails(
    string Url,
    string? Title,
    string? Description,
    string? Keywords,
    string? Content);
```

## Development

Build the library:

```bash
dotnet build
```

Run the preview app:

```bash
dotnet run --project ChatAIze.RabbitHole.Preview
```

## Links
- GitHub: https://github.com/chataize/rabbit-hole
- Chataize organization: https://github.com/chataize
- Website: https://www.chataize.com

## License

GPL-3.0-or-later. See `LICENSE.txt`.
