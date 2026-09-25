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

        private static (IReadOnlyList<string> Families, long ReadAt)? _installed;

        /// <summary>Every installed family, Title Cased, sorted. Re-read at most every two seconds, so a new install shows up.</summary>
        public static IReadOnlyList<string> Installed
        {
            get
            {
                if (_installed is { } cached && Environment.TickCount64 - cached.ReadAt < 2000) return cached.Families;

                IReadOnlyList<string> families = [.. SKFontManager.Default.GetFontFamilies()
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(TitleCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)];

                _installed = (families, Environment.TickCount64);
                return families;
            }
        }

        public static bool IsInstalled(string family) =>
            SKFontManager.Default.GetFontFamilies().Contains(family, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Each word's first letter capitalized; a word already in capitals
        /// (UI, MS) is left as it is. Nothing is lowercased.
        /// </summary>
        public static string TitleCase(string name) => string.Join(' ', name.Split(' ').Select(word =>
            word.Length == 0 || char.IsUpper(word[0]) ? word : char.ToUpperInvariant(word[0]) + word[1..]));

        private static readonly (int Weight, string Name)[] WeightNames =
        [
            (100, "Thin"), (200, "Extra Light"), (300, "Light"), (350, "Semilight"), (400, "Regular"),
            (500, "Medium"), (600, "Semibold"), (700, "Bold"), (800, "Extra Bold"), (900, "Black"), (950, "Extra Black"),
        ];

        /// <summary>The weights a family ships, lightest first; empty when it isn't installed.</summary>
        public static IReadOnlyList<int> WeightsOf(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return [];

            using SKFontStyleSet styles = SKFontManager.Default.GetFontStyles(family);
            return [.. styles.Select(s => s.Weight).Distinct().Order()];
        }

        /// <summary>A weight's usual name ("Semibold"), by the nearest standard weight.</summary>
        public static string WeightName(int weight) => WeightNames.MinBy(w => Math.Abs(w.Weight - weight)).Name;

        /// <summary>Of `weights`, the one nearest `weight`; `weight` itself when there are none.</summary>
        public static int Nearest(IReadOnlyList<int> weights, int weight) =>
            weights.Count == 0 ? weight : weights.MinBy(w => Math.Abs(w - weight));

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
