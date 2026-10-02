using System.Text;
using Volt;
using Xunit;

namespace Volt.Core.Tests;

public class HtmlWriterTests
{
    private static string Render(Action<HtmlWriter> action)
    {
        using var buffer = new PooledBufferWriter();
        var w = HtmlWriter.Rent(buffer);
        try { action(w); }
        finally { w.Return(); }
        return Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray());
    }

    [Fact]
    public void DocTypeAndBasicStructure()
    {
        var html = Render(w =>
        {
            w.DocType();
            using (w.Html("en"))
            {
                using (w.Head())
                {
                    w.MetaCharset();
                    w.Title("Test");
                }
                using (w.Body())
                {
                    using (w.H1()) w.Text("Hello");
                }
            }
        });
        Assert.Equal(
            "<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><title>Test</title></head><body><h1>Hello</h1></body></html>",
            html);
    }

    [Fact]
    public void EscapesText()
    {
        var html = Render(w => w.Text("<script>alert('x') & \"more\"</script>"));
        Assert.Equal("&lt;script&gt;alert('x') &amp; \"more\"&lt;/script&gt;", html);
    }

    [Fact]
    public void EscapesAttributes()
    {
        var html = Render(w =>
        {
            using (w.Div()) w.Attr("data-x", "a'b\"c&d");
        });
        Assert.Equal("<div data-x=\"a&#39;b&quot;c&amp;d\"></div>", html);
    }

    [Fact]
    public void ClassAndId()
    {
        var html = Render(w =>
        {
            using (w.Div())
            {
                w.Class("card highlighted");
                w.Id("main");
            }
        });
        Assert.Equal("<div class=\"card highlighted\" id=\"main\"></div>", html);
    }

    [Fact]
    public void VoidElementsDoNotClose()
    {
        var html = Render(w => { w.Br(); w.Hr(); w.Meta("viewport", "width=device-width"); });
        Assert.Equal("<br><hr><meta name=\"viewport\" content=\"width=device-width\">", html);
    }

    [Fact]
    public void NestedElementsWithText()
    {
        var html = Render(w =>
        {
            using (w.Ul())
            {
                w.Class("list");
                using (w.Li()) w.Text("one");
                using (w.Li()) w.Text("two");
            }
        });
        Assert.Equal("<ul class=\"list\"><li>one</li><li>two</li></ul>", html);
    }

    [Fact]
    public void BooleanAttrOmitsValue()
    {
        var html = Render(w =>
        {
            using (w.Input())
            {
                w.Attr("type", "checkbox");
                w.BooleanAttr("checked"u8);
            }
        });
        Assert.Equal("<input type=\"checkbox\" checked>", html);
    }

    [Fact]
    public void RawUtf8Passthrough()
    {
        var html = Render(w => w.RawUtf8("<b>raw</b>"u8));
        Assert.Equal("<b>raw</b>", html);
    }

    [Fact]
    public void ScriptSrcAndLink()
    {
        var html = Render(w =>
        {
            w.ScriptSrc("/app.js", defer: true);
            w.Link("stylesheet", "/site.css");
        });
        Assert.Equal("<script src=\"/app.js\" defer></script><link rel=\"stylesheet\" href=\"/site.css\">", html);
    }

    [Fact]
    public void H1TextConvenience()
    {
        var html = Render(w =>
        {
            w.H1Text("Title");
            w.PText("Paragraph");
        });
        Assert.Equal("<h1>Title</h1><p>Paragraph</p>", html);
    }

    [Fact]
    public void DataVoltAttributes()
    {
        var html = Render(w =>
        {
            using (w.Button())
            {
                w.DataVoltOn("click:increment");
                w.DataVoltKey("btn-1");
            }
        });
        Assert.Equal("<button data-v-on=\"click:increment\" data-v-key=\"btn-1\"></button>", html);
    }

    [Fact]
    public void WriterPoolReusesInstances()
    {
        using var buffer = new PooledBufferWriter();
        var a = HtmlWriter.Rent(buffer);
        var htmlA = a;
        a.Return();
        var b = HtmlWriter.Rent(buffer);
        Assert.Same(htmlA, b);
        b.Return();
    }
}
