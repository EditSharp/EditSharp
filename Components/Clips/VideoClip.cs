using EditSharp.Components.Media;
using System;
using SkiaSharp;
using EditSharp.Components.Nodes;
using EditSharp.Components.Nodes.Input;
using EditSharp.History;

namespace EditSharp.Components.Clips
{
    /// <summary>A clip that shows an image: its graph's inputs, through its effects.</summary>
    /// <remarks>It goes on a <see cref="Channels.VideoChannel"/>. The factories build the usual graph: the input, then a tint, then a transform.</remarks>
    public sealed class VideoClip : Clip
    {
        private readonly Graph _graph;
        /// <inheritdoc/>
        public override Graph Graph => _graph;

        private VideoClip(Graph graph)
        {
            _graph = graph;
            graph.Clip = this;
        }

        /// <summary>A clip showing a media.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="media">What the clip shows.</param>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <returns>The clip.</returns>
        public static VideoClip CreateFromMedia(VideoMedia media, TimeSpan start, TimeSpan duration) =>
            CreateFromInput(Transaction.Suppressed(() => new VideoMediaNode { Media = media }), start, duration);

        /// <summary>A clip showing an input node of any kind, such as a generator you've set up.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="input">Where the frames come from.</param>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <returns>The clip.</returns>
        public static VideoClip CreateFromInput(VideoInputNode input, TimeSpan start, TimeSpan duration) =>
            Transaction.Suppressed(() => new VideoClip(Graph.CreateVideoGraph(input)) { Start = start, Duration = duration });

        /// <summary>A clip showing text; see <see cref="TextNode"/>.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="content">The text.</param>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <param name="font">The name of an installed font family.</param>
        /// <param name="weight">How heavy the letters are, from 100 (thin) to 900 (black).</param>
        /// <param name="italic">Whether to use the font's italic.</param>
        /// <param name="align">How lines line up across the text box.</param>
        /// <param name="size">The font's em size, as a fraction of the frame's width.</param>
        /// <returns>The clip.</returns>
        public static VideoClip CreateText(
            string content, TimeSpan start, TimeSpan duration,
            string font = "Comic Sans MS", int weight = 400, bool italic = false,
            HorizontalTextAlignment align = HorizontalTextAlignment.Center, float size = 0.05f) =>
            CreateFromInput(Transaction.Suppressed(() => new TextNode
            {
                Content = content,
                Font = font,
                Weight = weight,
                Italic = italic,
                HorizontalAlignment = align,
                Size = size,
            }), start, duration);

        /// <summary>A clip filling the frame with one colour.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="color">The colour.</param>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <returns>The clip.</returns>
        public static VideoClip CreateColorGenerator(SKColor color, TimeSpan start, TimeSpan duration) =>
            CreateFromInput(Transaction.Suppressed(() => new ColorNode { Color = new(color) }), start, duration);

        /// <summary>A clip filling the frame with changing noise; see <see cref="NoiseNode"/>.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <param name="seed">Picks the pattern; null picks one at random.</param>
        /// <param name="detail">How fine the noise is, from 0 to 1.</param>
        /// <param name="seetheRate">How fast it changes, from 0 (still) to 1.</param>
        /// <returns>The clip.</returns>
        public static VideoClip CreateNoise(TimeSpan start, TimeSpan duration, int? seed = null, float detail = 0.03f, float seetheRate = 0.03f) =>
            CreateFromInput(Transaction.Suppressed(() => new NoiseNode
            {
                Seed = seed ?? Random.Shared.Next(),
                Detail = detail,
                SeetheRate = seetheRate,
            }), start, duration);

        /// <summary>A clip showing another timeline's picture.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="timeline">The timeline to show.</param>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <returns>The clip.</returns>
        public static VideoClip CreateTimelineEmbed(Timeline timeline, TimeSpan start, TimeSpan duration) =>
            CreateFromInput(Transaction.Suppressed(() => new TimelineVideoNode { Timeline = timeline }), start, duration);

        /// <summary>A clip with a graph you've built, such as one with several inputs merged together.</summary>
        /// <remarks>The clip isn't placed; add it to a channel. Nothing is recorded in history.</remarks>
        /// <param name="graph">The clip's graph; start one with <see cref="Graph.CreateEmptyVideoGraph"/>.</param>
        /// <param name="start">Where the clip starts on the timeline.</param>
        /// <param name="duration">How long it lasts.</param>
        /// <returns>The clip.</returns>
        /// <exception cref="ArgumentException"><paramref name="graph"/> isn't a video graph.</exception>
        public static VideoClip CreateCustom(Graph graph, TimeSpan start, TimeSpan duration)
        {
            if (graph.Domain != NodeDomain.Image)
                throw new ArgumentException("VideoClip requires an Image-domain Graph.", nameof(graph));

            return Transaction.Suppressed(() => new VideoClip(graph) { Start = start, Duration = duration });
        }

        /// <inheritdoc/>
        public override VideoClip Duplicate() => Transaction.Suppressed(() => new VideoClip(Graph.Duplicate()) { Name = Name, Start = Start, Duration = Duration, Speed = Speed });
    }
}
