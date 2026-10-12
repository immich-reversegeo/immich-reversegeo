using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ImmichReverseGeo.Web.Services;

public sealed record LocationTextSegment(string Text, bool Changed);

public static class LocationTextDiff
{
    public static IReadOnlyList<LocationTextSegment> Compare(string? value, string? other)
    {
        if (value == other)
        {
            return [new(DisplayValue(value), false)];
        }

        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(other))
        {
            return [new(DisplayValue(value), true)];
        }

        // Compare text elements so accents and emoji are never split inside a highlight.
        var left = TextElements(value);
        var right = TextElements(other);
        var changed = Enumerable.Repeat(true, left.Length).ToArray();
        // Bound memory for unusually long metadata. Keep shared ends in that case.
        if ((long)left.Length * right.Length > 262_144)
        {
            var prefix = 0;
            while (prefix < Math.Min(left.Length, right.Length) && left[prefix] == right[prefix])
            {
                changed[prefix++] = false;
            }

            var suffix = 0;
            while (suffix < Math.Min(left.Length, right.Length) - prefix
                && left[^(suffix + 1)] == right[^(suffix + 1)])
            {
                changed[left.Length - ++suffix] = false;
            }
        }
        else
        {
            var lengths = new int[left.Length + 1, right.Length + 1];
            for (var i = left.Length - 1; i >= 0; i--)
            {
                for (var j = right.Length - 1; j >= 0; j--)
                {
                    lengths[i, j] = left[i] == right[j]
                        ? lengths[i + 1, j + 1] + 1
                        : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
                }
            }

            var x = 0;
            var y = 0;
            while (x < left.Length && y < right.Length)
            {
                if (left[x] == right[y])
                {
                    changed[x++] = false;
                    y++;
                }
                else if (lengths[x + 1, y] >= lengths[x, y + 1])
                {
                    x++;
                }
                else
                {
                    y++;
                }
            }
        }

        var segments = new List<LocationTextSegment>();
        var start = 0;
        for (var i = 1; i <= left.Length; i++)
        {
            if (i == left.Length || changed[i] != changed[start])
            {
                segments.Add(new(string.Concat(left[start..i]), changed[start]));
                start = i;
            }
        }

        return segments;
    }

    public static string DisplayValue(string? value) => value is null ? "—" : value.Length == 0 ? "(empty)" : value;

    public static string VisibleChanges(string text)
    {
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            result.Append(rune.Value switch
            {
                ' ' => "␠",
                '\t' => "⇥",
                '\r' => "␍",
                '\n' => "↵",
                _ when Rune.IsWhiteSpace(rune) || category is UnicodeCategory.Format or UnicodeCategory.Control
                    => $"⟦U+{rune.Value:X4}⟧",
                _ => rune.ToString()
            });
        }

        return result.ToString();
    }

    public static string DescribeChanges(string text) =>
        "Changed characters: " + string.Join(" ", text.EnumerateRunes().Select(rune => $"U+{rune.Value:X4}"));

    private static string[] TextElements(string value)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        return elements.ToArray();
    }
}
