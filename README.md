# Avature ATS Scraper — C# / .NET 8

Production-grade async scraper for Avature-powered career pages, built with
idiomatic .NET patterns: `IHttpClientFactory`, Polly retry policies, `AngleSharp`
HTML parsing, and `CsvHelper` output.

---

## Quick Start

```bash
# Prerequisites: .NET 8 SDK (https://dotnet.microsoft.com/download)

cd AvatureScraper

# Restore NuGet packages
dotnet restore

# Build
dotnet build -c Release

# Run (defaults: seed=urls.txt, output=./output, concurrency=12)
dotnet run --project AvatureScraper -c Release -- urls.txt output 12

# Or run the published binary
dotnet publish AvatureScraper -c Release -o dist
./dist/AvatureScraper urls.txt output 12
```

### Arguments

| Position | Name        | Default    | Description                          |
|----------|-------------|------------|--------------------------------------|
| 1        | seed file   | `urls.txt` | Newline-delimited Avature URLs       |
| 2        | output dir  | `output`   | Where to write JSONL / CSV / summary |
| 3        | concurrency | `12`       | Max concurrent site probes           |

---

## Architecture

```
Program.cs                  ← DI host, HttpClient + Polly wiring, orchestration
│
├── Services/
│   ├── SiteDiscoveryService.cs   ← 3-layer site discovery
│   ├── ScraperService.cs         ← async scraping engine (Channels + SemaphoreSlim)
│   ├── FeedParser.cs             ← RSS 2.0 / Atom XML parser (System.Xml.Linq)
│   ├── HtmlParser.cs             ← HTML fallback parser (AngleSharp)
│   └── OutputService.cs          ← JSONL + CSV + summary JSON writer
│
└── Models/
    └── Models.cs                 ← Job, SiteConfig, SiteResult, RunSummary records
```

---

## Engineering Decisions

### Why `IHttpClientFactory` + Polly?

`HttpClient` is notoriously easy to misuse in .NET (socket exhaustion from
creating instances per-request). `IHttpClientFactory` solves this with proper
connection pooling and lifetime management. Polly adds:

- **Retry with exponential back-off** — 2 retries on transient errors (timeouts, 5xx)
- **Respect for `Retry-After`** — if a site rate-limits us, we honour it
- **Per-named-client policies** — the `"scraper"` client has a 20s timeout;
  the `"discovery"` client gets 45s for the slower CT log / Common Crawl APIs

### Why `SemaphoreSlim` over `Parallel.ForEachAsync`?

`Parallel.ForEachAsync` is excellent but bounds to the thread pool. For
network-bound async I/O, `SemaphoreSlim` with `Task.WhenAll` is more
transparent — the semaphore count directly maps to open TCP connections,
which is what we're actually limiting.

### Why `System.Xml.Linq` for RSS?

Avature's feed is standard RSS 2.0 with custom namespace elements.
`XDocument` + `XElement.Descendants` gives clean LINQ queries without the
ceremony of `XmlDocument` or a SAX parser. It handles namespace-aware
element lookup elegantly.

### Why `AngleSharp` for HTML?

`HtmlAgilityPack` is the classic choice but `AngleSharp` implements the
W3C HTML5 parsing spec — it handles malformed HTML the same way browsers
do. This matters for Avature sites that inject JS-rendered content with
slightly broken HTML.

---

## Key Avature API Pattern (Reverse-Engineered)

Every Avature site exposes these endpoints consistently:

| Endpoint | Data | Priority |
|---|---|---|
| `{base}{path}/SearchJobs/feed/?jobRecordsPerPage=100&jobOffset=N` | RSS 2.0 XML — rich structured data | **1st** |
| `{base}{path}/SearchJobs?jobRecordsPerPage=100&jobOffset=N` | HTML job cards | Fallback |
| `{base}{path}/JobDetail/{slug}/{id}` | Full job description | Optional detail fetch |

The scraper probes 8 career path variants per site:
`/careers`, `/en_US/careers`, `/en_GB/careers`, `/ExternalCareers`,
`/Careers`, `/es_ES/Careers`, `/pt_PT/careers`, `/jobs`

---

## Site Discovery (Three Layers)

1. **Seed file** — regex-extract unique `*.avature.net` subdomains from provided URLs
2. **crt.sh CT logs** — `GET https://crt.sh/?q=%.avature.net&output=json`  
   Certificate Transparency logs reveal subdomains not visible in crawls
3. **Common Crawl CDX API** — query the 3 most recent CC indexes for any `*.avature.net` URL  
   Adds long-tail sites indexed years ago that still serve jobs

---

## Output Files

### `jobs_YYYYMMDD_HHmmss.jsonl`
One JSON object per line (streaming-safe, easy to `jq`):
```json
{
  "jobId": "14984",
  "title": "Principal Software Engineer, Ally ai Platform",
  "company": "ally",
  "siteBaseUrl": "https://ally.avature.net",
  "applyUrl": "https://ally.avature.net/careers/JobDetail/...",
  "location": "Charlotte, NC or Detroit, MI (Hybrid)",
  "description": "At Ally, we are building the future of financial services...",
  "datePosted": "Mon, 02 Jun 2026 00:00:00 GMT",
  "jobType": "Full-Time",
  "department": "Technology",
  "sourceEndpoint": "feed",
  "scrapedAt": "2026-06-05T01:00:00.000Z"
}
```

### `jobs_YYYYMMDD_HHmmss.csv`
UTF-8 with BOM (Excel-compatible), same fields as JSONL.

### `summary_YYYYMMDD_HHmmss.json`
```json
{
  "runTimestamp": "20260605_010000",
  "totalSites": 210,
  "sitesWithJobs": 147,
  "totalJobsRaw": 38412,
  "uniqueJobs": 37891,
  "elapsedSeconds": 847.3,
  "endpointsUsed": { "feed": 131, "html": 16, "none": 63 },
  "siteBreakdown": [...]
}
```

---

## NuGet Dependencies

| Package | Version | Purpose |
|---|---|---|
| `AngleSharp` | 1.1.2 | W3C-spec HTML parsing |
| `Polly` | 8.3.1 | Retry / resilience policies |
| `Microsoft.Extensions.Http.Polly` | 8.0.4 | HttpClient + Polly integration |
| `Microsoft.Extensions.Hosting` | 8.0.0 | DI / ILogger / IHttpClientFactory |
| `Microsoft.Extensions.Logging.Console` | 8.0.0 | Console logging |
| `CsvHelper` | 33.0.1 | RFC 4180 CSV output |
