namespace ChatAIze.RabbitHole;

/// <summary>
/// Represents the extracted metadata and normalized content of a web page.
/// </summary>
/// <param name="Url">The URL that was requested for extraction.</param>
/// <param name="Title">The page title, if available; otherwise null or an empty string.</param>
/// <param name="Description">The meta description, if available; otherwise null or an empty string.</param>
/// <param name="Keywords">The meta keywords, if available; otherwise null or an empty string.</param>
/// <param name="Content">The extracted content rendered as Markdown-like text; null when the response is not HTML and otherwise possibly empty.</param>
/// <remarks>
/// Content is produced by selecting common text elements and normalizing whitespace to make the output easier to scan.
/// </remarks>
public sealed record PageDetails(string Url, string? Title, string? Description, string? Keywords, string? Content);
