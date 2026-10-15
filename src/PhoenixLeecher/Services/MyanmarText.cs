using System.Text;
using System.Text.RegularExpressions;

namespace PhoenixLeecher.Services;

/// <summary>
/// Myanmar text handling: NFC normalization + heuristic Zawgyi detection/conversion.
/// Rule (user spec): convert when confident; if uncertain, keep the original.
/// Live data so far is all Unicode; this is defensive for older Zawgyi filenames.
/// </summary>
public static class MyanmarText
{
    // Full dedup/display key: NFC -> case-insensitive -> Zawgyi->Unicode -> tidy spaces.
    public static string NormalizeKey(string s)
    {
        s = (s ?? "").Normalize(NormalizationForm.FormC).ToLowerInvariant();
        if (IsZawgyi(s))
            s = ZawgyiToUnicode(s);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    // Display form: NFC + Zawgyi->Unicode (no case folding).
    public static string ForDisplay(string s)
    {
        s = (s ?? "").Normalize(NormalizationForm.FormC);
        return IsZawgyi(s) ? ZawgyiToUnicode(s) : s;
    }

    /// <summary>
    /// Heuristic Zawgyi detection.
    /// - Zawgyi-only codepoints: U+106A, U+107E..U+1084
    /// - U+1031 (ေ) stored AFTER a consonant (visual order) instead of before it (Unicode logical order)
    /// </summary>
    public static bool IsZawgyi(string s)
    {
        foreach (char c in s)
            if (c == '\u106A' || (c >= '\u107E' && c <= '\u1084'))
                return true;
        // consonant (or medial/stack) followed by 1031 -> Zawgyi visual order
        for (int i = 1; i < s.Length; i++)
            if (s[i] == '\u1031' && IsConsonant(s[i - 1]))
                return true;
        return false;
    }

    private static bool IsConsonant(char c) =>
        (c >= '\u1000' && c <= '\u1021');

    /// <summary>
    /// Converts common Zawgyi patterns to Unicode.
    /// Handles the dominant case: U+1031 stored after the consonant -> moved before it.
    /// Exotic codepoints we cannot map safely are left untouched (spec: keep original if uncertain).
    /// </summary>
    public static string ZawgyiToUnicode(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            // Pattern: C + 1031  ->  1031 + C   (most common Zawgyi error)
            if (i + 1 < s.Length && IsConsonant(c) && s[i + 1] == '\u1031')
            {
                sb.Append('\u1031');
                sb.Append(c);
                i += 2;
                continue;
            }
            // Pattern: C + medial(103B/103C/103D) + 1031 -> 1031 + C + medial
            if (i + 2 < s.Length && IsConsonant(c) && IsMedial(s[i + 1]) && s[i + 2] == '\u1031')
            {
                sb.Append('\u1031');
                sb.Append(c);
                sb.Append(s[i + 1]);
                i += 3;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsMedial(char c) =>
        c == '\u103B' || c == '\u103C' || c == '\u103D' || c == '\u103E';
}
