using ColorCode;
using System.Collections.Concurrent;

namespace Memos.Core.Markdown;

/// <summary>
/// Colours fenced code through ColorCode. Highlighting is by far the most expensive part of
/// rendering and a document is re-rendered on every edit, so results are remembered: only the
/// block being typed in is ever highlighted again.
/// </summary>
internal static class CodeHighlighter
{
    /// <summary>Characters of code plus HTML one generation may hold before the older one is dropped.</summary>
    internal static int GenerationBudget { get; set; } = 4_000_000;

    private static readonly ConcurrentDictionary<string, ILanguage?> LanguagesByInfo = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    // Two generations make a cheap LRU: entries still in use move to the recent one, and what was
    // not asked for during a whole generation goes away with the older one.
    private static Dictionary<Key, string> _recent = [];
    private static Dictionary<Key, string> _older = [];
    private static int _recentSize;

    private readonly record struct Key(string LanguageId, string Code);

    /// <summary>The highlighted HTML of a piece of code.</summary>
    public static string Highlight(ILanguage language, string code)
    {
        var key = new Key(language.Id, code);
        lock (Gate)
        {
            if (_recent.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (_older.TryGetValue(key, out cached))
            {
                Remember(key, cached);
                return cached;
            }
        }

        var html = new HtmlClassFormatter().GetHtmlString(code, language);
        lock (Gate)
        {
            Remember(key, html);
        }

        return html;
    }

    internal static int CachedEntries
    {
        get
        {
            lock (Gate)
            {
                return _recent.Count + _older.Count;
            }
        }
    }

    private static void Remember(Key key, string html)
    {
        var size = key.Code.Length + html.Length;
        if (_recentSize + size > GenerationBudget && _recent.Count > 0)
        {
            _older = _recent;
            _recent = [];
            _recentSize = 0;
        }

        if (_recent.TryAdd(key, html))
        {
            _recentSize += size;
        }
    }

    /// <summary>The language an info string (what follows the opening fence) names, or null.</summary>
    public static ILanguage? FindLanguage(string info)
    {
        if (LanguagesByInfo.TryGetValue(info, out var known))
        {
            return known;
        }

        var id = info.Trim().ToLowerInvariant() switch
        {
            "cs" or "csharp" => "c#",
            "js" or "jsx" or "node" => "javascript",
            "ts" or "tsx" => "typescript",
            "py" => "python",
            "ps1" or "pwsh" => "powershell",
            "c++" or "cc" or "h" or "hpp" or "c" => "cpp",
            "htm" => "html",
            "xaml" or "svg" => "xml",
            "fs" => "f#",
            "vb" => "vb.net",
            var other => other,
        };

        ILanguage? language;
        try
        {
            language = Languages.FindById(id);
        }
        catch (Exception)
        {
            language = null;
        }

        // Info strings are whatever the user types after the fence; do not keep every one forever.
        if (LanguagesByInfo.Count < 512)
        {
            LanguagesByInfo.TryAdd(info, language);
        }

        return language;
    }
}
