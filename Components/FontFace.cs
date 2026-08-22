using System.Collections.Generic;
using SkiaSharp;
 
namespace EditSharp.Components
{
    /// <summary>
    /// Common font families for use with TextOverlay.
    /// </summary>
    public enum FontFace
    {
        Arial,
        ArialBlack,
        Calibri,
        Cambria,
        Candara,
        ComicSansMs,
        Consolas,
        Constantia,
        Corbel,
        CourierNew,
        Georgia,
        Helvetica,
        Impact,
        LucidaConsole,
        LucidaSansUnicode,
        PalatinoLinotype,
        SegoeUi,
        Tahoma,
        TimesNewRoman,
        TrebuchetMs,
        Verdana,
    }
 
    public static class FontFaceExtensions
    {
        private static readonly Dictionary<FontFace, string> FamilyNames = new()
        {
            [FontFace.Arial] = "Arial",
            [FontFace.ArialBlack] = "Arial Black",
            [FontFace.Calibri] = "Calibri",
            [FontFace.Cambria] = "Cambria",
            [FontFace.Candara] = "Candara",
            [FontFace.ComicSansMs] = "Comic Sans MS",
            [FontFace.Consolas] = "Consolas",
            [FontFace.Constantia] = "Constantia",
            [FontFace.Corbel] = "Corbel",
            [FontFace.CourierNew] = "Courier New",
            [FontFace.Georgia] = "Georgia",
            [FontFace.Helvetica] = "Helvetica",
            [FontFace.Impact] = "Impact",
            [FontFace.LucidaConsole] = "Lucida Console",
            [FontFace.LucidaSansUnicode] = "Lucida Sans Unicode",
            [FontFace.PalatinoLinotype] = "Palatino Linotype",
            [FontFace.SegoeUi] = "Segoe UI",
            [FontFace.Tahoma] = "Tahoma",
            [FontFace.TimesNewRoman] = "Times New Roman",
            [FontFace.TrebuchetMs] = "Trebuchet MS",
            [FontFace.Verdana] = "Verdana",
        };
 
        /// <summary>The real font family name SKFontManager needs to resolve this face.</summary>
        public static string ToFamilyName(this FontFace face) => FamilyNames[face];
 
        /// <summary>
        /// Resolves directly to an SKTypeface via the system font manager. Convenience
        /// wrapper around SKFontManager.Default.MatchFamily(face.ToFamilyName(), style).
        /// </summary>
        public static SKTypeface ToTypeface(this FontFace face, SKFontStyle? style = null) =>
            SKFontManager.Default.MatchFamily(face.ToFamilyName(), style ?? SKFontStyle.Normal);
 
        /// <summary>Resolves directly to an SKFont at the given size.</summary>
        public static SKFont ToFont(this FontFace face, float size, SKFontStyle? style = null) =>
            new(face.ToTypeface(style), size);
    }
}
 