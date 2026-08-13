using System;
using System.Collections.Generic;
using System.Text;

namespace EditSharp.Editing
{
    public class Transition
    {
        //which transition should be used (is an enum so new transitions can be added)
        public TransitionType Type;

        //how long the transition should last
        public TimeSpan Duration { get; set; }
    }

    public enum TransitionType
    {
        Fade,
        FadeBlack,
        FadeWhite,
        FadeGrays,
        FadeFast,
        FadeSlow,
        Dissolve,
        Distance,
        Pixelize,
        WipeLeft,
        WipeRight,
        WipeUp,
        WipeDown,
        WipeTopLeft,
        WipeTopRight,
        WipeBottomLeft,
        WipeBottomRight,
        SlideLeft,
        SlideRight,
        SlideUp,
        SlideDown,
        SmoothLeft,
        SmoothRight,
        SmoothUp,
        SmoothDown,
        CircleOpen,
        CircleClose,
        CircleCrop,
        RectCrop,
        Radial,
        VerticalOpen,
        VerticalClose,
        HorizontalOpen,
        HorizontalClose,
        DiagonalTopLeft,
        DiagonalTopRight,
        DiagonalBottomLeft,
        DiagonalBottomRight,
        SliceLeft,
        SliceRight,
        SliceUp,
        SliceDown,
        WindLeft,
        WindRight,
        WindUp,
        WindDown,
        CoverLeft,
        CoverRight,
        CoverUp,
        CoverDown,
        RevealLeft,
        RevealRight,
        RevealUp,
        RevealDown,
        SqueezeHorizontal,
        SqueezeVertical,
        ZoomIn,
        HorizontalBlur,
    }
}
