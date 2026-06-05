using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Extensions.Http;
using AvatureScraper.Services;

// ── Bootstrap ─────────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// Logging: console with timestamps
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(opts =>
{
    opts.IncludeScopes   = false;
    opts.TimestampFormat = "HH:mm:ss  ";
    opts.SingleLine      = true;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

// ── HttpClient configuration ──────────────────────────────────────────────────

// Polly retry policy: 1 retry with a short delay.
// Only retries on transient network errors (not 4xx/5xx — those are skipped immediately).
var retryPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(1, _ => TimeSpan.FromSeconds(2));

// Scraper client: aggressive timeout, connection pooling via IHttpClientFactory
builder.Services
    .AddHttpClient("scraper", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    })
    .AddPolicyHandler(retryPolicy)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect              = true,
        MaxAutomaticRedirections       = 5,
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, // handles expired SSL on older sites
    });

// Discovery client: slightly longer timeout for external APIs (crt.sh, Common Crawl)
builder.Services
    .AddHttpClient("discovery", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(45);
        client.DefaultRequestHeaders.Add("User-Agent",
            "AvatureScraper/1.0 (+https://github.com/yourorg/avature-scraper)");
    })
    .AddPolicyHandler(retryPolicy);

// ── Register services ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<SiteDiscoveryService>();
builder.Services.AddSingleton<FeedParser>();
builder.Services.AddSingleton<HtmlParser>();
builder.Services.AddSingleton<ScraperService>();
builder.Services.AddSingleton<OutputService>();

var host = builder.Build();

// ── Run ───────────────────────────────────────────────────────────────────────
var logger    = host.Services.GetRequiredService<ILogger<Program>>();
var discovery = host.Services.GetRequiredService<SiteDiscoveryService>();
var scraper   = host.Services.GetRequiredService<ScraperService>();
var output    = host.Services.GetRequiredService<OutputService>();

var seedFile   = args.Length > 0 ? args[0] : "urls.txt";
var outputDir  = args.Length > 1 ? args[1] : "output";
var concurrent = args.Length > 2 && int.TryParse(args[2], out var c) ? c : 12;

logger.LogInformation("══════════════════════════════════════════════════");
logger.LogInformation("  AVATURE SCRAPER  —  starting");
logger.LogInformation("  Seed file:   {Seed}",       seedFile);
logger.LogInformation("  Output dir:  {Out}",         outputDir);
logger.LogInformation("  Concurrency: {N} sites",     concurrent);
logger.LogInformation("══════════════════════════════════════════════════");

var sw = Stopwatch.StartNew();

// Phase 1: discover all Avature sites
logger.LogInformation("Phase 1 — Site discovery");
var sites = await discovery.DiscoverAllSitesAsync(seedFile);

// Phase 2: scrape all sites
logger.LogInformation("Phase 2 — Scraping {Count} sites", sites.Count);
var (jobs, results) = await scraper.ScrapeAllAsync(sites, concurrent);

// Phase 3: write output
logger.LogInformation("Phase 3 — Writing output");
sw.Stop();
var summary = await output.SaveAsync(jobs, results, outputDir, sw.Elapsed.TotalSeconds);

logger.LogInformation("══════════════════════════════════════════════════");
logger.LogInformation("  COMPLETE  —  {Elapsed:F1}s elapsed",     sw.Elapsed.TotalSeconds);
logger.LogInformation("  Sites scraped:  {Total}",                 summary.TotalSites);
logger.LogInformation("  Sites w/ jobs:  {WithJobs}",              summary.SitesWithJobs);
logger.LogInformation("  Unique jobs:    {Jobs:N0}",               summary.UniqueJobs);
logger.LogInformation("  Feed / HTML:    {F} / {H}",
    summary.EndpointsUsed.GetValueOrDefault("feed"),
    summary.EndpointsUsed.GetValueOrDefault("html"));
logger.LogInformation("══════════════════════════════════════════════════");
