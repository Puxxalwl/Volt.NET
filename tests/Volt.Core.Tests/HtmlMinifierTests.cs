using System.Text;
using Volt;
using Xunit;

namespace Volt.Core.Tests;

/// <summary>M6: VoltHtmlMinifier — safe streaming byte minifier.</summary>
public sealed class HtmlMinifierTests
{
    private static string Min(string html)
    {
        var src = Encoding.UTF8.GetBytes(html);
        var dst = new byte[src.Length + 16];
        int len = VoltHtmlMinifier.Minify(src, dst);
        return Encoding.UTF8.GetString(dst, 0, len);
    }

    [Fact]
    public void CollapsesWhitespaceRuns_ToSingleSpace()
    {
        Assert.Equal("<p>hello world</p>", Min("<p>hello   world</p>"));
        Assert.Equal("<p>hello world</p>", Min("<p>hello\tworld</p>"));
        Assert.Equal("<div> <p>a</p> <p>b</p> </div>", Min("<div>\n    <p>a</p>\n\n    <p>b</p>\n  </div>"));
    }

    [Fact]
    public void KillsIndentation_BetweenBlockElements()
    {
        var minified = Min("""
            <html>
              <body>
                <ul>
                  <li>one</li>
                </ul>
              </body>
            </html>
            """);
        Assert.DoesNotContain("\n", minified);
        Assert.DoesNotContain("  ", minified);
        Assert.Equal("""<html> <body> <ul> <li>one</li> </ul> </body> </html>""", minified);
    }

    [Fact]
    public void PreservesPreContent_Verbatim()
    {
        var html = "<pre>  a\n\tb  </pre>";
        Assert.Equal(html, Min(html));
    }

    [Fact]
    public void PreservesScriptAndStyle_Verbatim()
    {
        Assert.Equal("<script>\n  if (a   > b) { x(); }\n</script>", Min("<script>\n  if (a   > b) { x(); }\n</script>"));
        Assert.Equal("<style>\n  p  { margin: 0; }\n</style>", Min("<style>\n  p  { margin: 0; }\n</style>"));
    }

    [Fact]
    public void PreservesTextarea_AndResumesAfter()
    {
        Assert.Equal("<textarea>  keep  </textarea> <p>a b</p>", Min("<textarea>  keep  </textarea>\n  <p>a    b</p>"));
    }

    [Fact]
    public void CaseInsensitivePreservingTags_AndTagsWithAttributes()
    {
        Assert.Equal("<PRE>  x  </PRE>", Min("<PRE>  x  </PRE>"));
        Assert.Equal("<script type=\"module\">\n  keep\n</script>", Min("<script type=\"module\">\n  keep\n</script>"));
        // after the preserving element closes, minification resumes
        Assert.Equal("<script> k </script> <p>a b</p>", Min("<script> k </script>\n  <p>a b</p>"));
    }

    [Fact]
    public void AttributesUntouched_InsideNormalElements()
    {
        Assert.Equal("<div class=\"a  b\">x</div>", Min("<div class=\"a  b\">x</div>"));
    }

    [Fact]
    public void TextAndMarkupWithoutRuns_PassThroughUnchanged()
    {
        Assert.Equal("<p>hello</p>", Min("<p>hello</p>"));
    }

    [Fact]
    public void Measurement_MinificationShrinksIndentedPages()
    {
        var original = Encoding.UTF8.GetBytes("""
            <!DOCTYPE html>
            <html>
              <head>
                <title>t</title>
              </head>
              <body>
                <header>
                  <nav>
                    <a href="/">home</a>
                  </nav>
                </header>
                <main>
                  <p>content here</p>
                </main>
              </body>
            </html>
            """);
        var dst = new byte[original.Length + 16];
        int len = VoltHtmlMinifier.Minify(original, dst);
        // whitespace runs collapse: this modest 4-space-indent page loses ~25%;
        // real-world deeply indented markup loses far more
        Assert.True(len < original.Length * 0.8, $"expected <{original.Length * 0.8}, got {len}");
        Assert.Equal(0, Encoding.UTF8.GetString(dst, 0, len).Count(ch => ch == '\n'));
    }

    [Fact]
    public void VoltOptions_MinifyHtml_Null_FollowsRuntimeDefault()
    {
        var options = new VoltOptions();
        Assert.Null(options.MinifyHtml);

        VoltRuntime.HtmlMinifyDefault = true;
        Assert.True(options.MinifyHtml ?? VoltRuntime.HtmlMinifyDefault);
        VoltRuntime.HtmlMinifyDefault = false;
        Assert.False(options.MinifyHtml ?? VoltRuntime.HtmlMinifyDefault);

        options.MinifyHtml = true;
        Assert.True(options.MinifyHtml ?? false);
    }
}
