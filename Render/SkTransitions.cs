using System;
using SkiaSharp;
using EditSharp.Components.Transitions;

namespace EditSharp.Render
{
    /// <summary>
    /// Item 8, final architecture: with Transition now an abstract class
    /// hierarchy (FadeTransition/FadeToColorTransition/SlideTransition),
    /// dispatch is a direct pattern-match switch on the model type — same
    /// shape as ClipEffectsSk.ApplyStage's switch on BlurEffect/
    /// DropShadowEffect/RoundedCornersEffect. The earlier ISkTransition
    /// strategy-interface layer is REMOVED, not kept alongside this: once
    /// the model itself discriminates by real type, a parallel interface
    /// doing the same discrimination was redundant, and this stays
    /// consistent with how every other polymorphic model type in this
    /// codebase (Effect, and Clip's own subtypes) is already handled here.
    ///
    /// Extensibility for a future procedural/shader-backed transition isn't
    /// lost by dropping the interface — a new switch case can call into a
    /// SKRuntimeEffect-backed static method exactly as easily as a plain
    /// canvas-ops one; ClipEffectsSk already demonstrates this same pattern
    /// (Blur/DropShadow use SKImageFilter, RoundedCorners uses a clip path,
    /// same switch, no interface needed to keep them uniform).
    /// </summary>
    internal static class SkTransitionCompositor
    {
        /// <summary>
        /// Composites `outgoing` and `incoming` at the given progress into a
        /// fresh canvas-sized image — the direct replacement for
        /// FrameFilterChain.ComposeChannel's two-clip case. `transition ==
        /// null` falls back to a plain crossfade, matching the old code's
        /// own "fade" default when no transition is set.
        /// </summary>
        public static SKImage Compose(
            SKImage outgoing, SKImage incoming, Transition? transition,
            double progress, int canvasWidth, int canvasHeight)
        {
            float p = (float)Math.Clamp(progress, 0.0, 1.0);

            using SKSurface surface = SKSurface.Create(new SKImageInfo(
                canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            switch (transition)
            {
                case null:
                case FadeTransition:
                    DrawFade(canvas, outgoing, incoming, p);
                    break;

                case FadeToColorTransition fadeToColor:
                    DrawFadeToColor(canvas, outgoing, incoming, p, canvasWidth, canvasHeight, fadeToColor.Color);
                    break;

                case SlideTransition slide:
                    DrawSlide(canvas, outgoing, incoming, p, canvasWidth, canvasHeight, slide.Angle);
                    break;

                default:
                    throw new NotSupportedException(
                        $"Transition type {transition.GetType().Name} has no Skia " +
                        "implementation. The ~44 old ffmpeg xfade names (WipeLeft, " +
                        "CircleOpen, Dissolve, etc.) have no Transition subclass at " +
                        "all yet — see Transition.cs and the migration manifest.");
            }

            return surface.Snapshot();
        }

        /// <summary>Direct alpha crossfade — outgoing fades out as incoming fades in.</summary>
        private static void DrawFade(SKCanvas canvas, SKImage outgoing, SKImage incoming, float progress)
        {
            DrawWithAlpha(canvas, outgoing, 1f - progress);
            DrawWithAlpha(canvas, incoming, progress);
        }

        /// <summary>
        /// Fades OUT to `colour` over [0, 0.5], then fades IN from it over
        /// [0.5, 1].
        /// </summary>
        private static void DrawFadeToColor(
            SKCanvas canvas, SKImage outgoing, SKImage incoming,
            float progress, int canvasWidth, int canvasHeight, SKColor colour)
        {
            using var fill = new SKPaint { Color = colour };
            canvas.DrawRect(new SKRect(0, 0, canvasWidth, canvasHeight), fill);

            if (progress < 0.5f)
            {
                float t = progress / 0.5f;
                DrawWithAlpha(canvas, outgoing, 1f - t);
            }
            else
            {
                float t = (progress - 0.5f) / 0.5f;
                DrawWithAlpha(canvas, incoming, t);
            }
        }

        /// <summary>
        /// Both images translate together across the canvas at `angleDegrees`
        /// — outgoing exits in that direction while incoming enters from
        /// directly opposite, at the same rate. Angle convention matches
        /// Position/DropShadowEffect.Offset elsewhere: Y-up, 0 = motion
        /// toward +X (right), 90 = motion toward the top of the screen. X
        /// and Y travel distance are each normalized against their own
        /// canvas dimension independently (same convention Position/Offset
        /// already use), not a true Euclidean distance — matches how every
        /// other normalized value in this codebase already behaves.
        ///
        /// Algebraically verified to reduce exactly to the old SlideLeft/
        /// Right/Up/Down pixel math at 0/180/90/270 degrees.
        /// </summary>
        private static void DrawSlide(
            SKCanvas canvas, SKImage outgoing, SKImage incoming,
            float progress, int canvasWidth, int canvasHeight, float angleDegrees)
        {
            double radians = angleDegrees * Math.PI / 180.0;

            // Y-up math convention -> screen (Y-down) draw convention.
            float dx = (float)Math.Cos(radians);
            float dy = (float)-Math.Sin(radians);

            float travelX = dx * canvasWidth;
            float travelY = dy * canvasHeight;

            canvas.DrawImage(outgoing, progress * travelX, progress * travelY);
            canvas.DrawImage(incoming, (progress - 1f) * travelX, (progress - 1f) * travelY);
        }

        private static void DrawWithAlpha(SKCanvas canvas, SKImage image, float alpha)
        {
            if (alpha <= 0f) return;

            byte a = (byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255f);
            using var paint = new SKPaint { Color = new SKColor(255, 255, 255, a) };
            canvas.DrawImage(image, 0, 0, paint);
        }
    }
}
