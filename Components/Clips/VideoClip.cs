using System;
using SkiaSharp;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Sources;
using EditSharp.History;
using EditSharp.Components.Sources.Video;

namespace EditSharp.Components.Clips
{
    /// <summary>
    /// The only concrete visual Clip type — see Clip's own remarks on the
    /// "clips are graphs" rewrite. What used to be TextClip/GeneratorClip/
    /// NoiseClip/TimelineVideoClip (each a distinct Clip subtype) and
    /// VideoClip's own flat Source property are now all just different
    /// InputNode types wired into this class's single Image-domain
    /// Graph — see the static factory methods below and
    /// EditSharp.Components.Nodes.Sources.
    ///
    /// Sealed and otherwise data-less beyond Start/Duration/LinkGroupId
    /// (inherited from Clip) and Graph — everything else a visual clip
    /// used to carry directly (Modulate, Transform) now lives on nodes
    /// INSIDE the graph (TintNode, TransformNode), not on this class.
    /// </summary>
    public sealed class VideoClip : Clip
    {
        private readonly Graph _graph;
        public override Graph Graph => _graph;

        private VideoClip(Graph graph)
        {
            _graph = graph;
            graph.Clip = this;
        }

        // ---------------------------------------------------------------
        // Convenience factories — one per InputNode kind, each producing
        // the "normal/default" graph shape: that InputNode -> TintNode ->
        // TransformNode -> Output. Equivalent to what used to be
        // constructing a distinct Clip subtype.
        // ---------------------------------------------------------------

        public static VideoClip CreateFromSource(VideoSource source, TimeSpan start, TimeSpan duration) => Transaction.Suppressed(() => new VideoClip(Graph.CreateVideoGraph(new VideoSourceNode { Source = source })) { Start = start, Duration = duration });

        public static VideoClip CreateText(
            string content, TimeSpan start, TimeSpan duration,
            string font = "Comic Sans MS", int weight = 400, bool italic = false,
            HorizontalTextAlignment align = HorizontalTextAlignment.Center, float size = 0.05f) => CreateFromSource(new TextVideoSource
            {
                Content = content,
                Font = font,
                Weight = weight,
                Italic = italic,
                HorizontalAlignment = align,
                Size = size,
            }, start, duration);

        public static VideoClip CreateColorGenerator(SKColor color, TimeSpan start, TimeSpan duration) =>
            CreateFromSource(Transaction.Suppressed(() => new ColorVideoSource { Color = new(color) }), start, duration);

        public static VideoClip CreateNoise(TimeSpan start, TimeSpan duration, int? seed = null, float detail = 0.03f, float seetheRate = 0.03f) =>
            CreateFromSource(Transaction.Suppressed(() => new NoiseVideoSource
            {
                Seed = seed ?? Random.Shared.Next(),
                Detail = detail,
                SeetheRate = seetheRate,
            }), start, duration);

        public static VideoClip CreateTimelineEmbed(Timeline timeline, TimeSpan start, TimeSpan duration) =>
            CreateFromSource(Transaction.Suppressed(() => new TimelineVideoSource { Timeline = timeline }), start, duration);

        /// <summary>
        /// Escape hatch for a fully custom graph — multiple InputNodes,
        /// branches merged through MergeNode, extra effect nodes, whatever
        /// an author wants. `graph` must already be a valid Image-domain
        /// Graph (see Graph.CreateEmptyVideoGraph to start one
        /// from scratch, or build on a Create* factory's own graph after
        /// construction via `clip.Graph.AddNode(...)`/`Connect(...)`).
        /// </summary>
        public static VideoClip CreateCustom(Graph graph, TimeSpan start, TimeSpan duration)
        {
            if (graph.Domain != NodeDomain.Image)
                throw new ArgumentException("VideoClip requires an Image-domain Graph.", nameof(graph));

            return Transaction.Suppressed(() => new VideoClip(graph) { Start = start, Duration = duration });
        }

        public override VideoClip Duplicate() => Transaction.Suppressed(() => new VideoClip(Graph.Duplicate()) { Start = Start, Duration = Duration, Speed = Speed });
    }
}
