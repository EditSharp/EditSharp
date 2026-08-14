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

        private static int EvenAtLeast2(int value)
        {
            value = Math.Max(2, value);
            return value % 2 == 0 ? value : value + 1;
        }

        /// <summary>
        /// The clip's actual footprint on canvas THIS FRAME — the smallest rect
        /// worth rendering into, instead of the full canvas regardless of how
        /// large the clip actually appears on screen.
        ///
        /// Built from the SAME quad math ApplyTransform's perspective warp uses
        /// (ProjectCorner via ComputeQuad): computed once in FULL-CANVAS space to
        /// find where the four corners actually land this frame, then padded by
        /// TransparentBorderPixels and clamped to canvas bounds. The margin
        /// exists for the same reason TransparentBorderPixels does everywhere
        /// else — `perspective` clamps to edge pixels when it samples outside
        /// the source, and bilinear interpolation at a warped edge reaches a
        /// pixel or so beyond the corner itself, so a rect sized to the EXACT
        /// corners with no margin would clip that interpolated edge.
        ///
        /// This is a genuinely PER-FRAME computation, unlike the deleted whole-
        /// window pipeline's version of this idea, which had to cover a clip's
        /// ENTIRE keyframe range in one static rect since it rendered as one
        /// continuous stream. literalTransform here is already resolved to this
        /// exact frame's time (see Clip.TransformAt / FrameStateResolver), so
        /// the rect can be as tight as this one frame actually needs — animated
        /// content should do noticeably better than the deleted version's
        /// measured 33%, which was bounding an entire animation's motion path
        /// rather than one instant of it.
        ///
        /// ComputeContentSize (and therefore content's actual pixel size) stays
        /// based on the clip's MaxScale across its whole keyframe range, NOT
        /// this frame's literal scale — that decision is about how much detail
        /// the content buffer needs (sized for the biggest zoom the clip ever
        /// reaches), which is unrelated to where THIS frame's rect sits on
        /// canvas and would be wrong to tie to a single frame's transform.
        /// </summary>
        public static WorkRect ComputeWorkRect(
            Clip clip, ClipTransform literalTransform,
            int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight)
        {
            var (contentWidth, contentHeight) = ComputeContentSize(
                clip, nativeWidth, nativeHeight, canvasWidth, canvasHeight);

            ContentPlacement fullCanvasPlacement =
                PlaceInFrame(contentWidth, contentHeight, canvasWidth, canvasHeight);

            //frameWidth/frameHeight/offset = full canvas here deliberately —
            //this is the same call ClipVideoChain.Build used to make when
            //WorkRect was hardcoded to FullCanvas, reused purely to find where
            //the corners land before the real (tighter) work rect exists
            Quad quad = ComputeQuad(
                literalTransform, nativeWidth, nativeHeight,
                canvasWidth, canvasHeight,
                canvasWidth, canvasHeight, 0, 0,
                fullCanvasPlacement);

            double minX = Math.Min(Math.Min(quad.X0, quad.X1), Math.Min(quad.X2, quad.X3));
            double maxX = Math.Max(Math.Max(quad.X0, quad.X1), Math.Max(quad.X2, quad.X3));
            double minY = Math.Min(Math.Min(quad.Y0, quad.Y1), Math.Min(quad.Y2, quad.Y3));
            double maxY = Math.Max(Math.Max(quad.Y0, quad.Y1), Math.Max(quad.Y2, quad.Y3));

            int left = Math.Clamp(
                (int)Math.Floor(minX) - TransparentBorderPixels, 0, canvasWidth);
            int top = Math.Clamp(
                (int)Math.Floor(minY) - TransparentBorderPixels, 0, canvasHeight);
            int right = Math.Clamp(
                (int)Math.Ceiling(maxX) + TransparentBorderPixels, 0, canvasWidth);
            int bottom = Math.Clamp(
                (int)Math.Ceiling(maxY) + TransparentBorderPixels, 0, canvasHeight);

            int width = EvenAtLeast2(right - left);
            int height = EvenAtLeast2(bottom - top);

            //never smaller than the content's own fixed render size. Frame()
            //scales content to exactly contentWidth x contentHeight (see
            //ComputeContentSize above) and centres it in the work rect via
            //PlaceInFrame — a work rect tighter than that would ask
            //PlaceInFrame for a NEGATIVE centring offset. This is exactly the
            //case ComputeContentSize's own remarks describe: content is sized
            //for the clip's BIGGEST zoom across its whole keyframe range, but
            //a tight per-frame bbox is sized for THIS frame's actual scale —
            //on an early, zoomed-OUT frame of a clip that zooms in later,
            //the fixed content size can legitimately be larger than what
            //this one frame's bbox alone would need. Expanding around centre
            //keeps the original (smaller) bbox fully contained.
            if (width < contentWidth)
            {
                left -= (contentWidth - width) / 2;
                width = contentWidth;
            }

            if (height < contentHeight)
            {
                top -= (contentHeight - height) / 2;
                height = contentHeight;
            }

            //re-clamp position now that expansion may have pushed the rect
            //outside canvas bounds. ComputeContentSize guarantees
            //contentWidth/contentHeight are each strictly less than the
            //canvas's own dimension (it caps to canvasWidth/Height minus
            //2*TransparentBorderPixels), so this clamp can only tighten
            //`left`/`top`, never shrink width/height back below content size
            left = Math.Clamp(left, 0, Math.Max(0, canvasWidth - width));
            top = Math.Clamp(top, 0, Math.Max(0, canvasHeight - height));
            width = Math.Min(width, canvasWidth - left);
            height = Math.Min(height, canvasHeight - top);

            return new WorkRect(left, top, width, height);
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
        /// The complete `perspective` filter arguments for ONE FRAME, given a
        /// transform already resolved to that frame's exact time (e.g. via
        /// Clip.TransformAt). Always eight literal numbers and eval=init — there
        /// is no animation for `perspective` to evaluate, because there is no
        /// time in this filter graph at all: EditSharp resolved the clip's state
        /// before building the command, rather than handing ffmpeg a keyframe
        /// curve to evaluate via `on`/`t`.
        ///
        /// This is the frame-by-frame render's equivalent of BuildPerspectiveArgs'
        /// own no-keyframes branch — same ComputeQuad math, just called with a
        /// transform pinned to a point in time instead of read off the clip
        /// directly, so it works whether or not the clip itself is keyframed.
        /// </summary>
        public static string BuildLiteralPerspectiveArgs(
            ClipTransform transform,
            int nativeWidth, int nativeHeight,
            int canvasWidth, int canvasHeight,
            int frameWidth, int frameHeight,
            int offsetX, int offsetY,
            ContentPlacement placement,
            int coordinateScale = 1)
        {
            Quad q = ComputeQuad(
                transform, nativeWidth, nativeHeight, canvasWidth, canvasHeight,
                frameWidth, frameHeight, offsetX, offsetY, placement);

            double k = coordinateScale;

            return $"perspective=" +
                   $"x0={Num(q.X0 * k)}:y0={Num(q.Y0 * k)}:" +
                   $"x1={Num(q.X1 * k)}:y1={Num(q.Y1 * k)}:" +
                   $"x2={Num(q.X2 * k)}:y2={Num(q.Y2 * k)}:" +
                   $"x3={Num(q.X3 * k)}:y3={Num(q.Y3 * k)}:" +
                   $"sense=destination:eval=init";
        }


        private static double CameraDistance(int canvasWidth) =>
            (canvasWidth / 2.0) / Math.Tan(DegreesToRadians(FieldOfViewDegrees) / 2.0);

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

        private static string Num(double value) =>
            value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
