using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ChatAIze.RabbitHole;

/// <summary>
/// Crawls a website to discover in-scope links and extracts readable content from HTML pages.
/// </summary>
public sealed partial class WebsiteScraper
{
    /// <summary>
    /// Lowercased file extensions that indicate non-HTML assets to skip during link discovery.
    /// </summary>
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

    /// <summary>
    /// Shared HTTP client used for all requests to keep connections pooled, with a 60-second timeout.
    /// </summary>
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// Asynchronously discovers in-scope links starting from the provided URL.
    /// </summary>
    /// <param name="url">The absolute root URL to start crawling from.</param>
    /// <param name="depth">The maximum traversal depth (root is depth 1); values less than 2 only return the root URL.</param>
    /// <param name="cancellationToken">The token used to cancel the crawl.</param>
    /// <returns>An async sequence of normalized, de-duplicated URLs discovered during the crawl.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="url" /> is null, empty, or invalid.</exception>
    /// <remarks>
    /// The crawl uses breadth-first traversal, only parses <c>text/html</c> responses, and ignores mailto/tel/anchor-only links.
    /// URLs are normalized by trimming, lowercasing, removing query strings and fragments, and staying within the root URL prefix.
    /// Relative links without a leading slash are ignored because they do not match the root prefix.
    /// Because URLs are lowercased, paths that are case-sensitive on the server may be treated as equivalent.
    /// The sequence yields URLs as they are discovered and stops early if the cancellation token is triggered.
    /// </remarks>
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

    /// <summary>
    /// Downloads a page and extracts a Markdown-like representation of its content.
    /// </summary>
    /// <param name="url">The absolute URL to fetch and parse.</param>
    /// <param name="cancellationToken">The token used to cancel the fetch.</param>
    /// <returns>The extracted metadata and content for the requested page.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="url" /> is null, empty, or invalid.</exception>
    /// <exception cref="HttpRequestException">Thrown when the request fails or returns a non-success status.</exception>
    /// <remarks>
    /// If the response is not HTML, the returned metadata and content fields are null.
    /// Extraction prefers <c>article</c>, <c>main</c>, or <c>div.content</c> and renders headings, paragraphs, and lists
    /// into Markdown-style text while preserving inline links and images, with whitespace collapsed for readability.
    /// </remarks>
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

        // For non-HTML responses, return null metadata/content while keeping the URL.
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

    /// <summary>
    /// Queue item for breadth-first link traversal.
    /// </summary>
    /// <param name="Url">The URL to visit.</param>
    /// <param name="Depth">The current traversal depth for the URL, where the root is depth 1.</param>
    private record LinkCandidate(string Url, int Depth);

    /// <summary>
    /// Builds the compiled regex used to collapse consecutive whitespace in extracted text.
    /// </summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
