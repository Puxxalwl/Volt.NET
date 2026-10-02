using System.Text;

namespace Volt;

/// <summary>Generates sitemap.xml and robots.txt from the route table.</summary>
public static class Sitemap
{
    /// <summary>Builds sitemap.xml bytes. Static routes plus SSG dynamic paths.</summary>
    public static byte[] BuildXml(RouteRegistry registry, string baseUrl)
    {
        var sb = new StringBuilder(256);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        var baseSpan = baseUrl.TrimEnd('/');

        foreach (var route in registry.Routes)
        {
            if (route.ParamCount == 0)
            {
                AppendUrl(sb, baseSpan, route.Pattern);
            }
            else
            {
                // dynamic: enumerate SSG prerendered paths
                var page = route.Factory();
                if (page.Mode != RenderMode.SSG) continue;
                foreach (var value in page.StaticPaths())
                    AppendUrl(sb, baseSpan, route.MakePath(value));
            }
        }
        sb.Append("</urlset>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AppendUrl(StringBuilder sb, string baseUrl, string path)
    {
        sb.Append("<url><loc>").Append(XmlEscape(baseUrl)).Append(XmlEscape(path)).Append("</loc></url>\n");
    }

    /// <summary>Builds robots.txt bytes.</summary>
    public static byte[] BuildRobots(string baseUrl) =>
        Encoding.UTF8.GetBytes(
            "User-agent: *\nAllow: /\n\nSitemap: " + baseUrl.TrimEnd('/') + "/sitemap.xml\n");

    private static string XmlEscape(string s)
    {
        if (s.IndexOf('&') < 0 && s.IndexOf('<') < 0 && s.IndexOf('>') < 0) return s;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
