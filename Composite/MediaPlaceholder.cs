using System;
using System.Collections.Generic;
using SkiaSharp;

namespace EditSharp.Composite
{
    /// <summary>
    /// A small, fixed, cached "media offline" image — DECIDED IN
    /// CONVERSATION, direct response to real-world testing: previously,
    /// any media ScrubFrameSource couldn't yet (or couldn't ever) resolve —
    /// a scrub proxy still building, a missing/corrupt static image, a text
    /// rasterization failure — either threw outright (breaking the whole
    /// composite for that frame) or, for the proxy-build case specifically,
    /// meant ScrubToAsync's own caller BLOCKED until the build finished
    /// (see Playback's own remarks, SCRUB PROXY BUILDS RUN ON A REAL
    /// BACKGROUND THREAD, NEVER BLOCK SESSION STARTUP). Now, ScrubFrameSource
    /// substitutes this placeholder instead, for ANY input node it can't
    /// currently resolve real content for — see its own class remarks.
    ///
    /// SCRUB/REVERSE PREVIEW ONLY, DELIBERATELY NOT WIRED INTO
    /// SkClipContentSource (forward Playback or Render/*) — decided in
    /// conversation: silently substituting a placeholder for genuinely
    /// broken source media in a real export, or in normal forward playback,
    /// could hide an actual problem a user needs to see and fix; scrubbing/
    /// reverse is already an approximate PREVIEW by design (see
    /// ScrubProxyFormat's own class remarks on the "accurate to the proxy,
    /// not the source" trade-off this whole mechanism makes), so a
    /// placeholder standing in briefly (or indefinitely, for something
    /// genuinely broken) fits that same spirit.
    ///
    /// CACHED, KEYED BY (WIDTH, HEIGHT), FOR THE LIFETIME OF THE PROCESS —
    /// rendering the placeholder (a solid background, a diagonal hazard-
    /// stripe pattern, and a centered "MEDIA OFFLINE" label) is cheap but
    /// not free, and in practice only a small, fixed handful of distinct
    /// (width, height) pairs are ever requested (canvas resolution rarely
    /// changes within one Playback instance's life — see Playback's own
    /// remarks on RenderSettings.Resolution being assumed constant).
    /// DELIBERATELY NEVER DISPOSED/EVICTED — this is a small, static,
    /// process-wide cache, not owned by any one ScrubFrameSource instance;
    /// callers must NEVER call .Dispose() on the SKImage this returns (see
    /// ScrubFrameSource's own remarks on why it tracks broken nodes
    /// separately rather than storing this shared image in a dictionary
    /// that gets disposed on session teardown).
    ///
    /// TEXT DRAWN VIA THE MODERN SKFont API, NOT SKPaint.TextSize/
    /// TextAlign/Typeface (fixed here, real bug caught in review before
    /// this ever shipped on real hardware): the first version of this file
    /// set `TextSize`/`TextAlign`/`Typeface` directly on an `SKPaint` and
    /// called the single-argument `canvas.DrawText(string, x, y, paint)`
    /// overload — that whole API was removed from the SkiaSharp version
    /// this project actually targets (confirmed by every OTHER text call
    /// site in this codebase, e.g. TextRasterizer.Rasterize, which already
    /// uses `SKFont` + `canvas.DrawText(text, x, y, SKTextAlign, font,
    /// paint)` exclusively — `SKPaint` here only ever carries
    /// Color/IsAntialias/Style). This file now matches that same idiom: a
    /// disposable `SKFont` built from `SKFontManager.Default.MatchFamily`
    /// (the same resolution path `FontFace.ToTypeface` uses, just with a
    /// null family name for "whatever the system's default is" — there is
    /// no font-family concept for this internal-only placeholder), and
    /// real vertical centering via `SKFont.Metrics` (Ascent/Descent) rather
    /// than the old fudge-factor offset — see TextRasterizer.MeasureBlock
    /// for the same Ascent/Descent-based baseline math this mirrors.
    /// </summary>
    internal static class MediaPlaceholder
    {
        private static readonly Dictionary<(int Width, int Height), SKImage> Cache = new();
        private static readonly object Lock = new();

        /// <summary>
        /// Returns the shared placeholder image for `width`x`height`,
        /// rendering and caching it on first request for that size. NEVER
        /// dispose the returned image — see class remarks.
        /// </summary>
        public static SKImage Get(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);

            lock (Lock)
            {
                if (Cache.TryGetValue((width, height), out SKImage? cached))
                    return cached;

                SKImage image = Render(width, height);
                Cache[(width, height)] = image;
                return image;
            }
        }

        /// <summary>
        /// Draws into a plain SKBitmap (raster, no GPU context needed —
        /// this must be renderable even when nothing else about a scrub
        /// session, including its own GPU context, is ready yet) and hands
        /// back an independent SKImage copy via SKImage.FromBitmap, so the
        /// bitmap itself can be disposed immediately without invalidating
        /// the cached result — unlike an SKSurface snapshot, which stays
        /// tied to its surface's lifetime.
        /// </summary>
        private static SKImage Render(int width, int height)
        {
            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(new SKColor(28, 28, 32));

                // Diagonal hazard stripes — reads as "no signal" rather
                // than an unintentional plain gray box, at a glance and at
                // small preview sizes.
                int shortSide = Math.Min(width, height);
                int stripeSpacing = Math.Max(6, shortSide / 10);
                using var stripePaint = new SKPaint { Color = new SKColor(20, 20, 24), IsAntialias = false };

                for (int x = -height; x < width; x += stripeSpacing)
                {
                    using var path = new SKPath();
                    float half = stripeSpacing / 2f;
                    path.MoveTo(x, 0);
                    path.LineTo(x + height, height);
                    path.LineTo(x + height + half, height);
                    path.LineTo(x + half, 0);
                    path.Close();
                    canvas.DrawPath(path, stripePaint);
                }

                // Centered label — skipped entirely for a placeholder too
                // small to legibly fit any text at all (a tiny scrub
                // thumbnail, say), where the stripe pattern alone is the
                // whole signal.
                float fontSize = Math.Max(10, shortSide / 8f);
                if (shortSide >= 32)
                {
                    const string label = "MEDIA OFFLINE";

                    // Same family-resolution path FontFace.ToTypeface uses
                    // (SKFontManager.Default.MatchFamily) — null family
                    // name asks for whatever the system considers default,
                    // which is all a plain internal placeholder needs.
                    using SKTypeface typeface =
                        SKFontManager.Default.MatchFamily(null, SKFontStyle.Normal)
                        ?? SKTypeface.CreateDefault();
                    using var font = new SKFont(typeface, fontSize);

                    using var textPaint = new SKPaint
                    {
                        Color = SKColors.White,
                        IsAntialias = true,
                        Style = SKPaintStyle.Fill,
                    };

                    // Real vertical centering via font metrics, matching
                    // TextRasterizer.MeasureBlock's own Ascent/Descent
                    // baseline math — DrawText positions by baseline, so
                    // the midpoint between ascent and descent (both
                    // negative-above/positive-below-baseline per Skia's
                    // convention) is what actually centers the glyphs'
                    // visual box on `height / 2f`, not just the baseline
                    // itself.
                    SKFontMetrics metrics = font.Metrics;
                    float baseline = (height / 2f) - (metrics.Ascent + metrics.Descent) / 2f;

                    canvas.DrawText(label, width / 2f, baseline, SKTextAlign.Center, font, textPaint);
                }

                canvas.Flush();
            }

            return SKImage.FromBitmap(bitmap);
        }
    }
}