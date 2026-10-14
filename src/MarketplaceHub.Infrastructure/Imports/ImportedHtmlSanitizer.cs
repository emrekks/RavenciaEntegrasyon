using Ganss.Xss;

namespace MarketplaceHub.Infrastructure.Imports;

/// <summary>
/// Sanitizes HTML that enters the catalog through spreadsheet/file imports.
/// Keep this allowlist aligned with the browser rich-text renderer.
/// </summary>
public static class ImportedHtmlSanitizer
{
    private static readonly string[] AllowedTags =
    [
        "a", "b", "blockquote", "br", "code", "div", "em", "h1", "h2", "h3", "h4", "h5", "h6",
        "i", "img", "li", "ol", "p", "pre", "s", "strong", "u", "ul"
    ];

    private static readonly string[] AllowedAttributes =
    [
        "alt", "height", "href", "rel", "src", "target", "title", "width"
    ];

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sanitizer = new HtmlSanitizer
        {
            AllowDataAttributes = false,
            AllowCssCustomProperties = false,
            KeepChildNodes = true
        };
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedClasses.Clear();
        sanitizer.AllowedSchemes.Clear();

        foreach (var tag in AllowedTags) sanitizer.AllowedTags.Add(tag);
        foreach (var attribute in AllowedAttributes) sanitizer.AllowedAttributes.Add(attribute);
        foreach (var scheme in new[] { "http", "https", "mailto", "tel" }) sanitizer.AllowedSchemes.Add(scheme);

        return sanitizer.Sanitize(value);
    }
}
