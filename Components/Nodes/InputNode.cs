using EditSharp.Components.Media;
using System;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Nodes
{
    /// <summary>A node that brings content into a graph, rather than taking it from upstream: a media file, a generator, another timeline.</summary>
    /// <remarks>
    /// A graph can have any number of inputs, or none; one that reaches the output
    /// node is what makes a clip show or play anything. An input owns everything
    /// about how its content is used: the window of it the clip plays
    /// (<see cref="Start"/>, <see cref="Duration"/>, <see cref="Loop"/>) and every
    /// kind-specific setting, keyframed ones included, so its animations live on
    /// the node like any other node's. A kind that reads an <see cref="IMedia"/> holds
    /// it as a property; the media itself knows nothing about clips.
    /// <para>
    /// Trimming a clip's head moves every input's in-point in its graph by the same
    /// amount, limited by whichever has the least room.
    /// </para>
    /// <para>
    /// To add a kind, derive from <see cref="Input.VideoInputNode"/> or
    /// <see cref="Input.AudioInputNode"/>, mark the class [NodeKind("stable-id")],
    /// implement PrepareAsync and <see cref="GetNaturalLengthAsync"/>, and list any
    /// Animatable properties in <see cref="Node.Animatables"/>. Copying and saving
    /// come for free: an input's [Editable] properties are exactly what
    /// <see cref="ComponentSerializer"/> saves, and <see cref="Duplicate"/> round-trips
    /// through it. The id is written into saved files, so it must never change.
    /// </para>
    /// </remarks>
    public abstract class InputNode : Node
    {
        Time? _start;
        /// <summary>How far into the content to start; null starts at the beginning.</summary>
        [Editable("In point")]
        public Time? Start { get => _start; set { Transaction.Set(this, ref _start, value, static (o, v) => o._start = v); EndMayHaveMoved(); } }

        Time? _duration;
        /// <summary>How much of the content to use from <see cref="Start"/>; null uses everything there is.</summary>
        [Editable("Duration")]
        public Time? Duration { get => _duration; set { Transaction.Set(this, ref _duration, value, static (o, v) => o._duration = v); EndMayHaveMoved(); } }

        bool _loop;
        /// <summary>Whether the trimmed window repeats instead of ending.</summary>
        [Editable("Loop")]
        public bool Loop { get => _loop; set { Transaction.Set(this, ref _loop, value, static (o, v) => o._loop = v); EndMayHaveMoved(); } }

        /// <summary>Trims the clip to the content's end, after an edit that may have pulled the end inside the clip.</summary>
        /// <remarks>Call it from the setter of any property that changes how much content there is, such as a media or a timeline.</remarks>
        protected void EndMayHaveMoved() => OwnerClip?.TrimToSources();

        /// <summary>How long the content is, ignoring <see cref="Start"/> and <see cref="Duration"/>.</summary>
        /// <param name="ct">Cancels finding the length.</param>
        /// <returns>The length, or null when the content has no end of its own (a still image, a generator).</returns>
        /// <exception cref="SourceUnavailableException">The content can't be read.</exception>
        public abstract Task<Time?> GetNaturalLengthAsync(CancellationToken ct = default);

        /// <summary>How much content the input offers from <see cref="Start"/>: <see cref="Duration"/>, cut short by the end of the content.</summary>
        /// <param name="ct">Cancels finding the length.</param>
        /// <returns>The length; null when there's neither a Duration nor a natural length, zero when Start is at or past the end.</returns>
        /// <exception cref="SourceUnavailableException">The content can't be read.</exception>
        /// <exception cref="InvalidOperationException"><see cref="Start"/> is negative.</exception>
        public async Task<Time?> GetUsableLengthAsync(CancellationToken ct = default)
        {
            Time? length = ResolveWindow(await GetNaturalLengthAsync(ct)).Length;
            return length < Time.Zero ? Time.Zero : length;
        }

        /// <summary><see cref="GetNaturalLengthAsync"/>'s answer, if it's known right away.</summary>
        /// <param name="length">The natural length when known; otherwise null.</param>
        /// <returns>False while the length still has to be found, such as for a file not probed yet.</returns>
        public virtual bool TryGetNaturalLength(out Time? length)
        {
            Task<Time?> task = GetNaturalLengthAsync();
            length = task.IsCompletedSuccessfully ? task.Result : null;
            return task.IsCompletedSuccessfully;
        }

        /// <summary><see cref="GetUsableLengthAsync"/>'s answer, if it's known right away.</summary>
        /// <param name="length">The usable length when known; otherwise null.</param>
        /// <returns>False while the natural length still has to be found.</returns>
        /// <exception cref="InvalidOperationException"><see cref="Start"/> is negative.</exception>
        public bool TryGetUsableLength(out Time? length)
        {
            length = null;
            if (!TryGetNaturalLength(out Time? natural)) return false;

            length = ResolveWindow(natural).Length;
            if (length < Time.Zero) length = Time.Zero;
            return true;
        }

        /// <summary><see cref="Start"/>, zero when unset; how far into the content the clip starts.</summary>
        public Time InPoint
        {
            get => Start ?? Time.Zero;
            set => Start = value;
        }

        /// <summary>How far the in-point can move earlier: back to the start of the content.</summary>
        public Time MaxHeadroom => Start ?? Time.Zero;

        /// <summary>The usable length; null when the input loops, has no end, or isn't known yet.</summary>
        public Time? ContentLength => !Loop && TryGetUsableLength(out Time? length) ? length : null;

        /// <summary>A deep copy with a new <see cref="Node.Id"/>, made by saving and loading this node.</summary>
        /// <remarks>History is suppressed: the copy has nothing to undo. A media or a nested timeline is shared, not copied.</remarks>
        /// <returns>The copy.</returns>
        public override Node Duplicate() => ComponentSerializer.Copy(this, ReferencedTimeline, ReferencedMedia);

        /// <summary>The nested timeline with an id this node refers to, so a copy can share it.</summary>
        /// <param name="id">The timeline's <see cref="Timeline.Id"/>.</param>
        /// <returns>The timeline; null when this node doesn't refer to it.</returns>
        protected internal virtual Timeline? ReferencedTimeline(Guid id) => null;

        /// <summary>The media with an id this node refers to, so a copy can share it.</summary>
        /// <param name="id">The media's <see cref="IMedia.Id"/>.</param>
        /// <returns>The media; null when this node doesn't refer to it.</returns>
        protected internal virtual IMedia? ReferencedMedia(Guid id) => null;

        //what a session's reading of this node is keyed on: swapping it mid-session drops everything held
        //for the old content. The node itself for a generator; the media for a node that reads one
        internal virtual object ContentIdentity => this;

        //a person-readable name for logs and reports: the kind id, and the file when there is one
        internal virtual string Description => NodeKinds.Of(this)?.Id ?? GetType().Name;

        /// <summary>The window of the content's own time this input offers: from <see cref="Start"/>, for <see cref="Duration"/>, cut short by the end of the content.</summary>
        /// <param name="naturalLength">The content's length; null if it has no end.</param>
        /// <returns>Where the window starts, and its length; a null length has no end, a negative one starts past the end.</returns>
        /// <exception cref="InvalidOperationException"><see cref="Start"/> is negative.</exception>
        protected internal (Time Start, Time? Length) ResolveWindow(Time? naturalLength)
        {
            Time start = Start ?? Time.Zero;

            if (start < Time.Zero)
                throw new InvalidOperationException($"{GetType().Name}: Start ({start}) is negative.");

            Time? remaining = naturalLength - start;

            Time? length = (Duration, remaining) switch
            {
                ({ } d, { } r) => d < r ? d : r,
                ({ } d, null) => d,
                (null, { } r) => r,
                _ => null,
            };

            return (start, length);
        }

        /// <summary>Maps content time to the content's own time: adds <see cref="Start"/>, and wraps around the window when <see cref="Loop"/> is set.</summary>
        /// <remarks>Kinds that map time their own way don't have to use this.</remarks>
        /// <param name="contentTime">Time since the in-point, at 1x.</param>
        /// <param name="naturalLength">The content's length; null if it has no end.</param>
        /// <returns>The time in the content's own material.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="contentTime"/> is negative.</exception>
        /// <exception cref="SourceUnavailableException"><see cref="SourceUnavailableReason.EndOfSource"/>: the time is past the end of the window and Loop is off, or Start is at or past the end of the content.</exception>
        /// <exception cref="InvalidOperationException"><see cref="Start"/> is negative.</exception>
        protected internal Time ToMaterialTime(Time contentTime, Time? naturalLength)
        {
            if (contentTime < Time.Zero)
                throw new ArgumentOutOfRangeException(nameof(contentTime), contentTime, "Content time cannot be negative.");

            (Time start, Time? length) = ResolveWindow(naturalLength);

            if (length is not { } window)
                return start + contentTime;

            if (window <= Time.Zero)
                throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource,
                    $"{GetType().Name}: Start ({start}) is at or past the end of the content ({naturalLength}).");

            if (contentTime < window)
                return start + contentTime;

            if (!Loop)
                throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource,
                    $"{GetType().Name}: {contentTime} is past the end of its {window} window.");

            return start + contentTime % window;
        }
    }
}
