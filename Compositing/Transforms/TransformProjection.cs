using System;
using System.Globalization;
using System.Numerics;
using EditSharp.Components.Clips;
using EditSharp.Components;

namespace EditSharp.Compositing.Transforms
{
    /// <summary>Turns a ClipTransform into the four corners its content lands on.</summary>
    /// <remarks>Position moves the content's centre, in half-frames. Scale multiplies the fit to the frame. y is up. Rotations apply yaw, then pitch, then roll, seen with a 90 degree horizontal field of view.</remarks>
    internal static class TransformProjection
    {
        public const double FieldOfViewDegrees = 90.0;
        private const double NearPlaneFraction = 0.95;

        public readonly record struct ContentPlacement(int Width, int Height, int X, int Y);

        //about the pixels the content covers on screen at its largest keyframed scale, so a zoom stays sharp;
        //no larger than `limitWidth` x `limitHeight`, which default to the canvas
        public static (int Width, int Height) ComputeContentSize(
            ClipTransform transform, int nativeWidth, int nativeHeight, int canvasWidth, int canvasHeight,
            int limitWidth = 0, int limitHeight = 0)
        {
            var (baseW, baseH) = BaseFitSize(nativeWidth, nativeHeight, canvasWidth, canvasHeight);
            var (maxScaleX, maxScaleY) = MaxScale(transform);

            double desiredW = baseW * maxScaleX;
            double desiredH = baseH * maxScaleY;

            double limitW = limitWidth > 0 ? limitWidth : canvasWidth;
            double limitH = limitHeight > 0 ? limitHeight : canvasHeight;
            double fit = Math.Min(1.0, Math.Min(limitW / desiredW, limitH / desiredH));

            return (EvenAtLeast2((int)Math.Round(desiredW * fit)),
                    EvenAtLeast2((int)Math.Round(desiredH * fit)));
        }

        //the largest Scale reaches per axis across its keyframes
        public static (double X, double Y) MaxScale(ClipTransform transform)
        {
            Animatable<Vector2> scale = transform.Scale;

            double x, y;

            if (scale.Track == null || scale.Track.Keyframes.Count < 2)
            {
                x = Math.Abs(scale.StaticValue.X);
                y = Math.Abs(scale.StaticValue.Y);
            }
            else
            {
                x = 0;
                y = 0;

                foreach (Keyframe<Vector2> keyframe in scale.Track.Keyframes)
                {
                    x = Math.Max(x, Math.Abs(keyframe.Value.X));
                    y = Math.Max(y, Math.Abs(keyframe.Value.Y));
                }
            }

            //a clip scaled to nothing still needs a frame to live in
            return (Math.Max(x, 0.001), Math.Max(y, 0.001));
        }

        private static int EvenAtLeast2(int value)
        {
            value = Math.Max(2, value);
            return value % 2 == 0 ? value : value + 1;
        }

        private static (double Width, double Height) OutsetToFrame(
            double baseWidth, double baseHeight,
            int frameWidth, int frameHeight, ContentPlacement placement)
        {
            return (baseWidth * frameWidth / placement.Width,
                    baseHeight * frameHeight / placement.Height);
        }

        public readonly record struct Quad(
            double X0, double Y0,
            double X1, double Y1,
            double X2, double Y2,
            double X3, double Y3);

        public static (double Width, double Height) BaseFitSize(
            int nativeWidth, int nativeHeight, int canvasWidth, int canvasHeight)
        {
            double fit = Math.Min(
                (double)canvasWidth / nativeWidth,
                (double)canvasHeight / nativeHeight);

            return (nativeWidth * fit, nativeHeight * fit);
        }

        public static (double X, double Y) ProjectCorner(
            double signX, double signY,
            ResolvedTransform transform,
            double baseWidth, double baseHeight,
            int canvasWidth, int canvasHeight)
        {
            double halfW = baseWidth * transform.Scale.X / 2.0;
            double halfH = baseHeight * transform.Scale.Y / 2.0;

            double x0 = signX * halfW;
            double y0 = signY * halfH;

            double yaw = DegreesToRadians(transform.Yaw);
            double pitch = DegreesToRadians(transform.Pitch);
            double roll = DegreesToRadians(transform.Rotation);

            double x1 = x0 * Math.Cos(yaw);
            double z1 = -x0 * Math.Sin(yaw);

            double y2 = (y0 * Math.Cos(pitch)) + (z1 * Math.Sin(pitch));
            double z2 = (-y0 * Math.Sin(pitch)) + (z1 * Math.Cos(pitch));

            double cameraDistance = CameraDistance(canvasWidth);
            double clampedZ = Math.Min(z2, cameraDistance * NearPlaneFraction);
            double k = cameraDistance / (cameraDistance - clampedZ);

            double xp = x1 * k;
            double yp = y2 * k;

            double xr = (xp * Math.Cos(roll)) + (yp * Math.Sin(roll));
            double yr = (-xp * Math.Sin(roll)) + (yp * Math.Cos(roll));

            double xf = xr + (transform.Position.X * canvasWidth / 2.0);
            double yf = yr + (transform.Position.Y * canvasHeight / 2.0);

            return ((canvasWidth / 2.0) + xf, (canvasHeight / 2.0) - yf);
        }

        public static Quad ComputeQuad(
            ResolvedTransform transform,
            int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight,
            int frameWidth, int frameHeight,
            int offsetX, int offsetY,
            ContentPlacement placement)
        {
            var (contentW, contentH) = BaseFitSize(nativeWidth, nativeHeight, canvasWidth, canvasHeight);
            var (baseW, baseH) = OutsetToFrame(contentW, contentH, frameWidth, frameHeight, placement);

            var tl = ProjectCorner(-1, +1, transform, baseW, baseH, canvasWidth, canvasHeight);
            var tr = ProjectCorner(+1, +1, transform, baseW, baseH, canvasWidth, canvasHeight);
            var bl = ProjectCorner(-1, -1, transform, baseW, baseH, canvasWidth, canvasHeight);
            var br = ProjectCorner(+1, -1, transform, baseW, baseH, canvasWidth, canvasHeight);

            return new Quad(
                tl.X - offsetX, tl.Y - offsetY,
                tr.X - offsetX, tr.Y - offsetY,
                bl.X - offsetX, bl.Y - offsetY,
                br.X - offsetX, br.Y - offsetY);
        }

        private static double CameraDistance(int canvasWidth) =>
            (canvasWidth / 2.0) / Math.Tan(DegreesToRadians(FieldOfViewDegrees) / 2.0);

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

        private static string Num(double value) =>
            value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
