namespace Memos.Core.Helpers;

/// <summary>The smallest single replacement that turns one string into another.</summary>
public readonly record struct TextChange(int Start, int RemovedLength, int InsertedLength)
{
    public int Delta => InsertedLength - RemovedLength;
}

public static class TextDiff
{
    /// <summary>
    /// Finds the changed region by trimming the common prefix and suffix. The region never starts or
    /// ends in the middle of a surrogate pair, so it can be applied to a UTF-16 editing control safely.
    /// </summary>
    public static TextChange Compute(string oldText, string newText)
    {
        var a = oldText.AsSpan();
        var b = newText.AsSpan();

        var prefix = a.CommonPrefixLength(b);
        if (prefix > 0 && prefix < a.Length && char.IsHighSurrogate(a[prefix - 1]))
        {
            prefix--;
        }
        else if (prefix > 0 && prefix < b.Length && char.IsHighSurrogate(b[prefix - 1]))
        {
            prefix--;
        }

        var maxSuffix = Math.Min(a.Length, b.Length) - prefix;
        var suffix = 0;
        while (suffix < maxSuffix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        if (suffix > 0 && char.IsLowSurrogate(a[a.Length - suffix]))
        {
            suffix--;
        }

        return new TextChange(prefix, a.Length - prefix - suffix, b.Length - prefix - suffix);
    }
}
