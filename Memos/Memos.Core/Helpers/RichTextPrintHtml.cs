using System.Net;
using System.Text;

namespace Memos.Core.Helpers;

/// <summary>Renders the character styles supported by Memos for its print preview.</summary>
public static class RichTextPrintHtml
{
    public static string BuildPage(string title, string rtf)
    {
        var body = new StringBuilder();
        foreach (var run in RichTextCodec.Parse(rtf))
        {
            if (run.Bold) body.Append("<strong>");
            if (run.Italic) body.Append("<em>");
            if (run.Underline) body.Append("<u>");
            if (run.Strikethrough) body.Append("<s>");
            body.Append(WebUtility.HtmlEncode(run.Text).Replace("\n", "<br>"));
            if (run.Strikethrough) body.Append("</s>");
            if (run.Underline) body.Append("</u>");
            if (run.Italic) body.Append("</em>");
            if (run.Bold) body.Append("</strong>");
        }

        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) +
            "</title><style>@page{margin:18mm}body{font:11pt 'Segoe UI',sans-serif;color:#111;white-space:pre-wrap;overflow-wrap:anywhere;tab-size:4}</style></head><body>" + body + "</body></html>";
    }
}
