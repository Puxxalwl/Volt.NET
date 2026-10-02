namespace Volt;

/// <summary>Typed page metadata; rendered into &lt;head&gt; by <see cref="SeoHtml.SeoHead"/>.</summary>
public sealed class Seo
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Canonical { get; set; }
    public string? Robots { get; set; }
    public string? OgTitle { get; set; }
    public string? OgDescription { get; set; }
    public string? OgImage { get; set; }
    public string? OgUrl { get; set; }
    public string? TwitterCard { get; set; }
    public string? TwitterSite { get; set; }

    public static Seo Of(string title, string? description = null) =>
        new() { Title = title, Description = description };
}

/// <summary>Renders SEO metadata into the head.</summary>
public static class SeoHtml
{
    /// <summary>
    /// Writes title, description, canonical, robots and Open Graph/Twitter tags.
    /// Call inside a &lt;head&gt; scope.
    /// </summary>
    public static void SeoHead(this HtmlWriter w, Seo seo, string? baseUrl = null)
    {
        if (seo.Title is not null) w.Title(seo.Title);
        if (seo.Description is not null) w.Meta("description", seo.Description);
        if (seo.Canonical is not null) w.Link("canonical", Resolve(seo.Canonical, baseUrl));
        if (seo.Robots is not null) w.Meta("robots", seo.Robots);

        bool anyOg = seo.OgTitle is not null || seo.OgDescription is not null || seo.OgImage is not null || seo.OgUrl is not null;
        if (anyOg)
        {
            w.MetaProperty("og:title", seo.OgTitle ?? seo.Title ?? "");
            if (seo.OgDescription is not null) w.MetaProperty("og:description", seo.OgDescription);
            if (seo.OgImage is not null) w.MetaProperty("og:image", Resolve(seo.OgImage, baseUrl));
            if (seo.OgUrl is not null) w.MetaProperty("og:url", Resolve(seo.OgUrl, baseUrl));
            w.MetaProperty("og:type", "website");
        }
        if (seo.TwitterCard is not null) w.Meta("twitter:card", seo.TwitterCard);
        if (seo.TwitterSite is not null) w.Meta("twitter:site", seo.TwitterSite);
    }

    private static string Resolve(string url, string? baseUrl)
    {
        if (baseUrl is null || url.StartsWith("http://") || url.StartsWith("https://") || !url.StartsWith('/'))
            return url;
        return baseUrl.TrimEnd('/') + url;
    }
}
