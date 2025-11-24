using HtmlAgilityPack;
using KamiYomu.CrawlerAgents.Core;
using KamiYomu.CrawlerAgents.Core.Catalog;
using KamiYomu.CrawlerAgents.Core.Catalog.Builders;
using KamiYomu.CrawlerAgents.Core.Catalog.Definitions;
using KamiYomu.CrawlerAgents.Core.Inputs;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Web;
using Page = KamiYomu.CrawlerAgents.Core.Catalog.Page;

namespace KamiYomu.CrawlerAgents.MangaPark;

[DisplayName("KamiYomu Crawler Agent – mangapark.net")]
[CrawlerSelect("Language", "Chapter Translation language, translated fields such as Titles and Descriptions", ["en", "pt_br", "pt",])]
public partial class MangaParkCrawlerAgent : AbstractCrawlerAgent, ICrawlerAgent
{
    private bool _disposed = false;
    private readonly Uri _baseUri;
    private readonly string _language;
    private Lazy<Task<IBrowser>> _browser;
    private bool disposedValue;
    public MangaParkCrawlerAgent(IDictionary<string, object> options) : base(options)
    {
        _baseUri = new Uri("https://mangapark.net");
        _browser = new Lazy<Task<IBrowser>>(CreateBrowserAsync, true);
        if (Options.TryGetValue("Language", out var language) && language is string languageValue)
        {
            _language = languageValue;
        }
        else
        {
            _language = "en";
        }

    }

    public Task<IBrowser> GetBrowserAsync() => _browser.Value;

    private async Task<IBrowser> CreateBrowserAsync()
    {
        var launchOptions = new LaunchOptions
        {
            Headless = true,
            Timeout = TimeoutMilliseconds,
            Args = [
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-dev-shm-usage"
            ]
        };

        return await Puppeteer.LaunchAsync(launchOptions);
    }

