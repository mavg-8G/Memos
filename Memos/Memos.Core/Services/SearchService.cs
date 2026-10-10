using System.Text;
using System.Text.RegularExpressions;
using Memos.Core.Models;

namespace Memos.Core.Services;

/// <summary>The query cannot be used: an invalid regular expression, or one that takes too long.</summary>
public sealed class SearchQueryException(string message, bool timedOut = false) : Exception(message)
{
    public bool TimedOut { get; } = timedOut;
}

public interface ISearchService
{
    /// <summary>All non-empty matches, in order. Stops after <paramref name="maxMatches"/>.</summary>
    IReadOnlyList<TextMatch> FindAll(string text, string query, SearchOptions options, CancellationToken cancellationToken = default, int maxMatches = int.MaxValue);

    /// <summary>The edit that replaces one match, or null if the text at that position no longer matches.</summary>
    TextEdit? ReplaceOne(string text, TextMatch match, string query, string replacement, SearchOptions options);

    /// <summary>Replaces every match. Returns the new text and how many replacements were made.</summary>
    (string Text, int Count) ReplaceAll(string text, string query, string replacement, SearchOptions options, CancellationToken cancellationToken = default);
}

public sealed class SearchService : ISearchService
{
    private const string WordChar = @"[\p{L}\p{N}\p{M}_]";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public IReadOnlyList<TextMatch> FindAll(string text, string query, SearchOptions options, CancellationToken cancellationToken = default, int maxMatches = int.MaxValue)
    {
        var matches = new List<TextMatch>();
        if (query.Length == 0 || text.Length == 0)
        {
            return matches;
        }

        var regex = BuildRegex(query, options);
        try
        {
            for (var m = regex.Match(text); m.Success && matches.Count < maxMatches; m = m.NextMatch())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (m.Length > 0)
                {
                    matches.Add(new TextMatch(m.Index, m.Length));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            throw new SearchQueryException("The search took too long.", timedOut: true);
        }

        return matches;
    }

    public TextEdit? ReplaceOne(string text, TextMatch match, string query, string replacement, SearchOptions options)
    {
        if (query.Length == 0 || match.End > text.Length)
        {
            return null;
        }

        var regex = BuildRegex(query, options);
        try
        {
            var m = regex.Match(text, match.Start);
            if (!m.Success || m.Index != match.Start || m.Length != match.Length)
            {
                return null;
            }

            var result = m.Result(ToReplacementPattern(replacement, options));
            return new TextEdit(match.Start, match.Length, result, match.Start + result.Length, 0);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new SearchQueryException("The search took too long.", timedOut: true);
        }
    }

    public (string Text, int Count) ReplaceAll(string text, string query, string replacement, SearchOptions options, CancellationToken cancellationToken = default)
    {
        if (query.Length == 0 || text.Length == 0)
        {
            return (text, 0);
        }

        var regex = BuildRegex(query, options);
        var pattern = ToReplacementPattern(replacement, options);
        var builder = new StringBuilder(text.Length);
        var count = 0;
        var position = 0;
        try
        {
            for (var m = regex.Match(text); m.Success; m = m.NextMatch())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (m.Length == 0)
                {
                    continue;
                }

                builder.Append(text, position, m.Index - position).Append(m.Result(pattern));
                position = m.Index + m.Length;
                count++;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            throw new SearchQueryException("The search took too long.", timedOut: true);
        }

        return count == 0 ? (text, 0) : (builder.Append(text, position, text.Length - position).ToString(), count);
    }

    private static Regex BuildRegex(string query, SearchOptions options)
    {
        var pattern = options.UseRegex ? query : Regex.Escape(query);
        if (options.WholeWord)
        {
            pattern = $"(?<!{WordChar})(?:{pattern})(?!{WordChar})";
        }

        // Multiline so ^ and $ work per line; the editor text only uses '\n'.
        var regexOptions = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!options.MatchCase)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        try
        {
            return new Regex(pattern, regexOptions, Timeout);
        }
        catch (ArgumentException ex)
        {
            throw new SearchQueryException(ex.Message);
        }
    }

    /// <summary>
    /// In plain mode the replacement is literal. In regex mode $1-style groups work and \n, \t and \\ are escapes.
    /// </summary>
    private static string ToReplacementPattern(string replacement, SearchOptions options)
    {
        if (!options.UseRegex)
        {
            return replacement.Replace("$", "$$");
        }

        if (!replacement.Contains('\\'))
        {
            return replacement;
        }

        var sb = new StringBuilder(replacement.Length);
        for (var i = 0; i < replacement.Length; i++)
        {
            var c = replacement[i];
            if (c == '\\' && i + 1 < replacement.Length)
            {
                var next = replacement[++i];
                sb.Append(next switch { 'n' => "\n", 't' => "\t", '\\' => "\\", _ => "\\" + next });
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
