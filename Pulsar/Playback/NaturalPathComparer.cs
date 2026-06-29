using System;
using System.Collections.Generic;

namespace Pulsar.Playback;

/// <summary>
/// Compares file paths in a human-friendly, number-aware order.
///
/// Used so that we sort files like 11_mysong.mp3 and 113_myothersong.mp3
/// in a way that actually makes sense to humans.
///
/// To do that, we split paths by dir separators {'/', '\'} and compare
/// segment by segment. Within each segment, contiguous digits compare
/// as numbers. Then we tie-break with normal ordinal comparison.
/// </summary>
public sealed class NaturalPathComparer : IComparer<string>
{
    public static readonly NaturalPathComparer Instance = new();

    private static readonly char[] Separators = ['/', '\\'];

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var xs = x.Split(Separators);
        var ys = y.Split(Separators);

        var common = Math.Min(xs.Length, ys.Length);
        for (var i = 0; i < common; i++)
        {
            var c = CompareSegment(xs[i], ys[i]);
            if (c != 0) return c;
        }

        // All shared segments equal, shortest path wins.
        if (xs.Length != ys.Length) return xs.Length - ys.Length;

        // At this point, they're equal by our custom logic.
        // So just fall back to normal ordinal compare.
        return string.CompareOrdinal(x, y);
    }

    private static int CompareSegment(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var ra = ReadDigits(a, ref i);
                var rb = ReadDigits(b, ref j);
                var c = CompareNumeric(ra, rb);
                if (c != 0) return c;
            }
            else
            {
                var c = CompareCharInsensitive(a[i], b[j]);
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return a.Length - i - (b.Length - j);
    }

    private static string ReadDigits(string s, ref int index)
    {
        var start = index;
        while (index < s.Length && char.IsDigit(s[index])) index++;
        return s[start..index];
    }

    private static int CompareNumeric(string a, string b)
    {
        var at = a.TrimStart('0');
        var bt = b.TrimStart('0');
        if (at.Length != bt.Length) return at.Length - bt.Length;
        return string.CompareOrdinal(at, bt);
    }

    private static int CompareCharInsensitive(char a, char b) =>
        char.ToLowerInvariant(a).CompareTo(char.ToLowerInvariant(b));
}
