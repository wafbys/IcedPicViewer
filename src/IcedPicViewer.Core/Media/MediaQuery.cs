// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using IcedPicViewer.Models;

namespace IcedPicViewer.Core.Media;

/// <summary>Which media kinds the gallery shows.</summary>
public enum MediaFilter
{
    All,
    Image,
    Video,
}

/// <summary>Sort key for the gallery view. Values map to combo-box order.</summary>
public enum MediaSortKey
{
    Name,
    Date,
    Size,
    Extension,
}

/// <summary>
/// Pure gallery-query logic shared by the shell: kind filtering, wildcard /
/// substring search, and ordering comparisons. Kept in Core so it is
/// unit-testable without WinUI.
/// </summary>
public static class MediaQuery
{
    private static readonly char[] WildcardChars = ['*', '?'];

    // Bounded so a hostile / accidental flood of distinct patterns cannot grow
    // the process without limit.
    private const int RegexCacheCapacity = 256;
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    public static bool MatchesFilter(MediaFilter filter, MediaKind kind) => filter switch
    {
        MediaFilter.Image => kind == MediaKind.Image,
        MediaFilter.Video => kind == MediaKind.Video,
        _ => true,
    };

    /// <summary>File name shown in the UI (archive entries use the entry name).</summary>
    public static string GetDisplayName(MediaRef media) =>
        media.IsInArchive
            ? Path.GetFileName(media.ArchiveEntry!)
            : Path.GetFileName(media.Path);

    public static string GetExtension(MediaRef media) => Path.GetExtension(GetDisplayName(media));

    /// <summary>
    /// Matches <paramref name="name"/> against <paramref name="pattern"/>.
    /// A pattern with no <c>*</c>/<c>?</c> is a case-insensitive substring
    /// match; otherwise it is a wildcard match over the whole name.
    /// </summary>
    public static bool MatchesSearch(string name, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        pattern = pattern.Trim();
        if (pattern.IndexOfAny(WildcardChars) < 0)
            return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

        var regex = GetWildcardRegex(pattern);
        try
        {
            return regex.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Regex GetWildcardRegex(string pattern)
    {
        var regex = RegexCache.GetOrAdd(pattern, static p => BuildWildcardRegex(p));
        if (RegexCache.Count > RegexCacheCapacity)
        {
            // Cheap best-effort trim; the cache is only an optimisation.
            foreach (var key in RegexCache.Keys)
            {
                if (RegexCache.Count <= RegexCacheCapacity) break;
                RegexCache.TryRemove(key, out _);
            }
        }
        return regex;
    }

    private static Regex BuildWildcardRegex(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 8);
        sb.Append('^');
        foreach (var ch in pattern)
        {
            switch (ch)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                default: sb.Append(Regex.Escape(ch.ToString())); break;
            }
        }
        sb.Append('$');
        return new Regex(
            sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200));
    }

    /// <summary>Natural, case-insensitive name order (IMG_2 before IMG_10).</summary>
    public static int CompareName(MediaRef a, MediaRef b)
    {
        var byName = NaturalStringComparer.OrdinalIgnoreCase.Compare(GetDisplayName(a), GetDisplayName(b));
        return byName != 0 ? byName : string.CompareOrdinal(a.ToString(), b.ToString());
    }

    /// <summary>Extension first, then natural name.</summary>
    public static int CompareExtension(MediaRef a, MediaRef b)
    {
        var byExt = NaturalStringComparer.OrdinalIgnoreCase.Compare(GetExtension(a), GetExtension(b));
        return byExt != 0 ? byExt : CompareName(a, b);
    }
}

/// <summary>
/// Classic natural-order string comparison: runs of digits compare by numeric
/// value (so "2" &lt; "10"), everything else case-insensitively.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer OrdinalIgnoreCase = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                // Ignore leading zeros, then compare by significant length
                // (longer number wins) and finally digit by digit.
                var si = i;
                var sj = j;
                while (si < x.Length && x[si] == '0') si++;
                while (sj < y.Length && y[sj] == '0') sj++;

                var ei = si;
                var ej = sj;
                while (ei < x.Length && char.IsDigit(x[ei])) ei++;
                while (ej < y.Length && char.IsDigit(y[ej])) ej++;

                var lenI = ei - si;
                var lenJ = ej - sj;
                if (lenI != lenJ) return lenI - lenJ;

                for (var k = 0; k < lenI; k++)
                {
                    var d = x[si + k] - y[sj + k];
                    if (d != 0) return d;
                }

                i = ei;
                j = ej;
            }
            else
            {
                var d = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (d != 0) return d;
                i++;
                j++;
            }
        }

        return (x.Length - i) - (y.Length - j);
    }
}
