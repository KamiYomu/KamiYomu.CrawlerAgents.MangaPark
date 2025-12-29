using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;

using HtmlAgilityPack;

using KamiYomu.CrawlerAgents.Core;
using KamiYomu.CrawlerAgents.Core.Catalog;
using KamiYomu.CrawlerAgents.Core.Catalog.Builders;
using KamiYomu.CrawlerAgents.Core.Catalog.Definitions;
using KamiYomu.CrawlerAgents.Core.Inputs;

using Microsoft.Extensions.Logging;

using PuppeteerSharp;

using Page = KamiYomu.CrawlerAgents.Core.Catalog.Page;

namespace KamiYomu.CrawlerAgents.MangaPark;

[DisplayName("KamiYomu Crawler Agent – mangapark.net")]
[CrawlerSelect("Language", "Chapter Translation language, translated fields such as Titles and Descriptions", ["en", "pt_br", "pt"])]
[CrawlerSelect("Mirror", "MangaPark offers multiple mirror sites that may be online and useful. You can check their current status at https://mangaparkmirrors.pages.dev/",
    true, 0, [
        "https://mangapark.net",
        "https://mangapark.com",
        "https://mangapark.org",
        "https://mangapark.me",
        "https://mangapark.to",
        "https://comicpark.org",
        "https://comicpark.to",
        "https://readpark.org",
        "https://readpark.net",
        "https://parkmanga.com",
        "https://parkmanga.net",
        "https://parkmanga.org",
        "https://mpark.to"
    ])]
public partial class MangaParkCrawlerAgent : AbstractCrawlerAgent, ICrawlerAgent
{
    private bool _disposed = false;
    private readonly Uri _baseUri;
    private readonly string _language;
    private readonly Lazy<Task<IBrowser>> _browser;
    public MangaParkCrawlerAgent(IDictionary<string, object> options) : base(options)
    {
        _browser = new Lazy<Task<IBrowser>>(CreateBrowserAsync, true);
        _language = Options.TryGetValue("Language", out object? language) && language is string languageValue ? languageValue : "en";
        string mirrorUrl = Options.TryGetValue("Mirror", out object? mirror) && mirror is string mirrorValue ? mirrorValue : "https://mangapark.net";
        _baseUri = new Uri(mirrorUrl);
    }

    protected virtual async Task DisposeAsync(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (_browser.IsValueCreated)
                {
                    await _browser.Value.Result.CloseAsync();
                    _browser.Value.Result.Dispose();
                }
            }

