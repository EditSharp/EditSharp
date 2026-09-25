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

        /// <summary>Every installed family, in title case, sorted.</summary>
        /// <remarks>Read again at most every two seconds, so a newly installed font shows up.</remarks>
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

        /// <summary>Whether a family is installed.</summary>
        /// <param name="family">The family's name, in any case.</param>
        /// <returns>True when it's installed.</returns>
        public static bool IsInstalled(string family) =>
            SKFontManager.Default.GetFontFamilies().Contains(family, StringComparer.OrdinalIgnoreCase);

        /// <summary>A name with each word's first letter capitalized; nothing is lowercased, so UI and MS stay as they are.</summary>
        /// <param name="name">The name.</param>
        /// <returns>The name in title case.</returns>
        public static string TitleCase(string name) => string.Join(' ', name.Split(' ').Select(word =>
            word.Length == 0 || char.IsUpper(word[0]) ? word : char.ToUpperInvariant(word[0]) + word[1..]));

        private static readonly (int Weight, string Name)[] WeightNames =
        [
            (100, "Thin"), (200, "Extra Light"), (300, "Light"), (350, "Semilight"), (400, "Regular"),
            (500, "Medium"), (600, "Semibold"), (700, "Bold"), (800, "Extra Bold"), (900, "Black"), (950, "Extra Black"),
        ];

        /// <summary>The weights a family has, lightest first.</summary>
        /// <param name="family">The family's name.</param>
        /// <returns>The weights, from 100 (thin) to 900 (black); empty when the family isn't installed.</returns>
        public static IReadOnlyList<int> WeightsOf(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return [];

            using SKFontStyleSet styles = SKFontManager.Default.GetFontStyles(family);
            return [.. styles.Select(s => s.Weight).Distinct().Order()];
        }

        /// <summary>A weight's usual name, such as "Semibold", from the nearest standard weight.</summary>
        /// <param name="weight">The weight.</param>
        /// <returns>The name.</returns>
        public static string WeightName(int weight) => WeightNames.MinBy(w => Math.Abs(w.Weight - weight)).Name;

        /// <summary>The weight in a list nearest to another.</summary>
        /// <param name="weights">The weights to choose from.</param>
        /// <param name="weight">The weight wanted.</param>
        /// <returns>The nearest; <paramref name="weight"/> itself when the list is empty.</returns>
        public static int Nearest(IReadOnlyList<int> weights, int weight) =>
            weights.Count == 0 ? weight : weights.MinBy(w => Math.Abs(w - weight));

        /// <summary>A family's typeface in a style.</summary>
        /// <remarks>A family that isn't installed gives the default font, and logs a warning once per family.</remarks>
        /// <param name="family">The family's name.</param>
        /// <param name="style">The weight, width and slant.</param>
        /// <returns>The typeface.</returns>
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
