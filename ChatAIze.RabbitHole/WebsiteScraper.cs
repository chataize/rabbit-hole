using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ChatAIze.RabbitHole;

public sealed partial class WebsiteScraper
{
    // Static list of asset extensions we never want to crawl as HTML pages.
    private static readonly FrozenSet<string> ignoredExtensions = new List<string>(
    [
        ".7z",
        ".apk",
        ".avi",
        ".bz2",
        ".css",
        ".csv",
        ".dmg",
        ".doc",
        ".docx",
        ".exe",
        ".flv",
        ".gif",
        ".gz",
        ".iso",
        ".jpeg",
        ".jpg",
        ".js",
        ".json",
        ".jsx",
        ".md",
        ".mov",
        ".mp3",
        ".mp4",
        ".msi",
        ".ogg",
        ".pdf",
        ".png",
        ".ppt",
        ".pptx",
        ".rar",
        ".rpm",
        ".svg",
        ".tar",
        ".ts",
        ".tsx",
        ".txt",
        ".webm",
        ".webp",
        ".wmv",
        ".xls",
        ".xlsx",
        ".xml",
        ".xz",
        ".zip",
    ]).ToFrozenSet();

    // Reuse a single HttpClient instance to keep connection pooling efficient.
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public async IAsyncEnumerable<string> ScrapeLinksAsync(string url, int depth = 2, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL cannot be null or whitespace.", nameof(url));
        }

        // Normalize upfront so comparisons and de-duping are consistent.
        url = url.Trim().ToLowerInvariant();

        // Ensure we start from a valid absolute URL and capture it as the root.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var rootUri))
        {
            throw new ArgumentException("The provided URL is invalid.", nameof(url));
        }

        // Always emit the root URL first.
        yield return url;

        // Depth < 2 means "only the root URL."
        if (depth < 2)
        {
            yield break;
        }

        // Track URLs we have already emitted and a BFS queue with depth.
        var foundUrls = new HashSet<string>();
        var urlsToVisit = new Queue<LinkCandidate>();

        foundUrls.Add(url);
        urlsToVisit.Enqueue(new LinkCandidate(url, 1));

        while (urlsToVisit.TryDequeue(out var currentUrl))
        {
            // Allow the caller to cancel a potentially long crawl.
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            var htmlDocument = new HtmlDocument();

            try
            {
                // Fetch each page and parse only successful HTML responses.
                using var response = await _httpClient.GetAsync(currentUrl.Url, cancellationToken);
                // Skip non-HTML or unsuccessful responses.
                if (!response.IsSuccessStatusCode || !response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.InvariantCulture) != false)
                {
                    continue;
                }

                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                htmlDocument.Load(contentStream);
            }
            catch
            {
                // Best-effort crawl: ignore network/parse failures and continue.
                continue;
            }

            // Consider only anchor tags with href attributes.
            foreach (var node in htmlDocument.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                var foundUrl = node.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrWhiteSpace(foundUrl) || foundUrl == "/")
                {
                    continue;
                }

                // Normalize the URL as early as possible.
                foundUrl = foundUrl.Trim().ToLowerInvariant();

                // Ignore anchor-only links plus mailto/tel links.
                if (foundUrl.StartsWith('#') || foundUrl.StartsWith("mailto:") || foundUrl.StartsWith("tel:"))
                {
                    continue;
                }

                // Skip URLs that point to non-HTML assets.
                var path = Path.GetExtension(foundUrl);
                if (!string.IsNullOrWhiteSpace(path) && ignoredExtensions.Contains(path))
                {
                    continue;
                }

                // Resolve root-relative links against the original host.
                if (foundUrl.StartsWith('/'))
                {
                    foundUrl = $"{rootUri.Scheme}://{rootUri.Host}{foundUrl}";
                }

                // Keep the crawl inside the original URL prefix.
                if (!foundUrl.StartsWith(url))
                {
                    continue;
                }

                // Remove query/fragment so the same page is treated as one URL.
                var queryIndex = foundUrl.IndexOf('?');
                if (queryIndex > 0)
                {
                    foundUrl = foundUrl[..queryIndex];
                }

                var hashIndex = foundUrl.IndexOf('#');
                if (hashIndex > 0)
                {
                    foundUrl = foundUrl[..hashIndex];
                }

