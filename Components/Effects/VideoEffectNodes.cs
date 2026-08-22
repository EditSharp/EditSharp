using System;
using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;
using EditSharp.Components.Clips;
using EditSharp.Components; // ChannelBlendMode, Source, FontFace, Animatable
 
namespace EditSharp.Components.Effects
{
    /// <summary>Feeds the renderer. Mandatory, not removable — the one fixed anchor left in an Image-domain graph.</summary>
    public sealed class ImageOutputNode : EffectNode
    {
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Input)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new ImageOutputNode();
    }
 
    // -----------------------------------------------------------------
    // Input nodes — "clips are graphs" rewrite. These replace the old
    // fixed ImageSourceNode anchor AND the old TextClip/GeneratorClip/
    // NoiseClip/TimelineVideoClip Clip subtypes: each is now just an
    // ordinary InputNode inside a VideoClip's single EffectGraph, rather
    // than a distinct Clip subtype of its own. See InputNode's own
    // remarks and VideoClip's static factory methods.
    // -----------------------------------------------------------------
 
    /// <summary>
    /// A real media file, or a still image held for the clip's duration
    /// (an image is a Source with SourceType.Image, same convention as
    /// before this rewrite — no separate "ImageClip" node type). Replaces
    /// the old VideoClip.Source property directly.
    /// </summary>
    public sealed class MediaSourceNode : InputNode, ITrimmableInput
    {
        public required Source Source { get; set; }
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new MediaSourceNode { Enabled = Enabled, Source = Source.Duplicate() };
 
        public TimeSpan InPoint
        {
            get => Source.Start ?? TimeSpan.Zero;
            set => Source.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Source.Start ?? TimeSpan.Zero;
    }
 
    /// <summary>Replaces the old TextClip. No in-point concept — text has nothing to trim into.</summary>
    public sealed class TextInputNode : InputNode
    {
        public required string Content { get; set; }
        public FontFace FontFace { get; set; } = FontFace.ComicSansMs;
        public SKFontStyle FontStyle { get; set; } = SKFontStyle.Normal;
        public SKTextAlign Align { get; set; } = SKTextAlign.Center;
        public int WordsPerLine { get; set; } = int.MaxValue;
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new TextInputNode
        {
            Enabled = Enabled,
            Content = Content,
            FontFace = FontFace,
            FontStyle = FontStyle,
            Align = Align,
            WordsPerLine = WordsPerLine,
        };
    }
 
    /// <summary>
    /// Replaces the old GeneratorClip. The old ColorIn/ColorOut/ColorMain
    /// discrete fade-tuple fields are gone in favor of an ordinary
    /// keyframeable Color — the same simplification this schema already
    /// applied elsewhere (flat Volume -> GainNode, flat ClipTransform ->
    /// per-field Animatable): a fade-in/hold/fade-out is just three
    /// keyframes on one Animatable&lt;SKColor&gt; now, rather than a
    /// bespoke shape only this one node type understood.
    /// </summary>
    public sealed class ColorGeneratorInputNode : InputNode
    {
        public Animatable<SKColor> Color { get; set; } = new(SKColors.Black);
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new ColorGeneratorInputNode { Enabled = Enabled, Color = Color.Duplicate() };
    }
 
    /// <summary>Replaces the old NoiseClip. No in-point concept — procedural, generates for however long it's asked.</summary>
    public sealed class NoiseInputNode : InputNode
    {
        public int Seed { get; set; } = Random.Shared.Next();
        public float Detail { get; set; } = 0.03f;
        public float SeetheRate { get; set; } = 0.03f;
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new NoiseInputNode
        {
            Enabled = Enabled,
            Seed = Seed,
            Detail = Detail,
            SeetheRate = SeetheRate,
        };
    }
 
    /// <summary>
    /// Embeds another Timeline's fully composited picture. Replaces the old
    /// TimelineVideoClip Clip subtype — TimelineReference itself is
    /// unchanged, it just lives on a node now instead of directly on a
    /// dedicated Clip subtype.
    /// </summary>
    public sealed class TimelineVideoInputNode : InputNode, ITrimmableInput
    {
        public required TimelineReference Reference { get; set; }
 
        private static readonly NodePort[] StaticPorts = [new("Image", PortType.Image, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new TimelineVideoInputNode { Enabled = Enabled, Reference = Reference.Duplicate() };
 
        public TimeSpan InPoint
        {
            get => Reference.Start ?? TimeSpan.Zero;
            set => Reference.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Reference.Start ?? TimeSpan.Zero;
    }
 
    // -----------------------------------------------------------------
    // Default-wired nodes (TintNode, TransformNode) and the rest of the
    // effect-node catalogue — unchanged in kind from before this rewrite,
    // except TintNode is new (folds in the old flat Modulate property)
    // and TransformNode now carries its own data instead of being a
    // data-less marker.
    // -----------------------------------------------------------------
 
    /// <summary>
    /// Color tint, doubling as transparency via alpha. Auto-created and
    /// wired in by default (see EffectGraph.CreateVideoGraph) alongside
    /// TransformNode — this is what replaced the old flat
    /// VisualClip.Modulate property entirely, fully consistent with how
    /// GainNode already replaced the old flat AudioClip.Volume: a normal,
    /// removable, reorderable node beyond the default, not a fixed clip
    /// property any more.
    /// </summary>
    public sealed class TintNode : EffectNode
    {
        public Animatable<SKColor> Color { get; set; } = new(SKColors.White);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new TintNode { Enabled = Enabled, Color = Color.Duplicate() };
    }
 
    /// <summary>
    /// Wraps a clip's transform. Auto-created and wired in by default when
    /// a VideoClip's graph is first constructed, but is otherwise a normal,
    /// removable, rewireable node — this is what replaces the old
    /// EffectStage.PreTransform/PostTransform enum entirely (see the schema
    /// doc): "stage" is purely a function of a node's position relative to
    /// TransformNode in the graph, not a fixed property of an effect type.
    ///
    /// REWRITE: this node now carries its OWN ClipTransform data (Position/
    /// Scale/Rotation/Pitch/Yaw), rather than being a data-less marker
    /// whose data lived on the owning VisualClip — there is no more
    /// VisualClip to hold it now that VideoClip is the only concrete
    /// visual Clip type and a clip IS its graph. A graph is free to have
    /// more than one TransformNode (e.g. one per branch before a MergeNode)
    /// now that Transform data travels with the node instead of the clip.
    /// </summary>
    public sealed class TransformNode : EffectNode
    {
        public ClipTransform Transform { get; set; } = new();
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new TransformNode { Enabled = Enabled, Transform = Transform.Duplicate() };
    }
 
    public sealed class BlurNode : EffectNode
    {
        public Animatable<float> Radius { get; set; } = new(0f);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Input, optional: true),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new BlurNode { Enabled = Enabled, Radius = Radius.Duplicate() };
    }
 
    public sealed class DropShadowNode : EffectNode
    {
        public Animatable<Vector2> Offset { get; set; } = new(default);
        public Animatable<float> Blur { get; set; } = new(0f);
        public Animatable<SKColor> Color { get; set; } = new(SKColors.Black);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Input, optional: true),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new DropShadowNode
        {
            Enabled = Enabled,
            Offset = Offset.Duplicate(),
            Blur = Blur.Duplicate(),
            Color = Color.Duplicate(),
        };
    }
 
    public sealed class RoundedCornersNode : EffectNode
    {
        public Animatable<float> Radius { get; set; } = new(0f);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Input, optional: true),
            new("Image", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new RoundedCornersNode { Enabled = Enabled, Radius = Radius.Duplicate() };
    }
 
    public enum ShapeType { Rectangle, Ellipse, Polygon }
    public enum MaskChannelSource { Luma, Alpha }
    public enum MaskCombineMode { Add, Subtract, Intersect }
 
    /// <summary>
    /// Geometry is normalized 0-1 — a fraction of whatever image this mask
    /// ends up merged against — rather than absolute pixels. See the schema
    /// doc's "Masks are canvas-agnostic" note for why this sidesteps the
    /// pre-/post-TransformNode coordinate-space question entirely.
    /// </summary>
    public sealed class ShapeMaskNode : EffectNode
    {
        public ShapeType Shape { get; set; } = ShapeType.Rectangle;
        public Animatable<Vector2> Position { get; set; } = new(default);
        public Animatable<Vector2> Size { get; set; } = new(new Vector2(1, 1));
        public Animatable<float> Rotation { get; set; } = new(0f);
        public Animatable<float> Feather { get; set; } = new(0f);
 
        //only meaningful when Shape == Polygon. Kept as a plain list rather
        //than dedicated add/remove/reorder endpoints — flagged as an open
        //question in the schema doc, not settled here
        public List<Animatable<Vector2>> PolygonPoints { get; set; } = [];
 
        private static readonly NodePort[] StaticPorts = [new("Mask", PortType.Mask, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new ShapeMaskNode
        {
            Enabled = Enabled,
            Shape = Shape,
            Position = Position.Duplicate(),
            Size = Size.Duplicate(),
            Rotation = Rotation.Duplicate(),
            Feather = Feather.Duplicate(),
            PolygonPoints = [.. PolygonPoints.ConvertAll(p => p.Duplicate())],
        };
    }
 
    public sealed class ImageToMaskNode : EffectNode
    {
        public MaskChannelSource Channel { get; set; } = MaskChannelSource.Alpha;
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Image", PortType.Image, PortDirection.Input),
            new("Mask", PortType.Mask, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new ImageToMaskNode { Enabled = Enabled, Channel = Channel };
    }
 
    public sealed class MaskCombineNode : EffectNode
    {
        public MaskCombineMode Mode { get; set; } = MaskCombineMode.Add;
 
        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Mask, PortDirection.Input),
            new("B", PortType.Mask, PortDirection.Input),
            new("Result", PortType.Mask, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new MaskCombineNode { Enabled = Enabled, Mode = Mode };
    }
 
    /// <summary>
    /// Branch/recombine — the canonical node-graph pattern (split into two
    /// branches, effect each differently, recombine) that justifies a graph
    /// over a flat stack in the first place. Also the canonical way to
    /// combine TWO InputNodes in one graph now that a graph can have more
    /// than one (e.g. two MediaSourceNodes composited together).
    /// </summary>
    public sealed class MergeNode : EffectNode
    {
        //ChannelBlendMode, not the old ffmpeg-oriented BlendMode enum — see
        //ChannelBlendMode.cs's own remarks. The Skia-native compositor is
        //the live rendering path and MergeNode composites two image streams
        //the exact same way a Channel composites onto the ones beneath it,
        //so the two should speak the same blend-mode vocabulary.
        public ChannelBlendMode BlendMode { get; set; } = ChannelBlendMode.SrcOver;
        public Animatable<float> Mix { get; set; } = new(1f); // 0 = pure A, 1 = pure B
 
        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Image, PortDirection.Input),
            new("B", PortType.Image, PortDirection.Input),
 
            //optional Value modulation input — when connected (typically to
            //a ValueConstantNode or a MathNode chain — see ValueNodes.cs),
            //MULTIPLIES against Mix's own keyframed value rather than
            //replacing it, so an author keeps Mix's own curve and layers a
            //procedurally-computed modulation on top of it
            new("MixModulation", PortType.Value, PortDirection.Input, optional: true),
 
            new("Result", PortType.Image, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new MergeNode
        {
            Enabled = Enabled,
            BlendMode = BlendMode,
            Mix = Mix.Duplicate(),
        };
    }
}
 