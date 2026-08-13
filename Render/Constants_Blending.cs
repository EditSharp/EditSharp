using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;

namespace EditSharp.Assembly
{
    /// <summary>
    /// Lookup tables shared across the assembly pipeline.
    /// </summary>
    internal static partial class Constants
    {
        /// <summary>
        /// BlendMode to ffmpeg `blend` mode name.
        ///
        /// Normal is absent on purpose: it is plain alpha compositing, which goes
        /// through `overlay` rather than `blend` and never needs a name here.
        /// Every other value maps to a real ffmpeg mode, which is why BlendMode is
        /// its own enum rather than SkiaSharp's — SKBlendMode advertises around
        /// thirty modes, and its non-separable ones and Porter-Duff operators have
        /// no ffmpeg counterpart to map to.
        /// </summary>
        public static readonly Dictionary<BlendMode, string> BlendModeNames = new()
        {
            [BlendMode.Multiply] = "multiply",
            [BlendMode.Screen] = "screen",
            [BlendMode.Overlay] = "overlay",
            [BlendMode.Darken] = "darken",
            [BlendMode.Lighten] = "lighten",
            [BlendMode.ColorDodge] = "dodge",
            [BlendMode.ColorBurn] = "burn",
            [BlendMode.HardLight] = "hardlight",
            [BlendMode.SoftLight] = "softlight",
            [BlendMode.Difference] = "difference",
            [BlendMode.Exclusion] = "exclusion",
            [BlendMode.Addition] = "addition",
            [BlendMode.Subtract] = "subtract",
            [BlendMode.Divide] = "divide",
            [BlendMode.Average] = "average",
            [BlendMode.Negation] = "negation",
            [BlendMode.Xor] = "xor",
        };

        /// <summary>
        /// Transitions that do NOT preserve transparency, measured directly against
        /// ffmpeg by warping two bordered clips and counting opaque pixels that
        /// appeared in the transparent surround.
        ///
        /// Two different causes are mixed together here. FadeBlack, FadeWhite,
        /// CircleCrop and RectCrop pass through an opaque colour BY DEFINITION, so
        /// their behaviour is correct and simply incompatible with a
        /// transparent backdrop. SlideUp, SlideDown, CoverDown, RevealUp and ZoomIn look like genuine
        /// ffmpeg bugs — tellingly they are asymmetric, with SlideLeft/SlideRight,
        /// CoverLeft/Right/Up and RevealLeft/Right/Down all passing cleanly.
        ///
        /// This only matters above the bottom channel. Channel 0 is flattened onto
        /// opaque black anyway, so anything is safe there; higher channels need
        /// their transparency to survive or they punch an opaque rectangle through
        /// whatever is beneath them for the length of the transition.
        ///
        /// Results are version specific. Re-run the audit script against the
        /// project's own ffmpeg build rather than trusting this list indefinitely.
        /// </summary>
        public static readonly HashSet<TransitionType> AlphaUnsafeTransitions =
        [
            TransitionType.FadeBlack,
            TransitionType.FadeWhite,
            TransitionType.CircleCrop,
            TransitionType.RectCrop,
            TransitionType.SlideUp,
            TransitionType.SlideDown,
            TransitionType.CoverDown,
            TransitionType.RevealUp,
            TransitionType.ZoomIn,
        ];

        /// <summary>
        /// Whether a transition keeps transparency intact, and is therefore usable
        /// on a channel that has anything underneath it.
        /// </summary>
        public static bool PreservesAlpha(TransitionType type) =>
            !AlphaUnsafeTransitions.Contains(type);

        /// <summary>Every transition safe to use above the bottom channel.</summary>
        public static IEnumerable<TransitionType> AlphaSafeTransitions =>
            XfadeNames.Keys.Where(PreservesAlpha);
    }
}
