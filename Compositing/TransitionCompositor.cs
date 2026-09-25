using System;
using SkiaSharp;
using EditSharp.Components.Transitions;
using EditSharp.Compositing.Gpu;

namespace EditSharp.Compositing
{
    //draws the two clips of a transition in progress, dispatching on the transition's type
    internal static class TransitionCompositor
    {
        //the two clips at `progress` into a new frame-sized image; no transition set means a crossfade
        public static SKImage Compose(
            SKImage outgoing, SKImage incoming, Transition? transition,
            double progress, int canvasWidth, int canvasHeight, SurfacePool pool)
        {
            float p = (float)Math.Clamp(progress, 0.0, 1.0);

            SKSurface surface = pool.Rent(canvasWidth, canvasHeight);
            try
            {
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
                            "implementation.");
                }

                return surface.Snapshot();
            }
            finally
            {
                pool.Return(surface, canvasWidth, canvasHeight);
            }
        }

        //outgoing fades out as incoming fades in
        private static void DrawFade(SKCanvas canvas, SKImage outgoing, SKImage incoming, float progress)
        {
            DrawWithAlpha(canvas, outgoing, 1f - progress);
            DrawWithAlpha(canvas, incoming, progress);
        }

        //fades out to `colour` over the first half, then in from it over the second
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

        //both images move together at `angleDegrees` (y up, 0 is right, 90 is up): outgoing leaves that way,
        //incoming enters from the opposite side. x and y travel are each fractions of their own frame dimension
        private static void DrawSlide(
            SKCanvas canvas, SKImage outgoing, SKImage incoming,
            float progress, int canvasWidth, int canvasHeight, float angleDegrees)
        {
            double radians = angleDegrees * Math.PI / 180.0;

            //y up in the maths, y down on screen
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
