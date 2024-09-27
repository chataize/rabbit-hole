using ChatAIze.RabbitHole;

var scraper = new WebsiteScraper();

await foreach (var link in scraper.ScrapeLinksAsync("https://smmlegal.pl", 2))
{
    Console.WriteLine(link);
}

var homePageContent = await scraper.ScrapeContentAsync("https://smmlegal.pl/en/transformations/6282/");
Console.WriteLine(homePageContent);
