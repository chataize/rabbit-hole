﻿using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using HtmlAgilityPack;

namespace ChatAIze.RabbitHole;

public sealed class WebsiteScraper
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

    private readonly HttpClient _httpClient = new();

    public async IAsyncEnumerable<string> ScrapeLinksAsync(string? url, int depth = 1, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            yield break;
        }

        url = url.Trim().ToLowerInvariant();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var rootUri))
        {
            yield break;
        }

        if (depth < 1)
        {
            yield return url;
            yield break;
        }

        var foundUrls = new HashSet<string>();
        var urlsToVisit = new Queue<LinkCandidate>();

        urlsToVisit.Enqueue(new LinkCandidate(url, 0));

        while (urlsToVisit.TryDequeue(out var currentUrl))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (currentUrl.Depth >= depth)
            {
                continue;
            }

            using var response = await _httpClient.GetAsync(currentUrl.Url, cancellationToken);
            if (!response.IsSuccessStatusCode || !response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.InvariantCulture) != false)
            {
                continue;
            }

            var htmlDocument = new HtmlDocument();
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

            htmlDocument.Load(contentStream);

            foreach (var node in htmlDocument.DocumentNode.SelectNodes("//a[@href]"))
            {
                var foundUrl = node.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrWhiteSpace(foundUrl) || foundUrl == "/")
                {
                    continue;
                }

                foundUrl = foundUrl.Trim().ToLowerInvariant();

                if (foundUrl.StartsWith('#') || foundUrl.StartsWith("mailto:"))
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

                if (foundUrls.Add(foundUrl))
                {
                    urlsToVisit.Enqueue(new LinkCandidate(foundUrl, currentUrl.Depth + 1));
                    yield return foundUrl;
                }
            }
        }
    }

    public async Task<string> ScrapeContentAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode || !response.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.InvariantCulture) != false)
        {
            return string.Empty;
        }

        var htmlDocument = new HtmlDocument();
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        htmlDocument.Load(contentStream);

        var root = htmlDocument.DocumentNode;
        var contentNode = root.SelectSingleNode("//article") ?? root.SelectSingleNode("//main") ?? root.SelectSingleNode("//div[contains(@class, 'content')]");

        if (contentNode == null)
        {
            return string.Empty;
        }

        var stringBuilder = new StringBuilder();
        foreach (var node in contentNode.SelectNodes(".//h1 | .//h2 | .//h3 | .//p"))
        {
            switch (node.Name)
            {
                case "h1":
                    stringBuilder.AppendLine($"# {node.InnerText.Trim()}");
                    stringBuilder.AppendLine();
                    break;
                case "h2":
                    stringBuilder.AppendLine($"## {node.InnerText.Trim()}");
                    stringBuilder.AppendLine();
                    break;
                case "h3":
                    stringBuilder.AppendLine($"### {node.InnerText.Trim()}");
                    stringBuilder.AppendLine();
                    break;
                case "p":
                    stringBuilder.AppendLine(node.InnerText.Trim());
                    stringBuilder.AppendLine();
                    break;
            }
        }

        return stringBuilder.ToString();
    }

    private record LinkCandidate(string Url, int Depth);
}