    public Task<Uri> GetFaviconAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new Uri($"{_baseUri}/static-assets/img/favicon.ico"));
    }

    public async Task<PagedResult<Manga>> SearchAsync(string titleName, PaginationOptions paginationOptions, CancellationToken cancellationToken)
    {
        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();
        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);
        var pageNumber = string.IsNullOrWhiteSpace(paginationOptions?.ContinuationToken)
                        ? 1
                        : int.Parse(paginationOptions.ContinuationToken);

        var targetUri = new Uri(new Uri(_baseUri.ToString()), $"search?word={UrlEncoder.Default.Encode(titleName)}&lang={_language}&sortby=field_score&ig_genres=1&page={pageNumber}");
        await page.GoToAsync(targetUri.ToString(), new NavigationOptions
        {
            WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load },
            Timeout = TimeoutMilliseconds
        });

        var content = await page.GetContentAsync();

        var document = new HtmlDocument();
        document.LoadHtml(content);

        List<Manga> mangas = [];
        if (pageNumber > 0)
        {
            var nodes = document.DocumentNode.SelectNodes("//*[contains(@*[local-name()='q:key'], 'q4_9')]");

            if (nodes != null)
            {
                foreach (var divNode in nodes)
                {
                    Manga manga = ConvertToMangaFromList(divNode);
                    mangas.Add(manga);
                }
            }
        }

        return PagedResultBuilder<Manga>.Create()
            .WithData(mangas)
            .WithPaginationOptions(new PaginationOptions((pageNumber + 1).ToString()))
            .Build();
    }

    private Manga ConvertToMangaFromList(HtmlNode divNode)
    {
        // --- Title & Cover ---
        var aNode = divNode
            .Descendants("a")
            .FirstOrDefault(a => a.GetAttributeValue("href", "").Contains("/title/"));
        var href = aNode?.GetAttributeValue("href", string.Empty);

        var id = string.Empty;
        if (!string.IsNullOrEmpty(href))
            id = href.Split('/').Last();

        var imgNode = aNode?
            .Descendants("img")
            .FirstOrDefault(img => img.GetAttributeValue("title", "") != "");
        var coverUrl = NormalizeUrl(imgNode?.GetAttributeValue("src", string.Empty));
        var coverFileName = Path.GetFileName(coverUrl);
        var title = imgNode?.GetAttributeValue("title", string.Empty);

        var websiteUrl = NormalizeUrl(href);

        // --- Alternative Titles ---
        var altTitlesDiv = divNode
            .Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "lA_0");

        var altTitles = new List<string>();
        if (altTitlesDiv != null)
        {
            var spans = altTitlesDiv
                .Descendants("span")
                .Where(s => s.GetAttributeValue("q:key", "") == "Ts_1");

            foreach (var altTitleSpan in spans)
            {
                var text = altTitleSpan.InnerText.Trim();
                if (!string.IsNullOrEmpty(text))
                    altTitles.Add(text);
            }
        }

        // --- Author ---
        var authorDiv = divNode
            .Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "6N_0");

        string author = string.Empty;
        if (authorDiv != null)
        {
            var authorSpan = authorDiv
                .Descendants("span")
                .FirstOrDefault(s => s.GetAttributeValue("q:key", "") == "Ts_1");

            if (authorSpan != null)
                author = authorSpan.InnerText.Trim();
        }

        // --- Genres ---
        var genresDiv = divNode
            .Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "HB_9");

        var genres = new List<string>();
        if (genresDiv != null)
        {
            var genreSpans = genresDiv
                .Descendants("span")
                .Where(s => s.GetAttributeValue("q:key", "") == "kd_0");

            foreach (var genreSpan in genreSpans)
            {
                var text = genreSpan.InnerText.Trim();
                if (!string.IsNullOrEmpty(text))
                    genres.Add(text);
            }
        }

        // --- Last Chapter ---
        var lastChapterDiv = divNode
            .Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "R7_8");

        string rawText = string.Empty;
        if (lastChapterDiv != null)
        {
            var aNodeLastChapter = lastChapterDiv
                .Descendants("a")
                .FirstOrDefault();
            rawText = aNodeLastChapter?.InnerText.Trim() ?? string.Empty;
        }

        // --- Normalize Volume/Chapter ---
        string volume = string.Empty;
        string chapter = string.Empty;

        if (!string.IsNullOrEmpty(rawText))
        {
            var volChapterPattern = VolumeRegex();
            var chapterOnlyPattern = ChapterRegex();

            var match = volChapterPattern.Match(rawText);
            if (match.Success)
            {
                volume = match.Groups["vol"].Value;
                chapter = match.Groups["ch"].Value;
            }
            else
            {
                var chMatch = chapterOnlyPattern.Match(rawText);
                if (chMatch.Success)
                {
                    volume = string.Empty;
                    chapter = chMatch.Groups["ch"].Value;
                }
                else
                {
                    chapter = rawText;
                }
            }
        }

        // --- Build Manga ---
        var manga = MangaBuilder.Create()
            .WithId(id)
            .WithTitle(title)
            .WithAuthors([author])
            .WithDescription("No Description Available")
            .WithCoverUrl(new Uri(coverUrl))
            .WithCoverFileName(coverFileName)
            .WithWebsiteUrl(websiteUrl)
            .WithAlternativeTitles(
                altTitles.Select((p, i) => new { i = i.ToString(), p })
                         .ToDictionary(x => x.i, x => x.p))
            .WithLatestChapterAvailable(decimal.TryParse(chapter, out var chapterResult) ? chapterResult : 0)
            .WithLastVolumeAvailable(decimal.TryParse(volume, out var volumeResult) ? volumeResult : 0)
            .WithTags([.. genres])
            .WithIsFamilySafe(!genres.Any(g => IsGenreNotFamilySafe(g)))
            .Build();

        return manga;
    }

    public async Task<Manga> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();
        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        var finalUrl = new Uri(_baseUri, $"title/{id}").ToString();
        var response = await page.GoToAsync(finalUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        });

        var content = await response.TextAsync();
        var document = new HtmlDocument();
        document.LoadHtml(content);
        var rootNode = document.DocumentNode.SelectSingleNode("//*[contains(@*[local-name()='q:key'], 'g0_13')]"); ;
        Manga manga = ConvertToMangaFromSingleBook(rootNode, id);

        return manga;
    }

    private Manga ConvertToMangaFromSingleBook(HtmlNode divNode, string id)
    {
        // --- Title & Id ---
        var aNode = divNode.Descendants("a")
            .FirstOrDefault(a => a.GetAttributeValue("href", "").Contains("/title/"));
        var href = aNode?.GetAttributeValue("href", string.Empty);
        var title = aNode?.InnerText.Trim();

        // --- Cover ---
        var imgNode = divNode.Descendants("img").FirstOrDefault();
        var coverUrl = NormalizeUrl(imgNode?.GetAttributeValue("src", string.Empty));
        var coverFileName = Path.GetFileName(coverUrl);

        // --- Alternative Titles ---
        var altTitlesDiv = divNode.Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "tz_2");
        var altTitles = new List<string>();
        if (altTitlesDiv != null)
        {
            var text = altTitlesDiv.InnerText.Trim();
            if (!string.IsNullOrEmpty(text))
                altTitles.AddRange(text.Split("/").Select(p => p.Trim()));
        }

        // --- Authors ---
        var authorsDiv = divNode.Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "tz_4");
        var authors = new List<string>();
        if (authorsDiv != null)
        {
            var authorLinks = authorsDiv.Descendants("a");
            foreach (var link in authorLinks)
            {
                var text = link.InnerText.Trim();
                if (!string.IsNullOrEmpty(text))
                    authors.Add(text);
            }
        }

        // --- Genres ---
        var genresDiv = divNode.Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "30_2");
        var genres = new List<string>();
        if (genresDiv != null)
        {
            var genreSpans = genresDiv.Descendants("span")
                .Where(s => s.GetAttributeValue("q:key", "") == "kd_0");
            foreach (var span in genreSpans)
            {
                var text = span.InnerText.Trim();
                if (!string.IsNullOrEmpty(text))
                    genres.Add(text);
            }
        }

        // --- Description ---
        var descriptionDiv = divNode.Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("class", "").Contains("limit-html prose"));
        var description = string.Empty;
        if (descriptionDiv != null)
        {
            var paragraphs = descriptionDiv.Descendants("div")
                .Where(d => d.GetAttributeValue("class", "").Contains("limit-html-p"));
            description = string.Join("\n\n", paragraphs.Select(p => p.InnerText.Trim()));
        }

        // --- Release Status ---
        var releaseStatusDiv = divNode.Descendants("span")
            .FirstOrDefault(s => s.GetAttributeValue("q:key", "") == "Yn_5");
        var releaseStatus = releaseStatusDiv?.InnerText.Trim() ?? string.Empty;

        // --- Build Manga object ---
        var manga = MangaBuilder.Create()
            .WithId(id)
            .WithTitle(title)
            .WithAlternativeTitles(
                altTitles.Select((p, i) => new { i = i.ToString(), p })
                         .ToDictionary(x => x.i, x => x.p))
            .WithDescription(description)
            .WithAuthors([.. authors])
            .WithTags([.. genres])
            .WithCoverUrl(new Uri(coverUrl))
            .WithCoverFileName(coverFileName)
            .WithWebsiteUrl(NormalizeUrl(href))
            .WithIsFamilySafe(!genres.Any(g => IsGenreNotFamilySafe(g)))
            .WithReleaseStatus(releaseStatus.ToLowerInvariant() switch
            {
                "Completed" => ReleaseStatus.Completed,
                "Hiatus" => ReleaseStatus.OnHiatus,
                "Cancelled" => ReleaseStatus.Cancelled,
                _ => ReleaseStatus.Continuing,
            })
            .Build();

        return manga;
    }

    public async Task<PagedResult<Chapter>> GetChaptersAsync(Manga manga, PaginationOptions paginationOptions, CancellationToken cancellationToken)
    {
        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();
        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        var finalUrl = new Uri(_baseUri, $"title/{manga.Id}").ToString();
        var response = await page.GoToAsync(finalUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        });

        var content = await response.TextAsync();
        var document = new HtmlDocument();
        document.LoadHtml(content);
        var rootNode = document.DocumentNode.SelectSingleNode("//div[@data-name='chapter-list']");
        IEnumerable<Chapter> chapters = ConvertChaptersFromSingleBook(manga, rootNode);

        return PagedResultBuilder<Chapter>.Create()
                                          .WithPaginationOptions(new PaginationOptions(chapters.Count(), chapters.Count(), chapters.Count()))
                                          .WithData(chapters)
                                          .Build();
    }


    private List<Chapter> ConvertChaptersFromSingleBook(Manga manga, HtmlNode rootNode)
    {
        var chapters = new List<Chapter>();

        // Each chapter block is a div with q:key="8t_8"
        var chapterDivs = rootNode
            .Descendants("div")
            .Where(d => d.GetAttributeValue("q:key", "") == "8t_8");

        foreach (var chapterDiv in chapterDivs)
        {
            // --- Anchor node ---
            var aNode = chapterDiv.Descendants("a")
                .FirstOrDefault(a => a.InnerText.Trim().Contains("Ch") || a.InnerText.Trim().Contains("Chapter"));
            if (aNode == null) continue;

            // --- Uri ---
            var href = aNode.GetAttributeValue("href", string.Empty);
            var uriString = string.IsNullOrWhiteSpace(href)
                ? $"{_baseUri}/title/{manga.Id}/chapter/fallback"
                : NormalizeUrl(href);
            var uri = new Uri(uriString, UriKind.RelativeOrAbsolute);

            // --- Chapter Id ---
            string chapterId = null;
            if (!string.IsNullOrEmpty(uriString))
            {
                var uriObj = new Uri(uriString, UriKind.RelativeOrAbsolute);
                chapterId = uriObj.Segments.Last().Trim('/');
            }
            if (string.IsNullOrWhiteSpace(chapterId))
                chapterId = Guid.NewGuid().ToString(); // fallback unique id

            // --- Raw label ---
            var label = aNode.InnerText.Trim();

            // --- Extra title after colon ---
            var extraTitleSpan = chapterDiv.Descendants("span")
                .FirstOrDefault(s => s.GetAttributeValue("q:key", "") == "8t_1");
            var extraTitle = extraTitleSpan?.InnerText.Trim().TrimStart(':').Trim() ?? string.Empty;

            // --- Regex parse for Volume / Chapter / Title ---
            var chapterPattern = new Regex(
                @"^(?:Vol\.?(?<vol>[0-9]+|TBE|TBD))?\s*(?:Ch\.?|Chapter)\s*(?<ch>[0-9]+)(?::\s*(?<title>.+))?$",
                RegexOptions.IgnoreCase
            );

            var match = chapterPattern.Match(label);
            string volumeStr = match.Groups["vol"].Success ? match.Groups["vol"].Value : string.Empty;
            string numberStr = match.Groups["ch"].Success ? match.Groups["ch"].Value : string.Empty;
            string parsedTitle = match.Groups["title"].Success ? match.Groups["title"].Value : string.Empty;

            // --- Normalize values ---
            decimal volume = 0;
            if (!string.IsNullOrEmpty(volumeStr) && decimal.TryParse(volumeStr, out var volNum))
                volume = volNum;

            decimal number = 0;
            if (!string.IsNullOrEmpty(numberStr) && decimal.TryParse(numberStr, out var num))
                number = num;

            // --- Title (combine label + extra title or parsed title) ---
            var title = string.IsNullOrWhiteSpace(label)
                ? $"Chapter {chapterId}"
                : HttpUtility.HtmlDecode(
                    $"{label}{(string.IsNullOrEmpty(extraTitle) ? "" : $": {extraTitle}")}{(string.IsNullOrEmpty(parsedTitle) ? "" : $": {parsedTitle}")}"
                );

            // --- Build Chapter object with guaranteed values ---
            var chapter = ChapterBuilder.Create()
                .WithId(chapterId)
                .WithTitle(title)
                .WithParentManga(manga)
                .WithVolume(volume > 0 ? volume : 0)
                .WithNumber(number > 0 ? number : 0)
                .WithUri(uri)
                .WithTranslatedLanguage(_language)
                .Build();

            chapters.Add(chapter);
        }

        return chapters;
    }

    public async Task<IEnumerable<Core.Catalog.Page>> GetChapterPagesAsync(Chapter chapter, CancellationToken cancellationToken)
    {
        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();

        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        await page.GoToAsync(chapter.Uri.ToString(), new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        }); 
        
        var content = await page.GetContentAsync();
        var document = new HtmlDocument();
        document.LoadHtml(content);

        var pageNodes = document.DocumentNode.SelectNodes("//div[@id='images']//div[@data-name='image-item']");
        return ConvertToChapterPages(chapter, pageNodes);
    }

    private async Task PreparePageForNavigationAsync(IPage page)
    {
        page.Console += (sender, e) =>
        {
            // e.Message contains the console message
            Logger?.LogDebug($"[Browser Console] {e.Message.Type}: {e.Message.Text}");

            // You can also inspect arguments
            if (e.Message.Args != null)
            {
                foreach (var arg in e.Message.Args)
                {
                    Logger?.LogDebug($"   Arg: {arg.RemoteObject.Value}");
                }
            }
        };



        await page.EvaluateExpressionOnNewDocumentAsync(@"
        // Neutralize devtools detection
        const originalLog = console.log;
        console.log = function(...args) {
            if (args.length === 1 && args[0] === '[object HTMLDivElement]') {
                return; // skip detection trick
            }
            return originalLog.apply(console, args);
        };

        // Override reload to do nothing
        window.location.reload = () => console.log('Reload prevented');
    ");

        await page.EmulateTimezoneAsync("America/Toronto");

        var fixedDate = DateTime.Now;

        var fixedDateIso = fixedDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        await page.EvaluateExpressionOnNewDocumentAsync($@"
            // Freeze time to a specific date
            const fixedDate = new Date('{fixedDateIso}');
            Date = class extends Date {{
                constructor(...args) {{
                    if (args.length === 0) {{
                        return fixedDate;
                    }}
                    return super(...args);
                }}
                static now() {{
                    return fixedDate.getTime();
                }}
            }};
        ");

        await page.SetCookieAsync(new CookieParam
        {
            Name = "nsfw",
            Value = "2",
            Domain = ".mangapark.net",
            Path = "/",
            HttpOnly = false,
            Secure = true
        });

        await page.SetCookieAsync(new CookieParam
        {
            Name = "theme",
            Value = "mdark",
            Domain = ".mangapark.net",
            Path = "/",
            HttpOnly = false,
            Secure = true
        });
    }

    private static bool IsGenreNotFamilySafe(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        return p.Contains("adult", StringComparison.OrdinalIgnoreCase)
            || p.Contains("harem", StringComparison.OrdinalIgnoreCase)
            || p.Contains("hentai", StringComparison.OrdinalIgnoreCase)
            || p.Contains("ecchi", StringComparison.OrdinalIgnoreCase)
            || p.Contains("violence", StringComparison.OrdinalIgnoreCase)
            || p.Contains("smut", StringComparison.OrdinalIgnoreCase)
            || p.Contains("shota", StringComparison.OrdinalIgnoreCase)
            || p.Contains("sexual", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<Page> ConvertToChapterPages(Chapter chapter, HtmlNodeCollection pageNodes)
    {
        if (pageNodes == null)
            return Enumerable.Empty<Page>();

        var pages = new List<Page>();

        foreach (var node in pageNodes)
        {
            // Each node is a <div data-name="image-item">, so find the <img>
            var imgNode = node.SelectSingleNode(".//img");
            if (imgNode == null) continue;

            // Id attribute is like "p-1", "p-2"
            var idAttr = imgNode.GetAttributeValue("id", "");
            if (!idAttr.StartsWith("p-")) continue;

            // Parse page number
            if (!decimal.TryParse(idAttr.Substring(2), out decimal pageNumber))
                continue;

            // Image URL from src
            var imageUrl = imgNode.GetAttributeValue("src", null);
            if (string.IsNullOrEmpty(imageUrl)) continue;

            // Build Page object with guaranteed values
            var page = PageBuilder.Create()
                .WithChapterId(chapter.Id)
                .WithId(idAttr)
                .WithPageNumber(pageNumber > 0 ? pageNumber : 0)
                .WithImageUrl(new Uri(imageUrl))
                .WithParentChapter(chapter)
                .Build();

            pages.Add(page);
        }

        return pages;
    }

    private string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (!url.StartsWith("/") && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        var resolved = new Uri(_baseUri, url);
        return resolved.ToString();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // TODO: dispose managed state (managed objects)
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
        }
    }

    ~MangaParkCrawlerAgent()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"(?:Ch\.?|Chapter)\s*(?<ch>[0-9]+(?:\s*\[End\])?)", RegexOptions.IgnoreCase, "en-CA")]
    private static partial Regex VolumeRegex();

    [GeneratedRegex(@"Vol\.?\s*(?<vol>[0-9]+|TBD)\s*(?:Ch\.?|Chapter)\s*(?<ch>[0-9]+(?:\s*\[End\])?)", RegexOptions.IgnoreCase, "en-CA")]
    private static partial Regex ChapterRegex();
}
