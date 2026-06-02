namespace AvatureScraper.Models;

/// <summary>
/// A single job posting extracted from an Avature career site.
/// </summary>
public sealed record Job
{
    public string JobId          { get; init; } = string.Empty;
    public string Title          { get; init; } = string.Empty;
    public string Company        { get; init; } = string.Empty;   // inferred from subdomain
    public string SiteBaseUrl    { get; init; } = string.Empty;
    public string ApplyUrl       { get; init; } = string.Empty;
    public string Location       { get; init; } = string.Empty;
    public string Description    { get; init; } = string.Empty;   // clean text, HTML stripped
    public string DatePosted     { get; init; } = string.Empty;
    public string JobType        { get; init; } = string.Empty;
    public string Department     { get; init; } = string.Empty;
    public string SourceEndpoint { get; init; } = string.Empty;   // "feed" | "html"
    public string ScrapedAt      { get; init; } = DateTime.UtcNow.ToString("o");

    /// <summary>Stable deduplication key across runs.</summary>
    public string DedupKey => Convert.ToHexString(
        System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{SiteBaseUrl}|{JobId}|{Title}")));
}

/// <summary>
/// Configuration for one Avature career site to be scraped.
/// </summary>
public sealed record SiteConfig
{
    public string Subdomain  { get; init; } = string.Empty;
    public string BaseUrl    { get; init; } = string.Empty;
    public string CareerPath { get; init; } = "/careers";
}

/// <summary>
/// Result summary for one site after scraping completes.
/// </summary>
public sealed record SiteResult
{
    public string   Subdomain  { get; init; } = string.Empty;
    public string   BaseUrl    { get; init; } = string.Empty;
    public int      JobsFound  { get; init; }
    public string   Endpoint   { get; init; } = "none";
    public string?  Error      { get; init; }
}

/// <summary>
/// Overall run summary written to summary_TIMESTAMP.json.
/// </summary>
public sealed class RunSummary
{
    public string RunTimestamp   { get; set; } = string.Empty;
    public int    TotalSites     { get; set; }
    public int    SitesWithJobs  { get; set; }
    public int    TotalJobsRaw   { get; set; }
    public int    UniqueJobs     { get; set; }
    public double ElapsedSeconds { get; set; }

    public Dictionary<string, int> EndpointsUsed { get; set; } = new();
    public List<SiteResult> SiteBreakdown        { get; set; } = new();
}
