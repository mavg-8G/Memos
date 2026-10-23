using ColorCode;
using ColorCode.Common;
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
    private static Dictionary<Key, object> _recent = [];
    private static Dictionary<Key, object> _older = [];
    private static int _recentSize;

    /// <param name="Native">Spans for native text rather than HTML.</param>
    private readonly record struct Key(string LanguageId, string Code, bool Native);

    /// <summary>The highlighted HTML of a piece of code.</summary>
    public static string Highlight(ILanguage language, string code) =>
        (string)GetOrAdd(new Key(language.Id, code, Native: false), () => new HtmlClassFormatter().GetHtmlString(code, language), code.Length);

    /// <summary>The code split into stretches of one token kind each, for native text.</summary>
    public static IReadOnlyList<PrintSpan> Tokenize(ILanguage language, string code) =>
        (IReadOnlyList<PrintSpan>)GetOrAdd(new Key(language.Id, code, Native: true), () => new TokenColorizer().Tokenize(code, language), code.Length);

    private static object GetOrAdd(Key key, Func<object> create, int size)
    {
        lock (Gate)
        {
            if (_recent.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (_older.TryGetValue(key, out cached))
            {
                Remember(key, cached, size);
                return cached;
            }
        }

        var value = create();
        lock (Gate)
        {
            Remember(key, value, size + (value is string html ? html.Length : size));
        }

        return value;
    }

    /// <summary>Receives ColorCode's scopes and turns them into spans.</summary>
    private sealed class TokenColorizer() : CodeColorizerBase(null, null)
    {
        private readonly List<PrintSpan> _spans = [];

        public List<PrintSpan> Tokenize(string code, ILanguage language)
        {
            languageParser.Parse(code, language, Write);
            return _spans;
        }

        protected override void Write(string parsedSourceCode, IList<ColorCode.Parsing.Scope> scopes)
        {
            // Scopes nest; children are painted over their parents.
            var tokens = new CodeToken[parsedSourceCode.Length];
            Paint(tokens, scopes);
            var start = 0;
            for (var i = 1; i <= tokens.Length; i++)
            {
                if (i == tokens.Length || tokens[i] != tokens[start])
                {
                    var text = parsedSourceCode[start..i];
                    if (_spans.Count > 0 && _spans[^1].Token == tokens[start])
                    {
                        _spans[^1] = _spans[^1] with { Text = _spans[^1].Text + text };
                    }
                    else
                    {
                        _spans.Add(new PrintSpan(text, Code: true) { Token = tokens[start] });
                    }

                    start = i;
                }
            }
        }

        private static void Paint(CodeToken[] tokens, IList<ColorCode.Parsing.Scope> scopes)
        {
            foreach (var scope in scopes)
            {
                var token = ToToken(scope.Name);
                var end = Math.Min(tokens.Length, scope.Index + scope.Length);
                for (var i = Math.Max(0, scope.Index); i < end; i++)
                {
                    tokens[i] = token;
                }

                Paint(tokens, scope.Children);
            }
        }

        private static CodeToken ToToken(string scope) => scope switch
        {
            ScopeName.Keyword or ScopeName.ControlKeyword or ScopeName.PseudoKeyword or ScopeName.Predefined or ScopeName.JsonConst
                or ScopeName.BuiltinValue or ScopeName.PowerShellCommand or ScopeName.SqlSystemFunction => CodeToken.Keyword,
            ScopeName.String or ScopeName.StringCSharpVerbatim or ScopeName.StringEscape or ScopeName.JsonString
                or ScopeName.XmlAttributeValue or ScopeName.HtmlAttributeValue or ScopeName.CssPropertyValue => CodeToken.String,
            ScopeName.Comment or ScopeName.XmlComment or ScopeName.XmlDocComment or ScopeName.XmlDocTag or ScopeName.HtmlComment => CodeToken.Comment,
            ScopeName.Number or ScopeName.JsonNumber => CodeToken.Number,
            ScopeName.ClassName or ScopeName.Type or ScopeName.TypeVariable or ScopeName.PowerShellType or ScopeName.NameSpace
                or ScopeName.Constructor or ScopeName.BuiltinFunction => CodeToken.Type,
            ScopeName.PreprocessorKeyword or ScopeName.Attribute or ScopeName.PowerShellAttribute => CodeToken.Preprocessor,
            ScopeName.HtmlElementName or ScopeName.XmlName or ScopeName.CssSelector or ScopeName.JsonKey => CodeToken.Tag,
            ScopeName.HtmlAttributeName or ScopeName.XmlAttribute or ScopeName.CssPropertyName or ScopeName.PowerShellVariable
                or ScopeName.PowerShellParameter => CodeToken.Attribute,
            _ => CodeToken.None,
        };
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

    private static void Remember(Key key, object value, int size)
    {
        if (_recentSize + size > GenerationBudget && _recent.Count > 0)
        {
            _older = _recent;
            _recent = [];
            _recentSize = 0;
        }

        if (_recent.TryAdd(key, value))
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