            _disposed = true;
        }
    }

    ~MangaParkCrawlerAgent()
    {
        DisposeAsync(disposing: false);
    }

    // <inheritdoc/>
    public void Dispose()
    {
        DisposeAsync(disposing: true);
        GC.SuppressFinalize(this);
    }

    public Task<IBrowser> GetBrowserAsync()
    {
        return _browser.Value;
    }

    private async Task<IBrowser> CreateBrowserAsync()
    {
        LaunchOptions launchOptions = new()
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
        IBrowser browser = await GetBrowserAsync();
        using IPage page = await browser.NewPageAsync();
        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);
        int pageNumber = string.IsNullOrWhiteSpace(paginationOptions?.ContinuationToken)
                        ? 1
                        : int.Parse(paginationOptions.ContinuationToken);

        Uri targetUri = new(new Uri(_baseUri.ToString()), $"search?word={titleName}&lang={_language}&sortby=field_score&ig_genres=1&page={pageNumber}");
        _ = await page.GoToAsync(targetUri.ToString(), new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        });

        foreach (CookieParam? cookie in await page.GetCookiesAsync())
        {
            Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        }

        string content = await page.GetContentAsync();

        HtmlDocument document = new();
        document.LoadHtml(content);

        List<Manga> mangas = [];
        if (pageNumber > 0)
        {
            HtmlNodeCollection nodes = document.DocumentNode.SelectNodes("//*[contains(@*[local-name()='q:key'], 'q4_9')]");

            if (nodes != null)
            {
                foreach (HtmlNode divNode in nodes)
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
        HtmlNode? aNode = divNode
            .Descendants("a")
            .FirstOrDefault(a => a.GetAttributeValue("href", "").Contains("/title/"));
        string? href = aNode?.GetAttributeValue("href", string.Empty);

        string id = string.Empty;
        if (!string.IsNullOrEmpty(href))
        {
            id = href.Split('/').Last();
        }

        HtmlNode? imgNode = aNode?
            .Descendants("img")
            .FirstOrDefault(img => img.GetAttributeValue("title", "") != "");
        string coverUrl = NormalizeUrl(imgNode?.GetAttributeValue("src", string.Empty));
        string coverFileName = Path.GetFileName(coverUrl);
        string? title = imgNode?.GetAttributeValue("title", string.Empty);

        string websiteUrl = NormalizeUrl(href);

        // --- Alternative Titles ---
        HtmlNode? altTitlesDiv = divNode
            .Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "lA_0");

        List<string> altTitles = [];
        if (altTitlesDiv != null)
        {
            IEnumerable<HtmlNode> spans = altTitlesDiv
                .Descendants("span")
                .Where(s => s.GetAttributeValue("q:key", "") == "Ts_1");

            foreach (HtmlNode? altTitleSpan in spans)
            {
                string text = altTitleSpan.InnerText.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    altTitles.Add(text);
                }
            }
        }

        // --- Author ---
        HtmlNode? authorDiv = divNode
            .Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "6N_0");

        string author = string.Empty;
        if (authorDiv != null)
        {
            HtmlNode? authorSpan = authorDiv
                .Descendants("span")
                .FirstOrDefault(s => s.GetAttributeValue("q:key", "") == "Ts_1");

            if (authorSpan != null)
            {
                author = authorSpan.InnerText.Trim();
            }
        }

        // --- Genres ---
        HtmlNode? genresDiv = divNode
            .Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "HB_9");

        List<string> genres = [];
        if (genresDiv != null)
        {
            IEnumerable<HtmlNode> genreSpans = genresDiv
                .Descendants("span")
                .Where(s => s.GetAttributeValue("q:key", "") == "kd_0");

            foreach (HtmlNode? genreSpan in genreSpans)
            {
                string text = genreSpan.InnerText.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    genres.Add(text);
                }
            }
        }

        // --- Last Chapter ---
        HtmlNode? lastChapterDiv = divNode
            .Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "R7_8");

        string rawText = string.Empty;
        if (lastChapterDiv != null)
        {
            HtmlNode? aNodeLastChapter = lastChapterDiv
                .Descendants("a")
                .FirstOrDefault();
            rawText = aNodeLastChapter?.InnerText.Trim() ?? string.Empty;
        }

        // --- Normalize Volume/Chapter ---
        string volume = string.Empty;
        string chapter = string.Empty;

        if (!string.IsNullOrEmpty(rawText))
        {
            Regex volChapterPattern = VolumeRegex();
            Regex chapterOnlyPattern = ChapterRegex();

            Match match = volChapterPattern.Match(rawText);
            if (match.Success)
            {
                volume = match.Groups["vol"].Value;
                chapter = match.Groups["ch"].Value;
            }
            else
            {
                Match chMatch = chapterOnlyPattern.Match(rawText);
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
        Manga manga = MangaBuilder.Create()
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
            .WithLatestChapterAvailable(decimal.TryParse(chapter, out decimal chapterResult) ? chapterResult : 0)
            .WithLastVolumeAvailable(decimal.TryParse(volume, out decimal volumeResult) ? volumeResult : 0)
            .WithTags([.. genres])
            .WithOriginalLanguage(_language)
            .WithIsFamilySafe(!genres.Any(IsGenreNotFamilySafe))
            .Build();

        return manga;
    }

    public async Task<Manga> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        IBrowser browser = await GetBrowserAsync();
        using IPage page = await browser.NewPageAsync();
        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        string finalUrl = new Uri(_baseUri, $"title/{id}").ToString();
        IResponse response = await page.GoToAsync(finalUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        });

        foreach (CookieParam? cookie in await page.GetCookiesAsync())
        {
            Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        }

        string content = await page.GetContentAsync();
        HtmlDocument document = new();
        document.LoadHtml(content);
        HtmlNode rootNode = document.DocumentNode.SelectSingleNode("//*[contains(@*[local-name()='q:key'], 'g0_13')]"); ;
        Manga manga = ConvertToMangaFromSingleBook(rootNode, id);

        return manga;
    }

    private Manga ConvertToMangaFromSingleBook(HtmlNode divNode, string id)
    {
        // --- Title & Id ---
        HtmlNode? aNode = divNode.Descendants("a")
            .FirstOrDefault(a => a.GetAttributeValue("href", "").Contains("/title/"));
        string? href = aNode?.GetAttributeValue("href", string.Empty);
        string? title = aNode?.InnerText.Trim();

        // --- Cover ---
        HtmlNode? imgNode = divNode.Descendants("img").FirstOrDefault();
        string coverUrl = NormalizeUrl(imgNode?.GetAttributeValue("src", string.Empty));
        string coverFileName = Path.GetFileName(coverUrl);

        // --- Alternative Titles ---
        HtmlNode? altTitlesDiv = divNode.Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "tz_2");
        List<string> altTitles = [];
        if (altTitlesDiv != null)
        {
            string text = altTitlesDiv.InnerText.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                altTitles.AddRange(text.Split("/").Select(p => p.Trim()));
            }
        }

        // --- Authors ---
        HtmlNode? authorsDiv = divNode.Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "tz_4");
        List<string> authors = [];
        if (authorsDiv != null)
        {
            IEnumerable<HtmlNode> authorLinks = authorsDiv.Descendants("a");
            foreach (HtmlNode link in authorLinks)
            {
                string text = link.InnerText.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    authors.Add(text);
                }
            }
        }

        // --- Genres ---
        HtmlNode? genresDiv = divNode.Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("q:key", "") == "30_2");
        List<string> genres = [];
        if (genresDiv != null)
        {
            IEnumerable<HtmlNode> genreSpans = genresDiv.Descendants("span")
                .Where(s => s.GetAttributeValue("q:key", "") == "kd_0");
            foreach (HtmlNode? span in genreSpans)
            {
                string text = span.InnerText.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    genres.Add(text);
                }
            }
        }

        // --- Description ---
        HtmlNode? descriptionDiv = divNode.Descendants("div")
            .FirstOrDefault(d => d.GetAttributeValue("class", "").Contains("limit-html prose"));
        string description = string.Empty;
        if (descriptionDiv != null)
        {
            IEnumerable<HtmlNode> paragraphs = descriptionDiv.Descendants("div")
                .Where(d => d.GetAttributeValue("class", "").Contains("limit-html-p"));
            description = string.Join("\n\n", paragraphs.Select(p => p.InnerText.Trim()));
        }

        // --- Release Status ---
        HtmlNode? releaseStatusDiv = divNode.Descendants("span")
            .FirstOrDefault(s => s.GetAttributeValue("q:key", "") == "Yn_5");
        string releaseStatus = releaseStatusDiv?.InnerText.Trim() ?? string.Empty;

        // --- Build Manga object ---
        Manga manga = MangaBuilder.Create()
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
            .WithIsFamilySafe(!genres.Any(IsGenreNotFamilySafe))
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
        IBrowser browser = await GetBrowserAsync();
        using IPage page = await browser.NewPageAsync();
        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        string finalUrl = new Uri(_baseUri, $"title/{manga.Id}").ToString();
        IResponse response = await page.GoToAsync(finalUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        });

        foreach (CookieParam? cookie in await page.GetCookiesAsync())
        {
            Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        }

        string content = await page.GetContentAsync();

        HtmlDocument document = new();
        document.LoadHtml(content);
        HtmlNode rootNode = document.DocumentNode.SelectSingleNode("//div[@data-name='chapter-list']");
        IEnumerable<Chapter> chapters = ConvertChaptersFromSingleBook(manga, rootNode);

        return PagedResultBuilder<Chapter>.Create()
                                          .WithPaginationOptions(new PaginationOptions(chapters.Count(), chapters.Count(), chapters.Count()))
                                          .WithData(chapters)
                                          .Build();
    }

    private List<Chapter> ConvertChaptersFromSingleBook(Manga manga, HtmlNode rootNode)
    {
        List<Chapter> chapters = [];

        // Each chapter block is a div with q:key="8t_8"
        IEnumerable<HtmlNode> chapterDivs = rootNode
            .Descendants("div")
            .Where(d => d.GetAttributeValue("q:key", "") == "8t_8");

        foreach (HtmlNode? chapterDiv in chapterDivs)
        {
            // --- Anchor node ---
            HtmlNode? aNode = chapterDiv.Descendants("a")
                .FirstOrDefault(a => a.InnerText.Trim().Contains("Ch") || a.InnerText.Trim().Contains("Chapter"));
            if (aNode == null)
            {
                continue;
            }

            // --- Uri ---
            string href = aNode.GetAttributeValue("href", string.Empty);
            string uriString = string.IsNullOrWhiteSpace(href)
                ? $"{_baseUri}/title/{manga.Id}/chapter/fallback"
                : NormalizeUrl(href);
            Uri uri = new(uriString, UriKind.RelativeOrAbsolute);

            // --- Chapter Id ---
            string chapterId = null;
            if (!string.IsNullOrEmpty(uriString))
            {
                Uri uriObj = new(uriString, UriKind.RelativeOrAbsolute);
                chapterId = uriObj.Segments.Last().Trim('/');
            }
            if (string.IsNullOrWhiteSpace(chapterId))
            {
                chapterId = Guid.NewGuid().ToString(); // fallback unique id
            }

            // --- Raw label ---
            string label = aNode.InnerText.Trim();

            // --- Extra title after colon ---
            HtmlNode? extraTitleSpan = chapterDiv.Descendants("span")
                .FirstOrDefault(s => s.GetAttributeValue("q:key", "") == "8t_1");
            string extraTitle = extraTitleSpan?.InnerText.Trim().TrimStart(':').Trim() ?? string.Empty;

            // --- Regex parse for Volume / Chapter / Title ---
            Regex chapterPattern = new(
                @"^(?:Vol\.?(?<vol>[0-9]+|TBE|TBD))?\s*(?:Ch\.?|Chapter)\s*(?<ch>[0-9]+)(?::\s*(?<title>.+))?$",
                RegexOptions.IgnoreCase
            );

            Match match = chapterPattern.Match(label);
            string volumeStr = match.Groups["vol"].Success ? match.Groups["vol"].Value : string.Empty;
            string numberStr = match.Groups["ch"].Success ? match.Groups["ch"].Value : string.Empty;
            string parsedTitle = match.Groups["title"].Success ? match.Groups["title"].Value : string.Empty;

            // --- Normalize values ---
            decimal volume = 0;
            if (!string.IsNullOrEmpty(volumeStr) && decimal.TryParse(volumeStr, out decimal volNum))
            {
                volume = volNum;
            }

            decimal number = 0;
            if (!string.IsNullOrEmpty(numberStr) && decimal.TryParse(numberStr, out decimal num))
            {
                number = num;
            }

            // --- Title (combine label + extra title or parsed title) ---
            string title = string.IsNullOrWhiteSpace(label)
                ? $"Chapter {chapterId}"
                : HttpUtility.HtmlDecode(
                    $"{label}{(string.IsNullOrEmpty(extraTitle) ? "" : $": {extraTitle}")}{(string.IsNullOrEmpty(parsedTitle) ? "" : $": {parsedTitle}")}"
                );

            // --- Build Chapter object with guaranteed values ---
            Chapter chapter = ChapterBuilder.Create()
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

    public async Task<IEnumerable<Page>> GetChapterPagesAsync(Chapter chapter, CancellationToken cancellationToken)
    {
        IBrowser browser = await GetBrowserAsync();
        using IPage page = await browser.NewPageAsync();

        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        _ = await page.GoToAsync(chapter.Uri.ToString(), new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        });

        foreach (CookieParam? cookie in await page.GetCookiesAsync())
        {
            Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        }

        string content = await page.GetContentAsync();
        HtmlDocument document = new();
        document.LoadHtml(content);

        HtmlNodeCollection pageNodes = document.DocumentNode.SelectNodes("//div[@id='images']//div[@data-name='image-item']");
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
                foreach (IJSHandle? arg in e.Message.Args)
                {
                    Logger?.LogDebug($"   Arg: {arg.RemoteObject.Value}");
                }
            }
        };

        await page.SetCookieAsync([
            new CookieParam { Name = "nsfw", Value = "2", Domain = ".mangapark.net", Path = "/", HttpOnly = true, Secure = true },
            new CookieParam { Name = "theme", Value = "mdark", Domain = ".mangapark.net", Path = "/", HttpOnly = true, Secure = true }
        ]);

        _ = await page.EvaluateExpressionOnNewDocumentAsync(@"
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

        DateTime fixedDate = DateTime.Now;

        string fixedDateIso = fixedDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        _ = await page.EvaluateExpressionOnNewDocumentAsync($@"
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


    }

    private static bool IsGenreNotFamilySafe(string p)
    {
        return !string.IsNullOrWhiteSpace(p) && (p.Contains("adult", StringComparison.OrdinalIgnoreCase)
            || p.Contains("harem", StringComparison.OrdinalIgnoreCase)
            || p.Contains("hentai", StringComparison.OrdinalIgnoreCase)
            || p.Contains("ecchi", StringComparison.OrdinalIgnoreCase)
            || p.Contains("violence", StringComparison.OrdinalIgnoreCase)
            || p.Contains("smut", StringComparison.OrdinalIgnoreCase)
            || p.Contains("shota", StringComparison.OrdinalIgnoreCase)
            || p.Contains("sexual", StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<Page> ConvertToChapterPages(Chapter chapter, HtmlNodeCollection pageNodes)
    {
        if (pageNodes == null)
        {
            return [];
        }

        List<Page> pages = [];

        foreach (HtmlNode node in pageNodes)
        {
            // Each node is a <div data-name="image-item">, so find the <img>
            HtmlNode imgNode = node.SelectSingleNode(".//img");
            if (imgNode == null)
            {
                continue;
            }

            // Id attribute is like "p-1", "p-2"
            string idAttr = imgNode.GetAttributeValue("id", "");
            if (!idAttr.StartsWith("p-"))
            {
                continue;
            }

            // Parse page number
            if (!decimal.TryParse(idAttr[2..], out decimal pageNumber))
            {
                continue;
            }

            // Image URL from src
            string imageUrl = imgNode.GetAttributeValue("src", null);
            if (string.IsNullOrEmpty(imageUrl))
            {
                continue;
            }

            // Build Page object with guaranteed values
            Page page = PageBuilder.Create()
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
        {
            return string.Empty;
        }

        if (!url.StartsWith("/") && Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute))
        {
            return absolute.ToString();
        }

        Uri resolved = new(_baseUri, url);
        return resolved.ToString();
    }


    [GeneratedRegex(@"(?:Ch\.?|Chapter)\s*(?<ch>[0-9]+(?:\s*\[End\])?)", RegexOptions.IgnoreCase, "en-CA")]
    private static partial Regex VolumeRegex();

    [GeneratedRegex(@"Vol\.?\s*(?<vol>[0-9]+|TBD)\s*(?:Ch\.?|Chapter)\s*(?<ch>[0-9]+(?:\s*\[End\])?)", RegexOptions.IgnoreCase, "en-CA")]
    private static partial Regex ChapterRegex();
}
