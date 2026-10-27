using System.Text;
using System.Text.RegularExpressions;

using AngleSharp.Dom;

using Ganss.Xss;

using Markdig;

namespace WhalePod.Infrastructure.Services;

public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAdvancedExtensions()
        .Build();

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static RenderedMarkdown Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var html = Markdown.ToHtml(markdown, Pipeline);
        var sanitizer = CreateSanitizer();
        var safeHtml = sanitizer.Sanitize(html);
        var plainText = Markdown.ToPlainText(markdown, Pipeline);
        return new RenderedMarkdown(safeHtml, BuildExcerpt(plainText));
    }

    public static string BuildExcerpt(string plainText)
    {
        var normalized = Whitespace.Replace(plainText ?? string.Empty, " ").Trim();
        return RuneHelper.Truncate(normalized, 320);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer
        {
            AllowDataAttributes = false
        };

        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
                 {
                     "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6",
                     "ul", "ol", "li", "blockquote", "pre", "code", "strong", "em", "del",
                     "a", "img", "table", "thead", "tbody", "tr", "th", "td"
                 })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "href", "title", "src", "alt", "rel" })
        {
            sanitizer.AllowedAttributes.Add(attribute);
        }

        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");

        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();

        sanitizer.PostProcessNode += (_, args) =>
        {
            if (args.Node is IElement element && element.LocalName == "a")
            {
                element.RemoveAttribute("target");
                element.SetAttribute("rel", "nofollow noopener noreferrer");
            }
        };

        return sanitizer;
    }
}

public sealed record RenderedMarkdown(string Html, string Excerpt);

public static class RuneHelper
{
    public static int Count(string? value)
    {
        return (value ?? string.Empty).EnumerateRunes().Count();
    }

    public static string Truncate(string value, int maxRunes)
    {
        if (Count(value) <= maxRunes)
        {
            return value;
        }

        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used >= maxRunes)
            {
                break;
            }

            builder.Append(rune.ToString());
            used++;
        }

        return builder.ToString();
    }
}