                if (foundUrls.Add(foundUrl))
                {
                    // Yield newly discovered URLs immediately.
                    yield return foundUrl;

                    // Enqueue for BFS traversal if we haven't reached the depth limit.
                    if (currentUrl.Depth + 1 < depth)
                    {
                        urlsToVisit.Enqueue(new LinkCandidate(foundUrl, currentUrl.Depth + 1));
                    }
                }
            }
        }
    }

    public async ValueTask<PageDetails> ScrapeContentAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL cannot be null or whitespace.", nameof(url));
        }

        // Validate that the content scrape starts from an absolute URL.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("The provided URL is invalid.", nameof(url));
        }

        // Fetch the URL directly; unlike link discovery, we expect errors to surface.
        using var response = await _httpClient.GetAsync(uri, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Failed to retrieve content from '{url}'. Status code: {response.StatusCode}.");
        }

        // For non-HTML responses, return empty content while keeping the URL.
        if (response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.InvariantCulture) != true)
        {
            return new PageDetails(url, null, null, null, null);
        }

        var htmlDocument = new HtmlDocument();
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // Load the HTML into a DOM we can query with XPath.
        htmlDocument.Load(contentStream);

        var root = htmlDocument.DocumentNode;

        // Extract standard metadata when it exists.
        var title = root.SelectSingleNode("//title")?.InnerText.Trim();
        var description = root.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "").Trim();
        var keywords = root.SelectSingleNode("//meta[@name='keywords']")?.GetAttributeValue("content", "").Trim();

        // Prefer semantic containers when available; fall back to the full document.
        var contentNode = root.SelectSingleNode("//article") ?? root.SelectSingleNode("//main") ?? root.SelectSingleNode("//div[contains(@class, 'content')]") ?? root;
        // Build a simple Markdown-like representation of the content.
        var stringBuilder = new StringBuilder();

        // Flatten only common text elements to avoid boilerplate markup.
        foreach (var node in contentNode.SelectNodes(".//h1 | .//h2 | .//h3 | .//h4 | .//h5 | .//h6 | .//p | .//ul | .//ol") ?? Enumerable.Empty<HtmlNode>())
        {
            // Normalize whitespace so output is easier to read and compare.
            var trimmedText = SpaceRegex().Replace(node.InnerText.Trim(), " ");
            switch (node.Name)
            {
                case "h1":
                    // Map heading levels to Markdown-style prefixes.
                    _ = stringBuilder.AppendLine($"# {trimmedText}");
                    break;
                case "h2":
                    _ = stringBuilder.AppendLine($"## {trimmedText}");
                    break;
                case "h3":
                    _ = stringBuilder.AppendLine($"### {trimmedText}");
                    break;
                case "h4":
                    _ = stringBuilder.AppendLine($"#### {trimmedText}");
                    break;
                case "h5":
                    _ = stringBuilder.AppendLine($"##### {trimmedText}");
                    break;
                case "h6":
                    _ = stringBuilder.AppendLine($"###### {trimmedText}");
                    break;
                case "p":
                    // Reconstruct paragraph text while keeping inline links/images.
                    var paragraphContent = new StringBuilder();
                    foreach (var child in node.ChildNodes)
                    {
                        // Preserve inline images and links while flattening other text.
                        if (child.Name == "img")
                        {
                            var imgSrc = child.GetAttributeValue("src", "");
                            var imgAlt = child.GetAttributeValue("alt", "").Trim();

                            if (!string.IsNullOrEmpty(imgSrc))
                            {
                                _ = paragraphContent.AppendLine($"![{imgAlt}]({imgSrc})");
                            }
                        }
                        else if (child.Name == "a")
                        {
                            var href = child.GetAttributeValue("href", "");
                            var linkText = SpaceRegex().Replace(child.InnerText.Trim(), " ");

                            if (!string.IsNullOrEmpty(href))
                            {
                                // Render anchors as Markdown links.
                                _ = paragraphContent.Append($"[{linkText}]({href})");
                            }
                        }
                        else
                        {
                            var text = SpaceRegex().Replace(child.InnerText.Trim(), " ");
                            _ = paragraphContent.Append(text);
                        }
                    }

                    _ = stringBuilder.AppendLine(paragraphContent.ToString());
                    break;
                case "ul":
                    // Convert bullet list items to Markdown-style list entries.
                    var liNodes = node.SelectNodes(".//li");
                    if (liNodes != null)
                    {
                        foreach (var liNode in node.SelectNodes(".//li") ?? Enumerable.Empty<HtmlNode>())
                        {
                            var listItemText = SpaceRegex().Replace(liNode.InnerText.Trim(), " ");
                            _ = stringBuilder.AppendLine($"- {listItemText}");
                        }
                    }

                    break;
                case "ol":
                    // Keep list ordering explicit in the rendered output.
                    var itemIndex = 1;
                    var liNodes2 = node.SelectNodes(".//li");

                    if (liNodes2 != null)
                    {
                        foreach (var liNode in node.SelectNodes(".//li") ?? Enumerable.Empty<HtmlNode>())
                        {
                            var listItemText = SpaceRegex().Replace(liNode.InnerText.Trim(), " ");
                            _ = stringBuilder.AppendLine($"{itemIndex}. {listItemText}");
                            itemIndex++;
                        }
                    }

                    break;
            }
        }

        // Wrap up the normalized content and metadata for the caller.
        return new PageDetails(url, title, description, keywords, stringBuilder.ToString());
    }

    // Simple container for the BFS crawl queue.
    private record LinkCandidate(string Url, int Depth);

    // Collapse repeated whitespace to keep extracted text readable.
    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
