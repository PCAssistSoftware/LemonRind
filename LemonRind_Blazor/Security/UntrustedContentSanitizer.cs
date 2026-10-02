namespace LemonRindBlazor.Security;

/// <summary>
/// The Web reader and Web search modules already warn the model in plain
/// text not to treat fetched content as instructions, but that warning is
/// just more text the model reads - it does nothing to stop a page hiding
/// an instruction inside characters that render as invisible, or inside
/// letters that look like the warning's own alphabet but aren't. This closes
/// that gap: strips the actual bytes a page could use for those two tricks
/// before the content ever reaches the model, as a second, independent
/// layer alongside the plain-text warning.
///
/// Ported directly from the VB.NET/WPF LemonRind app's
/// Security\UntrustedContentSanitizer.vb.
/// </summary>
public static class UntrustedContentSanitizer
{
    private static readonly HashSet<char> ZeroWidthChars =
    [
        '​', '‌', '‍', '‎', '‏',
        '‪', '‫', '‬', '‭', '‮',
        '⁠', '⁡', '⁢', '⁣', '⁤',
        '﻿', '­',
    ];

    private static readonly Dictionary<char, char> HomoglyphMap = new()
    {
        ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z',
        ['Η'] = 'H', ['Ι'] = 'I', ['Κ'] = 'K', ['Μ'] = 'M',
        ['Ν'] = 'N', ['Ο'] = 'O', ['Ρ'] = 'P', ['Τ'] = 'T',
        ['Υ'] = 'Y', ['Χ'] = 'X',
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K',
        ['М'] = 'M', ['Н'] = 'H', ['О'] = 'O', ['Р'] = 'P',
        ['С'] = 'C', ['Т'] = 'T', ['Х'] = 'X',
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p',
        ['с'] = 'c', ['х'] = 'x', ['у'] = 'y',
    };

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (ZeroWidthChars.Contains(c)) continue;

            builder.Append(HomoglyphMap.TryGetValue(c, out var replacement) ? replacement : c);
        }

        return builder.ToString();
    }
}
