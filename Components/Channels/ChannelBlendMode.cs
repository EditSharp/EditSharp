namespace EditSharp.Components.Channels
{
    /// <summary>How a layer (a video channel, or MergeNode's B) combines with what's below it.</summary>
    /// <remarks>The first 29 are Skia's own blend modes, alpha-aware; Average, Negation, Divide and Subtract are separable blends applied where both layers are opaque, each layer alone where the other isn't.</remarks>
    public enum ChannelBlendMode
    {
        /// <summary>Nothing: the result is transparent.</summary>
        Clear,

        /// <summary>The layer replaces what's below.</summary>
        Src,

        /// <summary>Only what's below; the layer is ignored.</summary>
        Dst,

        /// <summary>The layer over what's below: ordinary drawing.</summary>
        SrcOver,

        /// <summary>What's below over the layer.</summary>
        DstOver,

        /// <summary>The layer, only where there's something below.</summary>
        SrcIn,

        /// <summary>What's below, only where the layer is.</summary>
        DstIn,

        /// <summary>The layer, only where there's nothing below.</summary>
        SrcOut,

        /// <summary>What's below, only where the layer isn't.</summary>
        DstOut,

        /// <summary>The layer over what's below, only where there's something below.</summary>
        SrcATop,

        /// <summary>What's below over the layer, only where the layer is.</summary>
        DstATop,

        /// <summary>Each only where the other isn't.</summary>
        Xor,

        /// <summary>The two added together.</summary>
        Plus,

        /// <summary>The two multiplied, alpha included.</summary>
        Modulate,

        /// <summary>Lightens: the inverse of multiplying the inverses.</summary>
        Screen,

        /// <summary>Multiply or Screen, depending on what's below.</summary>
        Overlay,

        /// <summary>The darker of the two.</summary>
        Darken,

        /// <summary>The lighter of the two.</summary>
        Lighten,

        /// <summary>Brightens what's below to reflect the layer.</summary>
        ColorDodge,

        /// <summary>Darkens what's below to reflect the layer.</summary>
        ColorBurn,

        /// <summary>Multiply or Screen, depending on the layer.</summary>
        HardLight,

        /// <summary>A softer HardLight: darkens or lightens depending on the layer.</summary>
        SoftLight,

        /// <summary>The difference between the two.</summary>
        Difference,

        /// <summary>Like Difference, with lower contrast.</summary>
        Exclusion,

        /// <summary>The two multiplied: darkens.</summary>
        Multiply,

        /// <summary>The layer's hue, with the saturation and luminosity below.</summary>
        Hue,

        /// <summary>The layer's saturation, with the hue and luminosity below.</summary>
        Saturation,

        /// <summary>The layer's hue and saturation, with the luminosity below.</summary>
        Color,

        /// <summary>The layer's luminosity, with the hue and saturation below.</summary>
        Luminosity,

        /// <summary>The mean of the two.</summary>
        Average,

        /// <summary>One minus the distance of their sum from one: 1 - |1 - below - layer|.</summary>
        Negation,

        /// <summary>What's below divided by the layer: brightens.</summary>
        Divide,

        /// <summary>The layer taken away from what's below: darkens.</summary>
        Subtract,
    }
}
