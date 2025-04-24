using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ChatAIze.RabbitHole;

public sealed partial class WebsiteScraper
{
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

        url = url.Trim().ToLowerInvariant();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var rootUri))
        {
            throw new ArgumentException("The provided URL is invalid.", nameof(url));
        }

        yield return url;

        if (depth < 2)
        {
            yield break;
        }

        var foundUrls = new HashSet<string>();
        var urlsToVisit = new Queue<LinkCandidate>();

        foundUrls.Add(url);
        urlsToVisit.Enqueue(new LinkCandidate(url, 1));

        while (urlsToVisit.TryDequeue(out var currentUrl))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            var htmlDocument = new HtmlDocument();

            try
            {
                using var response = await _httpClient.GetAsync(currentUrl.Url, cancellationToken);
                if (!response.IsSuccessStatusCode || !response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.InvariantCulture) != false)
                {
                    continue;
                }

                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                htmlDocument.Load(contentStream);
            }
            catch
            {
                continue;
            }

            foreach (var node in htmlDocument.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                var foundUrl = node.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrWhiteSpace(foundUrl) || foundUrl == "/")
                {
                    continue;
                }

                foundUrl = foundUrl.Trim().ToLowerInvariant();

                if (foundUrl.StartsWith('#') || foundUrl.StartsWith("mailto:") || foundUrl.StartsWith("tel:"))
                {
                    continue;
                }

                var path = Path.GetExtension(foundUrl);
                if (!string.IsNullOrWhiteSpace(path) && ignoredExtensions.Contains(path))
                {
                    continue;
                }

                if (foundUrl.StartsWith('/'))
                {
                    foundUrl = $"{rootUri.Scheme}://{rootUri.Host}{foundUrl}";
                }

                if (!foundUrl.StartsWith(url))
                {
                    continue;
                }

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
                    yield return foundUrl;

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

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("The provided URL is invalid.", nameof(url));
        }

        using var response = await _httpClient.GetAsync(uri, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Failed to retrieve content from '{url}'. Status code: {response.StatusCode}.");
        }

        if (response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.InvariantCulture) != true)
        {
            return new PageDetails(url, null, null, null, null);
        }

        var htmlDocument = new HtmlDocument();
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        htmlDocument.Load(contentStream);

        var root = htmlDocument.DocumentNode;

        var title = root.SelectSingleNode("//title")?.InnerText.Trim();
        var description = root.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "").Trim();
        var keywords = root.SelectSingleNode("//meta[@name='keywords']")?.GetAttributeValue("content", "").Trim();

        var contentNode = root.SelectSingleNode("//article") ?? root.SelectSingleNode("//main") ?? root.SelectSingleNode("//div[contains(@class, 'content')]") ?? root;
        var stringBuilder = new StringBuilder();

        foreach (var node in contentNode.SelectNodes(".//h1 | .//h2 | .//h3 | .//h4 | .//h5 | .//h6 | .//p | .//ul | .//ol") ?? Enumerable.Empty<HtmlNode>())
        {
            var trimmedText = SpaceRegex().Replace(node.InnerText.Trim(), " ");
            switch (node.Name)
            {
                case "h1":
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
                    var paragraphContent = new StringBuilder();
                    foreach (var child in node.ChildNodes)
                    {
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

        return new PageDetails(url, title, description, keywords, stringBuilder.ToString());
    }

    private record LinkCandidate(string Url, int Depth);

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
