using ChatAIze.RabbitHole;

var scraper = new WebsiteScraper();

await foreach (var link in scraper.ScrapeLinksAsync("https://chataize.com"))
{
    Console.WriteLine(link);
}

var homePageContent = await scraper.ScrapeContentAsync("https://chataize.com");
Console.WriteLine(homePageContent);
