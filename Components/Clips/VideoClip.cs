using System;
using SkiaSharp;
using EditSharp.Components.Effects;
using EditSharp.Components; // Source, FontFace
 
namespace EditSharp.Components.Clips
{
    /// <summary>
    /// The only concrete visual Clip type — see Clip's own remarks on the
    /// "clips are graphs" rewrite. What used to be TextClip/GeneratorClip/
    /// NoiseClip/TimelineVideoClip (each a distinct Clip subtype) and
    /// VideoClip's own flat Source property are now all just different
    /// InputNode types wired into this class's single Image-domain
    /// EffectGraph — see the static factory methods below and
    /// VideoEffectNodes.cs.
    ///
    /// Sealed and otherwise data-less beyond Start/Duration/LinkGroupId
    /// (inherited from Clip) and Graph — everything else a visual clip
    /// used to carry directly (Modulate, Transform) now lives on nodes
    /// INSIDE the graph (TintNode, TransformNode), not on this class.
    /// </summary>
    public sealed class VideoClip : Clip
    {
        private readonly EffectGraph _graph;
        public override EffectGraph Graph => _graph;
 
        private VideoClip(EffectGraph graph) => _graph = graph;
 
        // ---------------------------------------------------------------
        // Convenience factories — one per InputNode kind, each producing
        // the "normal/default" graph shape: that InputNode -> TintNode ->
        // TransformNode -> Output. Equivalent to what used to be
        // constructing a distinct Clip subtype.
        // ---------------------------------------------------------------
 
        public static VideoClip CreateFromSource(Source source, TimeSpan start, TimeSpan duration) =>
            new(EffectGraph.CreateVideoGraph(new MediaSourceNode { Source = source })) { Start = start, Duration = duration };
 
        public static VideoClip CreateText(
            string content, TimeSpan start, TimeSpan duration,
            FontFace fontFace = FontFace.ComicSansMs, SKFontStyle? fontStyle = null,
            SKTextAlign align = SKTextAlign.Center, int wordsPerLine = int.MaxValue) =>
            new(EffectGraph.CreateVideoGraph(new TextInputNode
            {
                Content = content,
                FontFace = fontFace,
                FontStyle = fontStyle ?? SKFontStyle.Normal,
                Align = align,
                WordsPerLine = wordsPerLine,
            }))
            { Start = start, Duration = duration };
 
        public static VideoClip CreateColorGenerator(SKColor color, TimeSpan start, TimeSpan duration) =>
            new(EffectGraph.CreateVideoGraph(new ColorGeneratorInputNode { Color = new(color) })) { Start = start, Duration = duration };
 
        public static VideoClip CreateNoise(TimeSpan start, TimeSpan duration, int? seed = null, float detail = 0.03f, float seetheRate = 0.03f) =>
            new(EffectGraph.CreateVideoGraph(new NoiseInputNode
            {
                Seed = seed ?? Random.Shared.Next(),
                Detail = detail,
                SeetheRate = seetheRate,
            }))
            { Start = start, Duration = duration };
 
        public static VideoClip CreateTimelineEmbed(TimelineReference reference, TimeSpan start, TimeSpan duration) =>
            new(EffectGraph.CreateVideoGraph(new TimelineVideoInputNode { Reference = reference })) { Start = start, Duration = duration };
 
        /// <summary>
        /// Escape hatch for a fully custom graph — multiple InputNodes,
        /// branches merged through MergeNode, extra effect nodes, whatever
        /// an author wants. `graph` must already be a valid Image-domain
        /// EffectGraph (see EffectGraph.CreateEmptyVideoGraph to start one
        /// from scratch, or build on a Create* factory's own graph after
        /// construction via `clip.Graph.AddNode(...)`/`Connect(...)`).
        /// </summary>
        public static VideoClip CreateCustom(EffectGraph graph, TimeSpan start, TimeSpan duration)
        {
            if (graph.Domain != EffectDomain.Image)
                throw new ArgumentException("VideoClip requires an Image-domain EffectGraph.", nameof(graph));
 
            return new VideoClip(graph) { Start = start, Duration = duration };
        }
 
        public override VideoClip Duplicate() => new(Graph.Duplicate()) { Start = Start, Duration = Duration };
    }
}
 