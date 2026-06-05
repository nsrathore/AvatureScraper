using System.Text.RegularExpressions;
using System.Text.Json;
using AvatureScraper.Models;
using Microsoft.Extensions.Logging;

namespace AvatureScraper.Services;

/// <summary>
/// Discovers all Avature-powered career sites via three layers:
///   1. Seed URL file parsing
///   2. Certificate Transparency logs (crt.sh)
///   3. Common Crawl CDX index API
/// </summary>
public sealed class SiteDiscoveryService(
    IHttpClientFactory httpClientFactory,
    ILogger<SiteDiscoveryService> logger)
{
    private static readonly Regex AvatureSubdomainRe = new(
        @"https?://([a-zA-Z0-9][a-zA-Z0-9\-]*?)\.avature\.net(/[^\s?#]*)?",
        RegexOptions.Compiled);

    private static readonly Regex CareerPathRe = new(
        @"/(en_[A-Z]{2}/careers|es_ES/Careers|pt_PT/careers|ExternalCareers|Careers|careers)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> SkipSubdomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "avature", "analytics", "docs", "marketing", "smtp-iatsapp-en07",
        "rocketchat", "iatsapp-tc30", "www", "mail", "api", "cdn",
        "static", "dev", "staging", "qa", "test", "sandbox", "admin"
    };

    // Career paths to probe in order of likelihood
    public static readonly string[] CareerPathCandidates =
    [
        "/careers",
        "/talent",
        "/jobs",
        "/career",
        "/en_US/careers",
        "/en_GB/careers",
        "/Careers",
        "/ExternalCareers",
        "/es_ES/Careers",
        "/pt_PT/careers",
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Full discovery pipeline: seed → CT logs → Common Crawl → dedup.
    /// Returns all unique site configs ready for scraping.
    /// </summary>
    public async Task<List<SiteConfig>> DiscoverAllSitesAsync(
        string seedFilePath,
        CancellationToken ct = default)
    {
        var allSubdomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Layer 1: seed file
        var seedSubs = ExtractFromSeedFile(seedFilePath);
        allSubdomains.UnionWith(seedSubs);
        logger.LogInformation("Seed file → {Count} subdomains", seedSubs.Count);

        // Layer 2: Certificate Transparency (crt.sh)
        try
        {
            var crtSubs = await QueryCrtShAsync(ct);
            var added   = crtSubs.Count(s => allSubdomains.Add(s));
            logger.LogInformation("crt.sh → {Added} new subdomains (total {Total})",
                added, allSubdomains.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning("crt.sh query failed: {Message}", ex.Message);
        }

        // Layer 3: Common Crawl CDX API
        try
        {
            var ccSubs = await QueryCommonCrawlAsync(ct);
            var added  = ccSubs.Count(s => allSubdomains.Add(s));
            logger.LogInformation("Common Crawl → {Added} new subdomains (total {Total})",
                added, allSubdomains.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Common Crawl query failed: {Message}", ex.Message);
        }

        // Build site configs (career path resolved during scraping via probing)
        var sites = allSubdomains
            .OrderBy(s => s)
            .Select(sub => new SiteConfig
            {
                Subdomain  = sub,
                BaseUrl    = $"https://{sub}.avature.net",
                CareerPath = "/careers"   // scraper probes alternates if this fails
            })
            .ToList();

        logger.LogInformation("Total sites to scrape: {Count}", sites.Count);
        return sites;
    }

    // ── Layer 1: Seed file ────────────────────────────────────────────────────

    public HashSet<string> ExtractFromSeedFile(string path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadLines(path))
        {
            var m = AvatureSubdomainRe.Match(line.Trim());
            if (!m.Success) continue;

            var sub = m.Groups[1].Value.ToLowerInvariant();
            if (!SkipSubdomains.Contains(sub))
                result.Add(sub);
        }

        return result;
    }

    /// <summary>
    /// Infer the career path from a seed URL's path segment.
    /// </summary>
    public static string InferCareerPath(string urlPath)
    {
        var m = CareerPathRe.Match(urlPath);
        return m.Success ? $"/{m.Groups[1].Value}" : "/careers";
    }

    // ── Layer 2: Certificate Transparency (crt.sh) ────────────────────────────

    /// <summary>
    /// Queries crt.sh for all SSL certificates issued to *.avature.net.
    /// Certificate Transparency logs surface subdomains that never appear in crawls.
    /// </summary>
    private async Task<HashSet<string>> QueryCrtShAsync(CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var client = httpClientFactory.CreateClient("discovery");

        var url  = "https://crt.sh/?q=%.avature.net&output=json";
        var json = await client.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(json);

        var namePattern = new Regex(
            @"^([a-zA-Z0-9][a-zA-Z0-9\-]*)\.avature\.net$",
            RegexOptions.Compiled);

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            foreach (var field in new[] { "name_value", "common_name" })
            {
                if (!entry.TryGetProperty(field, out var prop)) continue;
                var val = prop.GetString() ?? string.Empty;

                // name_value may contain multiple names separated by newlines
                foreach (var name in val.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var cleaned = name.Trim().TrimStart('*', '.');
                    var m = namePattern.Match(cleaned);
                    if (m.Success && !SkipSubdomains.Contains(m.Groups[1].Value))
                        result.Add(m.Groups[1].Value.ToLowerInvariant());
                }
            }
        }

        return result;
    }

    // ── Layer 3: Common Crawl CDX API ─────────────────────────────────────────

    /// <summary>
    /// Queries Common Crawl's CDX index for all *.avature.net URLs ever crawled.
    /// Uses the most recent 3 crawl indexes to maximise coverage.
    /// </summary>
    private async Task<HashSet<string>> QueryCommonCrawlAsync(CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var client = httpClientFactory.CreateClient("discovery");

        // Get available indexes
        var indexesJson = await client.GetStringAsync(
            "https://index.commoncrawl.org/collinfo.json", ct);

        using var indexDoc = JsonDocument.Parse(indexesJson);
        var indexes = indexDoc.RootElement
            .EnumerateArray()
            .Take(3)   // 3 most recent indexes
            .Select(e => e.TryGetProperty("cdx-api", out var p) ? p.GetString() : null)
            .Where(u => u is not null)
            .ToList();

        var subRe = new Regex(
            @"https?://([a-zA-Z0-9][a-zA-Z0-9\-]*)\.avature\.net",
            RegexOptions.Compiled);

        foreach (var cdxApi in indexes)
        {
            var url = $"{cdxApi}?url=*.avature.net&output=json&fl=url&limit=5000&collapse=urlkey";
            try
            {
                var text = await client.GetStringAsync(url, ct);

                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string urlValue;
                    try
                    {
                        using var lineDoc = JsonDocument.Parse(line);
                        urlValue = lineDoc.RootElement
                            .TryGetProperty("url", out var p) ? p.GetString() ?? line : line;
                    }
                    catch { urlValue = line; }

                    var m = subRe.Match(urlValue);
                    if (m.Success && !SkipSubdomains.Contains(m.Groups[1].Value))
                        result.Add(m.Groups[1].Value.ToLowerInvariant());
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("CC index {Url} failed: {Msg}", cdxApi, ex.Message);
            }
        }

        return result;
    }
}
