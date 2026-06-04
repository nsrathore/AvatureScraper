using System.Threading.Channels;
using AvatureScraper.Models;
using Microsoft.Extensions.Logging;

namespace AvatureScraper.Services;

/// <summary>
/// Core scraping engine.
///
/// Uses System.Threading.Channels as a producer/consumer queue so site-level
/// concurrency is bounded without blocking the thread pool. Each site is
/// processed by <see cref="ScrapeSiteAsync"/> which tries career path variants
/// and pagination automatically.
/// </summary>
public sealed class ScraperService(
    IHttpClientFactory httpClientFactory,
    FeedParser         feedParser,
    HtmlParser         htmlParser,
    ILogger<ScraperService> logger)
{
    // ── Avature URL builders ──────────────────────────────────────────────────

    private const int JobsPerPage    = 100;
    private const int MaxPagesPerSite = 50;   // safety cap → 5,000 jobs/site

    private static string FeedUrl(string baseUrl, string careerPath, int offset) =>
        $"{baseUrl}{careerPath}/SearchJobs/feed/?jobRecordsPerPage={JobsPerPage}&jobOffset={offset}";

    private static string SearchUrl(string baseUrl, string careerPath, int offset) =>
        $"{baseUrl}{careerPath}/SearchJobs?jobRecordsPerPage={JobsPerPage}&jobOffset={offset}";

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Scrape all provided sites concurrently, bounded by <paramref name="maxConcurrency"/>.
    /// Returns all collected jobs and per-site results.
    /// </summary>
    public async Task<(List<Job> Jobs, List<SiteResult> Results)> ScrapeAllAsync(
        IReadOnlyList<SiteConfig> sites,
        int maxConcurrency = 12,
        CancellationToken ct = default)
    {
        var allJobs    = new System.Collections.Concurrent.ConcurrentBag<Job>();
        var allResults = new System.Collections.Concurrent.ConcurrentBag<SiteResult>();

        // Channel as a semaphore-like bounded concurrency mechanism
        var sem = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = sites.Select(async site =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var (jobs, result) = await ScrapeSiteAsync(site, ct);
                foreach (var j in jobs) allJobs.Add(j);
                allResults.Add(result);

                var status = result.JobsFound > 0
                    ? $"✓ {result.JobsFound} jobs via {result.Endpoint}"
                    : $"✗ {result.Error ?? "no jobs"}";
                logger.LogInformation("[{Done}/{Total}] {Sub,-35} {Status}",
                    allResults.Count, sites.Count, site.Subdomain, status);
            }
            finally { sem.Release(); }
        });

        await Task.WhenAll(tasks);

        return (allJobs.ToList(), allResults.ToList());
    }

    // ── Site-level scraping ────────────────────────────────────────────────────

    private async Task<(List<Job> Jobs, SiteResult Result)> ScrapeSiteAsync(
        SiteConfig site, CancellationToken ct)
    {
        // Probe multiple career path variants; stop at first that yields jobs
        foreach (var careerPath in SiteDiscoveryService.CareerPathCandidates)
        {
            var (jobs, endpoint) = await TryCareerPathAsync(site, careerPath, ct);
            if (jobs is not null)
            {
                return (jobs, new SiteResult
                {
                    Subdomain  = site.Subdomain,
                    BaseUrl    = site.BaseUrl,
                    JobsFound  = jobs.Count,
                    Endpoint   = endpoint,
                });
            }
        }

        return ([], new SiteResult
        {
            Subdomain = site.Subdomain,
            BaseUrl   = site.BaseUrl,
            JobsFound = 0,
            Endpoint  = "none",
            Error     = "no working career path found",
        });
    }

    /// <summary>
    /// Try one career path prefix using the feed endpoint (preferred) then HTML fallback.
    /// Returns (jobs, endpointName) or (null, "") if neither worked.
    /// </summary>
    private async Task<(List<Job>? Jobs, string Endpoint)> TryCareerPathAsync(
        SiteConfig site, string careerPath, CancellationToken ct)
    {
        // ── Strategy 1: RSS feed ──────────────────────────────────────────────
        var feedXml = await FetchAsync(site.BaseUrl, careerPath, isFeed: true, offset: 0, ct);
        if (feedXml is not null && (feedXml.Contains("<item>") || feedXml.Contains("<entry>")))
        {
            var activeSite = site with { CareerPath = careerPath };
            var jobs       = feedParser.ParseFeed(feedXml, activeSite);

            if (jobs.Count > 0)
            {
                logger.LogDebug("{Sub}: feed page 1 → {Count} jobs", site.Subdomain, jobs.Count);
                var allJobs = new List<Job>(jobs);

                // Paginate
                for (var page = 1; jobs.Count == JobsPerPage && page < MaxPagesPerSite; page++)
                {
                    var pageXml = await FetchAsync(site.BaseUrl, careerPath, true, page * JobsPerPage, ct);
                    if (pageXml is null) break;

                    jobs = feedParser.ParseFeed(pageXml, activeSite);
                    if (jobs.Count == 0) break;

                    allJobs.AddRange(jobs);
                    logger.LogDebug("{Sub}: feed page {Page} → +{Count} jobs",
                        site.Subdomain, page + 1, jobs.Count);
                }

                return (allJobs, "feed");
            }
        }

        // ── Strategy 2: HTML search page ─────────────────────────────────────
        var html = await FetchAsync(site.BaseUrl, careerPath, isFeed: false, offset: 0, ct);
        if (html is not null && html.Contains("JobDetail"))
        {
            var activeSite          = site with { CareerPath = careerPath };
            var (pageJobs, total)   = htmlParser.ParseSearchPage(html, activeSite);

            if (pageJobs.Count > 0 || total > 0)
            {
                logger.LogDebug("{Sub}: html page 1 → {Count} jobs (total≈{Total})",
                    site.Subdomain, pageJobs.Count, total);

                var allJobs = new List<Job>(pageJobs);

                for (var page = 1; allJobs.Count < total && page < MaxPagesPerSite; page++)
                {
                    var pageHtml = await FetchAsync(site.BaseUrl, careerPath, false, page * JobsPerPage, ct);
                    if (pageHtml is null) break;

                    var (moreJobs, _) = htmlParser.ParseSearchPage(pageHtml, activeSite);
                    if (moreJobs.Count == 0) break;

                    allJobs.AddRange(moreJobs);
                    logger.LogDebug("{Sub}: html page {Page} → +{Count} jobs",
                        site.Subdomain, page + 1, moreJobs.Count);
                }

                return (allJobs, "html");
            }
        }

        return (null, string.Empty);
    }

    // ── HTTP fetch ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch one URL using the named HttpClient (Polly retry configured in Program.cs).
    /// Returns the response body or null on failure.
    /// </summary>
    private async Task<string?> FetchAsync(
        string baseUrl, string careerPath, bool isFeed, int offset, CancellationToken ct)
    {
        var url    = isFeed ? FeedUrl(baseUrl, careerPath, offset)
                            : SearchUrl(baseUrl, careerPath, offset);
        var client = httpClientFactory.CreateClient("scraper");

        try
        {
            using var response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("HTTP {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug("Fetch error {Url}: {Msg}", url, ex.Message);
            return null;
        }
    }
}
