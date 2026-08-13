using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Render
{
    /// <summary>
    /// Turns a Clip's ClipTransform (and its keyframe animation, if any) into the
    /// eight destination corner values ffmpeg's `perspective` filter needs.
    ///
    /// WHY THERE IS NO HOMOGRAPHY HERE
    /// `perspective` warps the whole FRAME's corners, not an inner rectangle. The
    /// obvious approach — pad the content into the canvas, then map the content
    /// rect to its target quad — needs a homography solved from four point
    /// correspondences, and that solve cannot be written as an ffmpeg expression.
    /// It would force every animated frame's corners to be baked out ahead of
    /// time.
    ///
    /// Instead the content is scaled to exactly canvas size before the filter
    /// runs, so the frame's corners ARE the content's corners and the destination
    /// corners can be computed directly. The deliberate stretch is undone by the
    /// warp itself, because the target quad is built from the content's true
    /// aspect ratio. Everything stays closed-form, so animation works natively
    /// through `eval=frame` rather than needing per-frame baking.
    ///
    /// PROJECTION CONVENTIONS
    ///   - Position moves the clip's CENTRE, normalized to half the canvas:
    ///     (-1, 1) puts the centre on the top-left corner, so three quarters of
    ///     the clip sits off-screen. Y is up.
    ///   - Scale multiplies the base fit — the largest uniform size the content
    ///     can be without cropping — so Scale (1,1) on a 4:3 source over a 16:9
    ///     canvas is pillarboxed rather than stretched.
    ///   - Positive Yaw swings the clip's right edge away from the viewer;
    ///     positive Pitch tips its top edge away.
    ///   - Applied Yaw, then Pitch, then Rotation. Rotation is a plain in-plane
    ///     roll applied last, so it always reads as a straightforward on-screen
    ///     spin no matter what the out-of-plane angles are doing.
    ///   - 90 degree horizontal field of view, so the camera sits half a canvas
    ///     width back from the image plane.
    /// </summary>
    internal static class TransformExpressions
    {
        /// <summary>
        /// Field of view, measured across the canvas WIDTH. Camera distance falls
        /// out of this as (canvasWidth / 2) / tan(fov / 2), which at 90 degrees is
        /// simply half the canvas width. Measuring horizontally rather than
        /// vertically keeps foreshortening from getting overblown on a wide canvas.
        /// </summary>
        public const double FieldOfViewDegrees = 90.0;

        /// <summary>
        /// A corner rotated far enough out of plane can land on or behind the
        /// camera, where the projection divides by zero and the quad turns inside
        /// out. Real 3D pipelines clip against a near plane; this clamps the
        /// corner's depth to just short of the camera instead, which keeps extreme
        /// angles ugly rather than catastrophic. Only reachable when the content
        /// is nearly canvas-width AND turned close to 90 degrees.
        /// </summary>
        private const double NearPlaneFraction = 0.95;

        /// <summary>
        /// Width of the fully transparent border left around the content before
        /// the warp.
        ///
        /// `perspective` does NOT make the area outside its quad transparent on
        /// its own — with no border it clamps to the edge pixels, so the entire
        /// frame comes out opaque and the quad is invisible. Verified directly:
        /// a borderless frame renders edge-to-edge opaque, while the same warp
        /// with a border masks correctly. The border gives the filter genuinely
        /// transparent pixels to sample when it reaches outside the source, and
        /// its interpolation across that alpha step is also what softens the
        /// warped edge.
        ///
        /// The content is scaled to the canvas MINUS this border on each side, so
        /// a wider border costs a little resolution (8px of 1920 is under half a
        /// percent). Two pixels would be enough for bilinear sampling; eight
        /// leaves margin.
        /// </summary>
        public const int TransparentBorderPixels = 8;

        /// <summary>
        /// The size the content must be scaled to before being padded out to the
        /// full canvas. The pad supplies the transparent border the warp needs to
        /// mask against (see TransparentBorderPixels).
        /// </summary>
        /// <summary>
        /// Where the content sits inside the canvas-sized frame, and how big it is
        /// there.
        /// </summary>
        public readonly record struct ContentPlacement(int Width, int Height, int X, int Y);

        /// <summary>
        /// Sizes the content to roughly the number of pixels it will actually
        /// OCCUPY ON SCREEN, rather than blowing it up to fill the frame.
        ///
        /// This matters for sharpness, not layout. `perspective` samples with a
        /// two-tap bilinear, which cannot antialias minification — so any shrinking
        /// it performs aliases the picture. `scale` uses a proper wide kernel and
        /// does not. Filling the frame and letting the warp shrink the result puts
        /// the reduction in the wrong filter; sizing the content up front puts it
        /// in `scale` and leaves the warp at roughly 1:1. Measured against a
        /// lanczos reference at 0.4x: 1.08 mean error filling the frame, 0.12
        /// sizing it here.
        ///
        /// The size is taken at the clip's LARGEST scale, so a zoom stays sharp at
        /// its biggest rather than being sized for the opening frame — smaller
        /// moments then minify through the warp, which is the unavoidable
        /// direction to err in.
        ///
        /// Capped so a transparent border always survives: `perspective` clamps to
        /// edge pixels when it samples outside the source, so a frame with no
        /// transparent margin warps to a fully opaque rectangle and the quad never
        /// appears at all.
        /// </summary>
        public static ContentPlacement ComputeContentPlacement(
            Clip clip, int nativeWidth, int nativeHeight, int canvasWidth, int canvasHeight)
        {
            var (width, height) = ComputeContentSize(
                clip, nativeWidth, nativeHeight, canvasWidth, canvasHeight);

            return PlaceInFrame(width, height, canvasWidth, canvasHeight);
        }

        /// <summary>
        /// The content's pixel size on its own, with no opinion about what frame it
        /// ends up in.
        ///
        /// Still measured against the CANVAS, not the work frame: "how big is this
        /// clip on screen" is a canvas-relative question, and answering it against
        /// a smaller work frame would resize the content every time the work frame
        /// moved. The work frame only decides where this sits, never how big it is.
        /// </summary>
        public static (int Width, int Height) ComputeContentSize(
            Clip clip, int nativeWidth, int nativeHeight, int canvasWidth, int canvasHeight)
        {
            var (baseW, baseH) = BaseFitSize(nativeWidth, nativeHeight, canvasWidth, canvasHeight);
            var (maxScaleX, maxScaleY) = MaxScale(clip);

            double desiredW = baseW * maxScaleX;
            double desiredH = baseH * maxScaleY;

            int limitW = canvasWidth - (2 * TransparentBorderPixels);
            int limitH = canvasHeight - (2 * TransparentBorderPixels);

            //uniform cap so the aspect the transform asked for is preserved
            double fit = Math.Min(1.0, Math.Min(limitW / desiredW, limitH / desiredH));

            return (EvenAtLeast2((int)Math.Round(desiredW * fit)),
                    EvenAtLeast2((int)Math.Round(desiredH * fit)));
        }

        /// <summary>
        /// Centres the content inside a frame of the given size.
        ///
        /// Centring is not cosmetic — it is what makes the work frame legal at all.
        /// The frame's model-space rectangle is the content's scaled up by the
        /// border ratio (see OutsetToFrame), and that outset is only symmetric if
        /// the content sits dead centre. Off-centre content would need a different
        /// homography, not just a different ratio.
        /// </summary>
        public static ContentPlacement PlaceInFrame(
            int contentWidth, int contentHeight, int frameWidth, int frameHeight) =>
            new(contentWidth, contentHeight,
                (frameWidth - contentWidth) / 2, (frameHeight - contentHeight) / 2);

        /// <summary>
        /// The largest scale the clip ever reaches on each axis.
        ///
        /// Clip.Transform is read ONLY when there are no keyframes. That is its
        /// documented contract and exactly what Clip.TransformAt does — once
        /// keyframes exist they define the clip's state entirely. Taking the max
        /// across both meant a keyframed clip left at the default Transform (scale
        /// 1,1) sized its content raster to the full canvas no matter how small it
        /// ever actually rendered. That was merely wasteful before; it also pins
        /// the work frame to full canvas, which cancels the bounding-box
        /// optimization outright for every animated clip. Measured: animated cases
        /// went from 1.00x to 2.4-3.1x once this stopped reading the base
        /// transform.
        /// </summary>
        public static (double X, double Y) MaxScale(Clip clip)
        {
            double x, y;

            if (clip.Keyframes.Count == 0)
            {
                x = Math.Abs(clip.Transform.Scale.X);
                y = Math.Abs(clip.Transform.Scale.Y);
            }
            else
            {
                x = 0;
                y = 0;

                foreach (Keyframe keyframe in clip.Keyframes)
                {
                    x = Math.Max(x, Math.Abs(keyframe.Transform.Scale.X));
                    y = Math.Max(y, Math.Abs(keyframe.Transform.Scale.Y));
                }
            }

            //a clip scaled to nothing still needs a frame to live in
            return (Math.Max(x, 0.001), Math.Max(y, 0.001));
        }

        /// <summary>
        /// A clip's working frame: the size of the buffer its chain actually runs
        /// in, and where that buffer sits on the canvas.
        /// </summary>
        public readonly record struct WorkRect(int X, int Y, int Width, int Height)
        {
            /// <summary>The whole canvas — the behaviour before this existed.</summary>
            public static WorkRect FullCanvas(int canvasWidth, int canvasHeight) =>
                new(0, 0, canvasWidth, canvasHeight);

            public bool IsFullCanvas(int canvasWidth, int canvasHeight) =>
                X == 0 && Y == 0 && Width == canvasWidth && Height == canvasHeight;

            /// <summary>The smallest rect covering both — for a transition segment.</summary>
            public WorkRect Union(WorkRect other)
            {
                int x = Math.Min(X, other.X);
                int y = Math.Min(Y, other.Y);

                return new WorkRect(
                    x, y,
                    Math.Max(X + Width, other.X + other.Width) - x,
                    Math.Max(Y + Height, other.Y + other.Height) - y);
            }
        }

        /// <summary>
        /// How far outside the exact projected quad the chain still writes pixels.
        ///
        /// The warped edge is antialiased by interpolating ACROSS the alpha step,
        /// so `perspective` puts partial coverage a pixel or so beyond the quad
        /// itself, and the area-resolve of the supersampled mask reaches a little
        /// further again. Measured directly: a box sized to the quad alone lost
        /// exactly one column of edge pixels on a clip whose edge fell on the box
        /// boundary, and every inset clip was unaffected. 2px covers both.
        /// </summary>
        public const int EdgeSampleMargin = 2;

        /// <summary>
        /// The work frame for one clip: the union, across the clip's whole
        /// duration, of where its content actually lands on the canvas.
        ///
        /// SAMPLED PER FRAME, not per keyframe. The screen-space extent between two
        /// keyframes is not monotonic — a clip rotating from 0 to 90 degrees is at
        /// its widest at 45, which is not a keyframe — so a keyframe-only union
        /// comes out too small and clips content mid-animation. Per-frame sampling
        /// is a few thousand corner projections for a long clip, which is nothing
        /// next to rendering it, and it removes an entire class of "looked right on
        /// paper" failure.
        ///
        /// extraMargin is for post-transform effects that draw OUTSIDE the content
        /// — a drop shadow's blur and offset, a blur's spread. Those are screen-
        /// space quantities so they add directly in canvas pixels.
        ///
        /// The result is clipped to the canvas (nothing outside it is visible), and
        /// then grown if necessary so the frame can still hold the content plus its
        /// transparent border: `perspective`'s input and output are the same
        /// buffer, so it has to satisfy both at once.
        /// </summary>
        public static WorkRect ComputeWorkRect(
            Clip clip, int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight, int fps, double durationSeconds,
            int extraMargin = 0)
        {
            var (contentW, contentH) = ComputeContentSize(
                clip, nativeWidth, nativeHeight, canvasWidth, canvasHeight);

            var (fitW, fitH) = BaseFitSize(nativeWidth, nativeHeight, canvasWidth, canvasHeight);

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            int frames = Math.Max(1, (int)Math.Ceiling(durationSeconds * fps));

            for (int i = 0; i <= frames; i++)
            {
                double t = Math.Min(i / (double)fps, durationSeconds);
                ClipTransform transform = clip.TransformAt(TimeSpan.FromSeconds(t));

                foreach ((int sx, int sy) in Corners)
                {
                    var (x, y) = ProjectCorner(
                        sx, sy, transform, fitW, fitH, canvasWidth, canvasHeight);

                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            int margin = extraMargin + EdgeSampleMargin;

            int x0 = Math.Max(0, (int)Math.Floor(minX) - margin);
            int y0 = Math.Max(0, (int)Math.Floor(minY) - margin);
            int x1 = Math.Min(canvasWidth, (int)Math.Ceiling(maxX) + margin);
            int y1 = Math.Min(canvasHeight, (int)Math.Ceiling(maxY) + margin);

            //a clip animated entirely off-canvas still has to produce a legal
            //buffer; the enable window is what stops it being seen, not this
            if (x1 <= x0) { x0 = 0; x1 = 2; }
            if (y1 <= y0) { y0 = 0; y1 = 2; }

            //offsets kept even purely as a precaution around the supersampled
            //mask's neighbour-upscale/area-downscale pair. Tested at odd offsets
            //and the output was identical, so this is belt-and-braces rather than
            //load-bearing — it costs at most one pixel of frame.
            x0 -= x0 % 2;
            y0 -= y0 % 2;

            int width = Math.Max(x1 - x0, contentW + (2 * TransparentBorderPixels));
            int height = Math.Max(y1 - y0, contentH + (2 * TransparentBorderPixels));

            width += width % 2;
            height += height % 2;

            //ComputeContentSize caps the content to canvas minus two borders, so
            //the minimum above can never exceed the canvas
            width = Math.Min(width, canvasWidth);
            height = Math.Min(height, canvasHeight);

            if (x0 + width > canvasWidth) x0 = Math.Max(0, canvasWidth - width);
            if (y0 + height > canvasHeight) y0 = Math.Max(0, canvasHeight - height);

            return new WorkRect(x0, y0, width, height);
        }

        private static readonly (int X, int Y)[] Corners =
            [(-1, +1), (+1, +1), (-1, -1), (+1, -1)];

        private static int EvenAtLeast2(int value)
        {
            value = Math.Max(2, value);
            return value % 2 == 0 ? value : value + 1;
        }

        /// <summary>
        /// The corners `perspective` is given are the FRAME's corners, and the
        /// frame is larger than the content by the transparent border. The border
        /// is part of the same flat plane as the content, so scaling the model-
        /// space rectangle up by that ratio BEFORE projecting is exact — the
        /// border foreshortens along with everything else.
        ///
        /// The ratio is against the FRAME, which is the work rect rather than the
        /// canvas now. A smaller frame around the same content simply means less
        /// border, and the projected content quad lands in exactly the same place —
        /// verified pixel-for-pixel across fourteen transforms.
        /// </summary>
        private static (double Width, double Height) OutsetToFrame(
            double baseWidth, double baseHeight,
            int frameWidth, int frameHeight, ContentPlacement placement)
        {
            return (baseWidth * frameWidth / placement.Width,
                    baseHeight * frameHeight / placement.Height);
        }

        /// <summary>
        /// The four corners, in the order `perspective` expects them:
        /// x0/y0 top-left, x1/y1 top-right, x2/y2 bottom-left, x3/y3 bottom-right.
        /// </summary>
        public readonly record struct Quad(
            double X0, double Y0,
            double X1, double Y1,
            double X2, double Y2,
            double X3, double Y3);

        /// <summary>
        /// The content's size at Scale (1,1) — its native size scaled uniformly
        /// until it just touches the canvas on whichever axis is the tighter fit.
        /// Scale then multiplies this, per axis.
        /// </summary>
        public static (double Width, double Height) BaseFitSize(
            int nativeWidth, int nativeHeight, int canvasWidth, int canvasHeight)
        {
            double fit = Math.Min(
                (double)canvasWidth / nativeWidth,
                (double)canvasHeight / nativeHeight);

            return (nativeWidth * fit, nativeHeight * fit);
        }

        /// <summary>
        /// Projects one corner of the clip through yaw, pitch, perspective divide,
        /// roll, and finally position, landing in canvas pixel coordinates.
        ///
        /// signX/signY select which corner: (-1, +1) is top-left in the model's
        /// Y-up space, (+1, -1) is bottom-right, and so on.
        /// </summary>
        public static (double X, double Y) ProjectCorner(
            double signX, double signY,
            ClipTransform transform,
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

            // Yaw about the vertical axis. The clip starts flat at z = 0, so the
            // depth this introduces is entirely a function of the corner's x.
            double x1 = x0 * Math.Cos(yaw);
            double z1 = -x0 * Math.Sin(yaw);

            // Pitch about the horizontal axis, carrying the depth yaw just added.
            double y2 = (y0 * Math.Cos(pitch)) + (z1 * Math.Sin(pitch));
            double z2 = (-y0 * Math.Sin(pitch)) + (z1 * Math.Cos(pitch));

            double cameraDistance = CameraDistance(canvasWidth);
            double clampedZ = Math.Min(z2, cameraDistance * NearPlaneFraction);
            double k = cameraDistance / (cameraDistance - clampedZ);

            double xp = x1 * k;
            double yp = y2 * k;

            // Roll, positive being clockwise on screen (matching how Rotation
            // behaved in the previous overlay pipeline). In Y-up maths that is a
            // negative-angle rotation, hence the sign arrangement here.
            double xr = (xp * Math.Cos(roll)) + (yp * Math.Sin(roll));
            double yr = (-xp * Math.Sin(roll)) + (yp * Math.Cos(roll));

            double xf = xr + (transform.Position.X * canvasWidth / 2.0);
            double yf = yr + (transform.Position.Y * canvasHeight / 2.0);

            // Into pixel space, flipping Y because the canvas counts downward.
            return ((canvasWidth / 2.0) + xf, (canvasHeight / 2.0) - yf);
        }

        /// <summary>
        /// All four corners of a static (un-animated) transform, in the WORK
        /// FRAME's coordinates.
        ///
        /// The projection itself always happens in canvas space — camera distance
        /// and Position are both canvas-relative, and must stay that way or the
        /// clip would move when its work frame did. The work frame enters only as
        /// the outset ratio and as a final translation of the finished corners.
        /// </summary>
        public static Quad ComputeQuad(
            ClipTransform transform,
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

        /// <summary>
        /// The complete `perspective` filter arguments for a clip. When the clip
        /// has no keyframes this is eight literal numbers and eval stays at init;
        /// with keyframes each value becomes an expression in `on` and eval moves
        /// to frame.
        ///
        /// `on` (the filter's output frame counter) is used rather than `t`
        /// because `perspective` does not expose `t` at all — verified against the
        /// filter directly. Since the output frame rate is fixed at build time,
        /// on/fps is exactly the clip-relative time in seconds, and each clip is
        /// its own stream so the counter starts at zero where the clip does.
        ///
        /// Passing the full canvas as the frame with a zero offset reproduces the
        /// pre-bounding-box output exactly, which is what the full-canvas fallback
        /// paths rely on.
        /// </summary>
        public static string BuildPerspectiveArgs(
            Clip clip,
            int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight,
            int frameWidth, int frameHeight,
            int offsetX, int offsetY,
            int fps,
            int coordinateScale = 1)
        {
            var (contentWidth, contentHeight) = ComputeContentSize(
                clip, nativeWidth, nativeHeight, canvasWidth, canvasHeight);

            ContentPlacement placement = PlaceInFrame(
                contentWidth, contentHeight, frameWidth, frameHeight);

            var (contentW, contentH) = BaseFitSize(nativeWidth, nativeHeight, canvasWidth, canvasHeight);
            var (baseW, baseH) = OutsetToFrame(contentW, contentH, frameWidth, frameHeight, placement);

            if (clip.Keyframes.Count == 0)
            {
                Quad q = ComputeQuad(
                    clip.Transform, nativeWidth, nativeHeight, canvasWidth, canvasHeight,
                    frameWidth, frameHeight, offsetX, offsetY, placement);
                double k = coordinateScale;

                return $"perspective=" +
                       $"x0={Num(q.X0 * k)}:y0={Num(q.Y0 * k)}:" +
                       $"x1={Num(q.X1 * k)}:y1={Num(q.Y1 * k)}:" +
                       $"x2={Num(q.X2 * k)}:y2={Num(q.Y2 * k)}:" +
                       $"x3={Num(q.X3 * k)}:y3={Num(q.Y3 * k)}:" +
                       $"sense=destination:eval=init";
            }

            var corners = new (double SignX, double SignY)[]
            {
                (-1, +1), // x0/y0 top-left
                (+1, +1), // x1/y1 top-right
                (-1, -1), // x2/y2 bottom-left
                (+1, -1), // x3/y3 bottom-right
            };

            var parts = new List<string>();
            for (int i = 0; i < corners.Length; i++)
            {
                var (sx, sy) = corners[i];
                parts.Add($"x{i}='{CornerExpression(clip, sx, sy, baseW, baseH, canvasWidth, canvasHeight, fps, wantX: true, coordinateScale, offsetX, offsetY)}'");
                parts.Add($"y{i}='{CornerExpression(clip, sx, sy, baseW, baseH, canvasWidth, canvasHeight, fps, wantX: false, coordinateScale, offsetX, offsetY)}'");
            }

            return $"perspective={string.Join(":", parts)}:sense=destination:eval=frame";
        }

        /// <summary>
        /// One corner coordinate as an ffmpeg expression. The animated transform
        /// properties are evaluated into registers first (st/ld), then the same
        /// projection maths as ProjectCorner is written out against them — so the
        /// C# and the filter agree by construction rather than by two independent
        /// implementations happening to match.
        /// </summary>
        private static string CornerExpression(
            Clip clip, double signX, double signY,
            double baseWidth, double baseHeight,
            int canvasWidth, int canvasHeight,
            int fps, bool wantX, int coordinateScale,
            int offsetX, int offsetY)
        {
            // clip-relative seconds; perspective exposes `on`, not `t`
            string time = $"(on/{Num(fps)})";

            string scaleX = Piecewise(clip, time, fps, t => t.Scale.X);
            string scaleY = Piecewise(clip, time, fps, t => t.Scale.Y);
            string rotation = Piecewise(clip, time, fps, t => t.Rotation);
            string pitch = Piecewise(clip, time, fps, t => t.Pitch);
            string yaw = Piecewise(clip, time, fps, t => t.Yaw);
            string posX = Piecewise(clip, time, fps, t => t.Position.X);
            string posY = Piecewise(clip, time, fps, t => t.Position.Y);

            const double toRad = Math.PI / 180.0;
            double cameraDistance = CameraDistance(canvasWidth);
            double nearPlane = cameraDistance * NearPlaneFraction;

            var sb = new StringBuilder();

            // registers: 1 scaleX, 2 scaleY, 3 roll, 4 pitch, 5 yaw, 6 posX, 7 posY
            sb.Append($"st(1,{scaleX});");
            sb.Append($"st(2,{scaleY});");
            sb.Append($"st(3,({rotation})*{Num(toRad)});");
            sb.Append($"st(4,({pitch})*{Num(toRad)});");
            sb.Append($"st(5,({yaw})*{Num(toRad)});");
            sb.Append($"st(6,{posX});");
            sb.Append($"st(7,{posY});");

            // corner in model space, Y-up
            string x0 = $"({Num(signX * baseWidth / 2.0)}*ld(1))";
            string y0 = $"({Num(signY * baseHeight / 2.0)}*ld(2))";

            // yaw about the vertical axis
            string x1 = $"({x0}*cos(ld(5)))";
            string z1 = $"(-{x0}*sin(ld(5)))";

            // pitch about the horizontal axis
            string y2 = $"({y0}*cos(ld(4))+{z1}*sin(ld(4)))";
            string z2 = $"(-{y0}*sin(ld(4))+{z1}*cos(ld(4)))";

            // perspective divide, clamped just short of the camera
            string k = $"({Num(cameraDistance)}/({Num(cameraDistance)}-min({z2},{Num(nearPlane)})))";

            string xp = $"({x1}*{k})";
            string yp = $"({y2}*{k})";

            // roll, then position, then into pixel space
            //the whole corner formula is linear in output pixels, so a supersampled
            //pass just needs every coordinate multiplied by the factor
            string scale = coordinateScale == 1 ? "" : $"{Num(coordinateScale)}*";

            //The projection above is in CANVAS pixels — it has to be, since camera
            //distance and Position are both canvas-relative. The work frame enters
            //here and only here, as a translation. It is subtracted BEFORE the
            //supersample factor multiplies: the factor scales the work frame, so
            //the translation belongs in work-frame units, not canvas ones.
            if (wantX)
            {
                string xr = $"({xp}*cos(ld(3))+{yp}*sin(ld(3)))";
                sb.Append(
                    $"{scale}(({Num(canvasWidth / 2.0)}+{xr}+ld(6)*{Num(canvasWidth / 2.0)})" +
                    $"-{Num(offsetX)})");
            }
            else
            {
                string yr = $"(-{xp}*sin(ld(3))+{yp}*cos(ld(3)))";
                sb.Append(
                    $"{scale}(({Num(canvasHeight / 2.0)}-({yr}+ld(7)*{Num(canvasHeight / 2.0)}))" +
                    $"-{Num(offsetY)})");
            }

            return sb.ToString();
        }

        /// <summary>
        /// A single transform property as a piecewise expression over the clip's
        /// keyframes. Values are held before the first keyframe and after the
        /// last; between any pair, progress runs through the same eased cubic
        /// Clip.Ease uses.
        ///
        /// The easing is deliberately a cubic evaluated directly against
        /// normalized time rather than a CSS-style cubic-bezier: solving a bezier
        /// for its parameter given elapsed time is iterative, and ffmpeg
        /// expressions cannot iterate. Written this way the filter reproduces
        /// Clip.Ease exactly.
        /// </summary>
        private static string Piecewise(
            Clip clip, string time, int fps, Func<ClipTransform, float> select)
        {
            List<Keyframe> ordered = [.. clip.Keyframes.OrderBy(k => k.Start)];

            if (ordered.Count == 1) return Num(select(ordered[0].Transform));

            // built from the last segment backwards so each `if` nests inside the
            // previous one's else branch
            string expression = Num(select(ordered[^1].Transform));

            for (int i = ordered.Count - 2; i >= 0; i--)
            {
                Keyframe from = ordered[i];
                Keyframe to = ordered[i + 1];

                double fromValue = select(from.Transform);
                double toValue = select(to.Transform);
                double fromTime = from.Start.TotalSeconds;
                double span = (to.Start - from.Start).TotalSeconds;

                string segment;
                if (span <= 0 || IsHold(from, to))
                {
                    // a Constant on either side means no motion across the segment
                    segment = Num(fromValue);
                }
                else
                {
                    var (p1, p2) = ControlValues(from, to);

                    string u = $"(({time}-{Num(fromTime)})/{Num(span)})";
                    string eased =
                        $"(3*(1-{u})*(1-{u})*{u}*{Num(p1)}" +
                        $"+3*(1-{u})*{u}*{u}*{Num(p2)}" +
                        $"+{u}*{u}*{u})";

                    segment = $"({Num(fromValue)}+({Num(toValue - fromValue)})*{eased})";
                }

                expression = $"if(lt({time},{Num(to.Start.TotalSeconds)}),{segment},{expression})";
            }

            // hold the first keyframe's value for anything before it
            double firstTime = ordered[0].Start.TotalSeconds;
            if (firstTime > 0)
            {
                expression = $"if(lt({time},{Num(firstTime)}),{Num(select(ordered[0].Transform))},{expression})";
            }

            return expression;
        }

        private static bool IsHold(Keyframe from, Keyframe to) =>
            from.InterpolationOut == Interpolation.Constant ||
            to.InterpolationIn == Interpolation.Constant;

        /// <summary>
        /// The cubic's two interior control values. Placed so that a strength of 0
        /// reproduces linear motion exactly — at p1 = 1/3 and p2 = 2/3 the cubic
        /// collapses to u — and a strength of 1 leaves the curve fully flat at
        /// that end. Linear and Bezier therefore agree at the boundary instead of
        /// stepping.
        /// </summary>
        private static (double P1, double P2) ControlValues(Keyframe from, Keyframe to)
        {
            double p1 = from.InterpolationOut == Interpolation.Bezier
                ? (1.0 - Math.Clamp(from.EaseOutStrength, 0f, 1f)) / 3.0
                : 1.0 / 3.0;

            double p2 = to.InterpolationIn == Interpolation.Bezier
                ? 1.0 - ((1.0 - Math.Clamp(to.EaseInStrength, 0f, 1f)) / 3.0)
                : 2.0 / 3.0;

            return (p1, p2);
        }

        private static double CameraDistance(int canvasWidth) =>
            (canvasWidth / 2.0) / Math.Tan(DegreesToRadians(FieldOfViewDegrees) / 2.0);

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

        private static string Num(double value) =>
            value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
