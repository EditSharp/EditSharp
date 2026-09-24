using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace EditSharp.Components
{
    /// <summary>The font families installed on this machine, by name.</summary>
    public static class FontFamilies
    {
        private static readonly ConcurrentDictionary<string, bool> Warned = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every installed family, Title Cased, sorted, read afresh each time so a new install shows up.</summary>
        public static IReadOnlyList<string> Installed =>
            [.. SKFontManager.Default.GetFontFamilies()
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(TitleCase)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)];

        public static bool IsInstalled(string family) =>
            SKFontManager.Default.GetFontFamilies().Contains(family, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Each word's first letter capitalized; a word already in capitals
        /// (UI, MS) is left as it is. Nothing is lowercased.
        /// </summary>
        public static string TitleCase(string name) => string.Join(' ', name.Split(' ').Select(word =>
            word.Length == 0 || char.IsUpper(word[0]) ? word : char.ToUpperInvariant(word[0]) + word[1..]));

        /// <summary>The family's typeface in this style; a missing family draws with the default font and warns once.</summary>
        public static SKTypeface Resolve(string family, SKFontStyle style)
        {
            SKTypeface? typeface = string.IsNullOrWhiteSpace(family) ? null : SKFontManager.Default.MatchFamily(family, style);
            if (typeface is not null) return typeface;

            if (Warned.TryAdd(family ?? "", true))
                EditSharpConfig.Logger.LogWarning($"The font '{family}' isn't installed; drawing with the default font.");

            return SKFontManager.Default.MatchFamily(null, style) ?? SKTypeface.Default;
        }
    }
}
