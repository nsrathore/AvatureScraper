using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AvatureScraper.Models;
using Microsoft.Extensions.Logging;

namespace AvatureScraper.Services;

/// <summary>
/// Fallback parser for Avature HTML SearchJobs pages.
/// Used when the RSS feed endpoint returns nothing useful.
///
/// Avature renders job cards in one of several patterns depending on the
/// client's template configuration — we try multiple selectors in priority order.
/// </summary>
public sealed class HtmlParser(ILogger<HtmlParser> logger)
{
    private static readonly AngleSharp.Html.Parser.HtmlParser Parser = new();

    private static readonly Regex TotalJobsRe = new(
        @"(\d[\d,]*)\s*(?:results?|jobs?|positions?|openings?|opportunities?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TotalRecordsRe = new(
        @"totalRecords[""'\s:]+(\d+)",
        RegexOptions.Compiled);

    private static readonly Regex JobIdFromUrlRe = new(
        @"/(\d+)(?:[/?#]|$)", RegexOptions.Compiled);

    // Job card CSS selectors in priority order
    private static readonly string[] CardSelectors =
    [
        "[data-job-id]",
        ".job-item",
        ".jobItem",
        "article.job",
        "li[class*='job']",
        "div[class*='job-card']",
        "[class*='jobDetail']",
    ];

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse the HTML of an Avature SearchJobs page.
    /// Returns (jobs found on this page, estimated total across all pages).
    /// </summary>
    public (List<Job> Jobs, int Total) ParseSearchPage(string html, SiteConfig site)
    {
        var document = Parser.ParseDocument(html);
        var total    = ExtractTotal(html, document);
        var jobs     = new List<Job>();

        // Try structured card selectors first
        foreach (var selector in CardSelectors)
        {
            var cards = document.QuerySelectorAll(selector);
            if (!cards.Any()) continue;

            foreach (var card in cards)
            {
                var job = ParseCard(card, site);
                if (job is not null) jobs.Add(job);
            }

            if (jobs.Count > 0)
            {
                logger.LogDebug("{Site}: {Count} jobs via selector '{Sel}'",
                    site.Subdomain, jobs.Count, selector);
                return (jobs, Math.Max(total, jobs.Count));
            }
        }

        // Fallback: scan all <a href> tags that look like JobDetail links
        var jobLinks = document.QuerySelectorAll("a[href*='/JobDetail/']");
        foreach (var a in jobLinks)
        {
            var href  = a.GetAttribute("href") ?? string.Empty;
            var title = a.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            var jobId    = ExtractJobId(href);
            var fullUrl  = href.StartsWith("http") ? href : $"{site.BaseUrl}{href}";
            var location = a.Closest("[class*='location']")?.TextContent.Trim() ?? string.Empty;

            jobs.Add(new Job
            {
                JobId          = jobId,
                Title          = title,
                Company        = site.Subdomain,
                SiteBaseUrl    = site.BaseUrl,
                ApplyUrl       = fullUrl,
                Location       = location,
                Description    = string.Empty,
                DatePosted     = string.Empty,
                JobType        = string.Empty,
                Department     = string.Empty,
                SourceEndpoint = "html-link-scan",
            });
        }

        return (jobs, Math.Max(total, jobs.Count));
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private Job? ParseCard(IElement card, SiteConfig site)
    {
        // Job ID — prefer data attribute
        var jobId = card.GetAttribute("data-job-id")
                 ?? card.GetAttribute("data-id")
                 ?? string.Empty;

        // Title
        var titleEl = card.QuerySelector("[class*='title']")
                   ?? card.QuerySelector("h2")
                   ?? card.QuerySelector("h3")
                   ?? card.QuerySelector("a");
        var title = titleEl?.TextContent.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title)) return null;

        // Link
        var linkEl  = card.QuerySelector("a[href]");
        var href    = linkEl?.GetAttribute("href") ?? string.Empty;
        var fullUrl = href.StartsWith("http") ? href : $"{site.BaseUrl}{href}";

        if (string.IsNullOrEmpty(jobId))
            jobId = ExtractJobId(href, title);

        // Location
        var locEl    = card.QuerySelector("[class*='location']")
                    ?? card.QuerySelector("[class*='city']");
        var location = locEl?.TextContent.Trim() ?? string.Empty;

        // Date posted
        var dateEl    = card.QuerySelector("time")
                     ?? card.QuerySelector("[class*='date']");
        var datePosted = dateEl?.GetAttribute("datetime")
                      ?? dateEl?.TextContent.Trim()
                      ?? string.Empty;

        // Department / category
        var deptEl = card.QuerySelector("[class*='department']")
                  ?? card.QuerySelector("[class*='category']");
        var dept   = deptEl?.TextContent.Trim() ?? string.Empty;

        return new Job
        {
            JobId          = jobId,
            Title          = title,
            Company        = site.Subdomain,
            SiteBaseUrl    = site.BaseUrl,
            ApplyUrl       = !string.IsNullOrEmpty(fullUrl) ? fullUrl
                             : $"{site.BaseUrl}{site.CareerPath}/JobDetail?jobId={jobId}",
            Location       = location,
            Description    = string.Empty,   // fetched separately if needed
            DatePosted     = datePosted,
            JobType        = string.Empty,
            Department     = dept,
            SourceEndpoint = "html",
        };
    }

    private static int ExtractTotal(string rawHtml, IDocument document)
    {
        // Try text patterns first
        var m = TotalJobsRe.Match(rawHtml);
        if (m.Success && int.TryParse(m.Groups[1].Value.Replace(",", ""), out var n1))
            return n1;

        var m2 = TotalRecordsRe.Match(rawHtml);
        if (m2.Success && int.TryParse(m2.Groups[1].Value, out var n2))
            return n2;

        return 0;
    }

    private static string ExtractJobId(string url, string? title = null)
    {
        var m = JobIdFromUrlRe.Match(url);
        if (m.Success) return m.Groups[1].Value;

        var input = (title ?? string.Empty) + url;
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input)))[..12];
    }
}
