using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsvHelper;
using CsvHelper.Configuration;
using AvatureScraper.Models;
using Microsoft.Extensions.Logging;

namespace AvatureScraper.Services;

/// <summary>
/// Writes scrape results to disk in three formats:
///   - JSONL  (one JSON object per line, streaming-friendly)
///   - CSV    (Excel-compatible, UTF-8 with BOM)
///   - JSON   (summary / run metadata)
/// </summary>
public sealed class OutputService(ILogger<OutputService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented          = false,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions SummaryOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deduplicate jobs, write all output files, return the run summary.
    /// </summary>
    public async Task<RunSummary> SaveAsync(
        IReadOnlyList<Job>        allJobs,
        IReadOnlyList<SiteResult> siteResults,
        string                    outputDir,
        double                    elapsedSeconds,
        CancellationToken         ct = default)
    {
        Directory.CreateDirectory(outputDir);

        var ts         = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var uniqueJobs = Deduplicate(allJobs);

        logger.LogInformation("Writing {Count} unique jobs (from {Raw} raw)", 
            uniqueJobs.Count, allJobs.Count);

        // Parallel writes
        await Task.WhenAll(
            WriteJsonlAsync(uniqueJobs, Path.Combine(outputDir, $"jobs_{ts}.jsonl"), ct),
            WriteCsvAsync(uniqueJobs,   Path.Combine(outputDir, $"jobs_{ts}.csv"),   ct)
        );

        var summary = BuildSummary(ts, allJobs.Count, uniqueJobs, siteResults, elapsedSeconds);
        await WriteSummaryAsync(summary, Path.Combine(outputDir, $"summary_{ts}.json"), ct);

        return summary;
    }

    // ── Deduplication ─────────────────────────────────────────────────────────

    private static List<Job> Deduplicate(IReadOnlyList<Job> jobs)
    {
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Job>(jobs.Count);

        foreach (var job in jobs)
            if (seen.Add(job.DedupKey))
                result.Add(job);

        return result;
    }

    // ── Writers ────────────────────────────────────────────────────────────────

    private async Task WriteJsonlAsync(
        IReadOnlyList<Job> jobs, string path, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, append: false,
            encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var job in jobs)
        {
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(job, JsonOpts).AsMemory(), ct);
        }

        logger.LogInformation("JSONL → {Path}  ({Count} records)", path, jobs.Count);
    }

    private async Task WriteCsvAsync(
        IReadOnlyList<Job> jobs, string path, CancellationToken ct)
    {
        // UTF-8 with BOM so Excel opens correctly
        await using var writer = new StreamWriter(path, append: false,
            encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            NewLine = Environment.NewLine,
        };

        await using var csv = new CsvWriter(writer, config);
        await csv.WriteRecordsAsync(jobs, ct);

        logger.LogInformation("CSV   → {Path}  ({Count} records)", path, jobs.Count);
    }

    private async Task WriteSummaryAsync(
        RunSummary summary, string path, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(summary, SummaryOpts);
        await File.WriteAllTextAsync(path, json, ct);
        logger.LogInformation("Summary → {Path}", path);
    }

    // ── Summary builder ────────────────────────────────────────────────────────

    private static RunSummary BuildSummary(
        string                    ts,
        int                       rawCount,
        IReadOnlyList<Job>        uniqueJobs,
        IReadOnlyList<SiteResult> siteResults,
        double                    elapsedSeconds)
    {
        return new RunSummary
        {
            RunTimestamp   = ts,
            TotalSites     = siteResults.Count,
            SitesWithJobs  = siteResults.Count(r => r.JobsFound > 0),
            TotalJobsRaw   = rawCount,
            UniqueJobs     = uniqueJobs.Count,
            ElapsedSeconds = Math.Round(elapsedSeconds, 1),
            EndpointsUsed  = new Dictionary<string, int>
            {
                ["feed"]      = siteResults.Count(r => r.Endpoint == "feed"),
                ["html"]      = siteResults.Count(r => r.Endpoint.StartsWith("html")),
                ["none"]      = siteResults.Count(r => r.Endpoint == "none"),
            },
            SiteBreakdown = siteResults
                .OrderByDescending(r => r.JobsFound)
                .ToList(),
        };
    }
}
