using System.Globalization;
using System.Xml.Linq;

using WhalePod.Infrastructure.Services;
using WhalePod.Web.Services;

namespace WhalePod.Web.Endpoints;

internal static class DiscoveryEndpoints
{
    public static void MapDiscoveryEndpoints(this WebApplication app)
    {
        app.MapGet("/rss.xml", async (
            HttpContext http,
            PublicDiscoveryService discovery,
            PublicSiteLinks siteLinks) =>
        {
            var document = await discovery.GetRssAsync(50, http.RequestAborted);
            if (TryWriteNotModified(http, document.LastModified, "rss"))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Text(
                RenderRss(document.Items, document.LastModified, siteLinks),
                "application/rss+xml; charset=utf-8");
        });

        app.MapGet("/sitemap.xml", async (
            HttpContext http,
            PublicDiscoveryService discovery,
            PublicSiteLinks siteLinks) =>
        {
            var document = await discovery.GetSitemapIndexAsync(5000, http.RequestAborted);
            if (TryWriteNotModified(http, document.LastModified, "sitemap-index"))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Text(
                RenderSitemapIndex(document.Shards, siteLinks),
                "application/xml; charset=utf-8");
        });

        app.MapGet("/sitemap-posts-{page:int}.xml", async (
            int page,
            HttpContext http,
            PublicDiscoveryService discovery,
            PublicSiteLinks siteLinks) =>
        {
            var document = await discovery.GetPostSitemapShardAsync(page, 5000, http.RequestAborted);
            if (document is null)
            {
                return Results.NotFound();
            }

            if (TryWriteNotModified(http, document.LastModified, $"sitemap-posts-{page}"))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Text(
                RenderSitemap(document.Entries, siteLinks),
                "application/xml; charset=utf-8");
        });

        app.MapGet("/sitemap-pages.xml", async (
            HttpContext http,
            PublicDiscoveryService discovery,
            PublicSiteLinks siteLinks) =>
        {
            var document = await discovery.GetPageSitemapAsync(http.RequestAborted);
            if (TryWriteNotModified(http, document.LastModified, "sitemap-pages"))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Text(
                RenderSitemap(document.Entries, siteLinks),
                "application/xml; charset=utf-8");
        });

        app.MapGet("/robots.txt", (HttpContext http, PublicSiteLinks siteLinks) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=3600";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var robots = $"""
                User-agent: *
                Allow: /
                Disallow: /admin/
                Disallow: /settings/
                Disallow: /dev/
                Disallow: /internal/
                Disallow: /api/
                Disallow: /appeals
                Disallow: /email/unsubscribe
                Disallow: /editor
                Disallow: /search
                Sitemap: {siteLinks.Absolute("/sitemap.xml")}
                """;
            return Results.Text(robots, "text/plain; charset=utf-8");
        });
    }

    private static string RenderRss(
        IReadOnlyList<RssItemView> items,
        DateTimeOffset lastModified,
        PublicSiteLinks siteLinks)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var channel = new XElement(
            "channel",
            new XElement("title", "WhalePod（鲸群）"),
            new XElement("link", siteLinks.BaseUrl),
            new XElement("description", "可复现的 AI 工具实测记录与可直接复制的产物。"),
            new XElement("language", "zh-cn"),
            lastModified > DateTimeOffset.UnixEpoch
                ? new XElement("lastBuildDate", lastModified.UtcDateTime.ToString("R"))
                : null,
            new XElement(
                atom + "link",
                new XAttribute("href", siteLinks.Absolute("/rss.xml")),
                new XAttribute("rel", "self"),
                new XAttribute("type", "application/rss+xml")),
            items.Select(item =>
            {
                var link = siteLinks.Absolute($"/posts/{item.Id}");
                return new XElement(
                    "item",
                    new XElement("title", item.Title),
                    new XElement("link", link),
                    new XElement("guid", new XAttribute("isPermaLink", "true"), link),
                    new XElement("pubDate", item.CreatedAt.UtcDateTime.ToString("R")),
                    new XElement("description", item.Excerpt),
                    new XElement("category", item.CategorySlug),
                    new XElement(dc + "creator", $"@{item.AuthorHandle}"));
            }));
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "rss",
                new XAttribute("version", "2.0"),
                new XAttribute(XNamespace.Xmlns + "atom", atom),
                new XAttribute(XNamespace.Xmlns + "dc", dc),
                channel));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static bool TryWriteNotModified(
        HttpContext http,
        DateTimeOffset lastModified,
        string resourceName)
    {
        var etag = $"\"{resourceName}-{lastModified.UtcTicks}\"";
        http.Response.Headers.CacheControl = "public, max-age=300";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.LastModified = lastModified.ToUniversalTime().ToString("R");
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";

        var ifNoneMatch = http.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            var matches = ifNoneMatch
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => value == "*" || value == etag || value == $"W/{etag}");
            if (matches)
            {
                http.Response.StatusCode = StatusCodes.Status304NotModified;
            }

            return matches;
        }

        var ifModifiedSince = http.Request.GetTypedHeaders().IfModifiedSince;
        if (lastModified > DateTimeOffset.UnixEpoch
            && ifModifiedSince is not null
            && lastModified.ToUnixTimeSeconds() <= ifModifiedSince.Value.ToUnixTimeSeconds())
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return true;
        }

        return false;
    }

    private static string RenderSitemapIndex(
        IReadOnlyList<SitemapShard> shards,
        PublicSiteLinks siteLinks)
    {
        XNamespace sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                sitemap + "sitemapindex",
                shards
                    .GroupBy(shard => shard.Path, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .Select(shard => new XElement(
                        sitemap + "sitemap",
                        new XElement(sitemap + "loc", siteLinks.Absolute(shard.Path)),
                        new XElement(
                            sitemap + "lastmod",
                            shard.LastModified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string RenderSitemap(
        IReadOnlyList<SitemapEntry> entries,
        PublicSiteLinks siteLinks)
    {
        XNamespace sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                sitemap + "urlset",
                entries
                    .GroupBy(entry => entry.Path, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .Select(entry => new XElement(
                        sitemap + "url",
                        new XElement(sitemap + "loc", siteLinks.Absolute(entry.Path)),
                        new XElement(
                            sitemap + "lastmod",
                            entry.LastModified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))))));
        return document.ToString(SaveOptions.DisableFormatting);
    }
}