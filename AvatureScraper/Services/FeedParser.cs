using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Parser;
using AvatureScraper.Models;
using Microsoft.Extensions.Logging;

namespace AvatureScraper.Services;

/// <summary>
/// Parses Avature RSS 2.0 feed XML into Job records.
///
/// Avature feeds follow standard RSS 2.0 with custom elements for
/// location, department, and jobType. The feed endpoint is always:
///   {base}{careerPath}/SearchJobs/feed/?jobRecordsPerPage=N&amp;jobOffset=N
/// </summary>
public sealed class FeedParser(ILogger<FeedParser> logger)
{
    private static readonly AngleSharp.Html.Parser.HtmlParser HtmlParser = new();

    private static readonly Regex JobIdFromUrlRe = new(
        @"/(\d+)(?:[/?#]|$)", RegexOptions.Compiled);

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse an Avature RSS feed XML string into a list of Job records.
    /// Handles both RSS 2.0 &lt;item&gt; and Atom &lt;entry&gt; formats.
    /// </summary>
    public List<Job> ParseFeed(string xml, SiteConfig site)
    {
        var jobs = new List<Job>();

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        {
            logger.LogDebug("XML parse failed for {Site}: {Msg}", site.Subdomain, ex.Message);
            return jobs;
        }

        // Support both RSS 2.0 <item> and Atom <entry>
        var ns      = doc.Root?.Name.Namespace ?? XNamespace.None;
        var items   = doc.Descendants("item").ToList();
        if (items.Count == 0)
            items = doc.Descendants(ns + "entry").ToList();

        foreach (var item in items)
        {
            var job = ParseItem(item, ns, site);
            if (job is not null)
                jobs.Add(job);
        }

        return jobs;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private Job? ParseItem(XElement item, XNamespace ns, SiteConfig site)
    {
        var title = Text(item, "title", ns);
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var link        = Text(item, "link", ns) ?? Text(item, "guid", ns) ?? string.Empty;
        var description = Text(item, "description", ns)
                       ?? Text(item, "content", ns)
                       ?? Text(item, "summary", ns)
                       ?? string.Empty;
        var pubDate   = Text(item, "pubDate", ns) ?? Text(item, "published", ns) ?? string.Empty;
        var location  = Text(item, "location", ns) ?? string.Empty;
        var dept      = Text(item, "department", ns)
                     ?? Text(item, "category", ns)
                     ?? string.Empty;
        var jobType   = Text(item, "jobType", ns) ?? Text(item, "type", ns) ?? string.Empty;

        // Extract job ID from URL
        var jobId = ExtractJobId(link, title);

        // Strip HTML from description
        var descText = StripHtml(description);

        return new Job
        {
            JobId          = jobId,
            Title          = title.Trim(),
            Company        = site.Subdomain,
            SiteBaseUrl    = site.BaseUrl,
            ApplyUrl       = !string.IsNullOrEmpty(link) ? link.Trim()
                             : $"{site.BaseUrl}{site.CareerPath}/JobDetail?jobId={jobId}",
            Location       = location.Trim(),
            Description    = descText.Length > 5_000
                             ? descText[..5_000] : descText,
            DatePosted     = pubDate.Trim(),
            JobType        = jobType.Trim(),
            Department     = dept.Trim(),
            SourceEndpoint = "feed",
        };
    }

    private static string? Text(XElement parent, string localName, XNamespace ns)
    {
        // Try with namespace first, then without
        return parent.Element(ns + localName)?.Value
            ?? parent.Element(localName)?.Value;
    }

    private static string ExtractJobId(string url, string title)
    {
        var m = JobIdFromUrlRe.Match(url);
        if (m.Success) return m.Groups[1].Value;

        // Fall back to MD5 of title+url for non-numeric IDs
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(title + url));
        return Convert.ToHexString(hash)[..12];
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Use AngleSharp to properly parse HTML and extract text
        var document = HtmlParser.ParseDocument(html);
        return document.Body?.TextContent
            .Replace("\r\n", "\n")
            .Trim()
            ?? html;
    }
}
