using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Sources
{
    /// <summary>Where a clip's raw content comes from: a media file, a generator, another timeline.</summary>
    /// <remarks>
    /// Compositing and mixing never know what kind of source they're reading. They
    /// prepare it, open readers on it, and react to the <see cref="SourceUnavailableReason"/>
    /// a failed read reports.
    /// <para>
    /// A source is plain data. Its [Editable] properties are exactly what
    /// <see cref="SourceSerializer"/> saves, and <see cref="Duplicate"/> round-trips
    /// through the serializer, so a new property is copied exactly when it's saved.
    /// Nothing prepared or opened (probe results, decoders) is kept on the source,
    /// so several Playbacks can read one source at once.
    /// </para>
    /// <para>
    /// To add a kind, derive from <see cref="Video.VideoSource"/> or <see cref="Audio.AudioSource"/>,
    /// mark the class [SourceKind("stable-id")], implement PrepareAsync and
    /// <see cref="GetNaturalLengthAsync"/>, and add whatever identifies its content
    /// to AddFingerprint. The id is written into saved files, so it must never change.
    /// BlenderSceneVideoSource and VSTInstrumentSource show the bare shape.
    /// </para>
    /// </remarks>
    public abstract class Source
    {
        TimeSpan? _start;
        /// <summary>How far into the source's own material to start; null starts at the beginning.</summary>
        [Editable("In point")]
        public TimeSpan? Start { get => _start; set { Transaction.Set(this, ref _start, value, static (o, v) => o._start = v); EndMayHaveMoved(); } }

        TimeSpan? _duration;
        /// <summary>How much of the source to use from <see cref="Start"/>; null uses everything there is.</summary>
        [Editable("Duration")]
        public TimeSpan? Duration { get => _duration; set { Transaction.Set(this, ref _duration, value, static (o, v) => o._duration = v); EndMayHaveMoved(); } }

        bool _loop;
        /// <summary>Whether the trimmed window repeats instead of ending.</summary>
        [Editable("Loop")]
        public bool Loop { get => _loop; set { Transaction.Set(this, ref _loop, value, static (o, v) => o._loop = v); EndMayHaveMoved(); } }

        //the node this source is in, if any; set by the node
        internal Nodes.Node? Holder { get; set; }

        //an edit may have pulled the source's end inside its clip: trim the clip to it
        private protected void EndMayHaveMoved() => Holder?.OwnerClip?.TrimToSources();

        /// <summary>How long the source's own material is, ignoring <see cref="Start"/> and <see cref="Duration"/>.</summary>
        /// <param name="ct">Cancels finding the length.</param>
        /// <returns>The length, or null when the material has no end of its own (a still image, a generator).</returns>
        /// <exception cref="SourceUnavailableException">The material can't be read.</exception>
        public abstract Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default);

        /// <summary>How much material the source offers from <see cref="Start"/>: <see cref="Duration"/>, cut short by the end of the material.</summary>
        /// <param name="ct">Cancels finding the length.</param>
        /// <returns>The length; null when there's neither a Duration nor a natural length, zero when Start is at or past the end.</returns>
        /// <exception cref="SourceUnavailableException">The material can't be read.</exception>
        /// <exception cref="InvalidOperationException"><see cref="Start"/> is negative.</exception>
        public async Task<TimeSpan?> GetUsableLengthAsync(CancellationToken ct = default)
        {
            TimeSpan? length = ResolveWindow(await GetNaturalLengthAsync(ct)).Length;
            return length < TimeSpan.Zero ? TimeSpan.Zero : length;
        }

        /// <summary><see cref="GetNaturalLengthAsync"/>'s answer, if it's known right away.</summary>
        /// <param name="length">The natural length when known; otherwise null.</param>
        /// <returns>False while the length still has to be found, such as for a file not probed yet.</returns>
        public virtual bool TryGetNaturalLength(out TimeSpan? length)
        {
            Task<TimeSpan?> task = GetNaturalLengthAsync();
            length = task.IsCompletedSuccessfully ? task.Result : null;
            return task.IsCompletedSuccessfully;
        }

        /// <summary><see cref="GetUsableLengthAsync"/>'s answer, if it's known right away.</summary>
        /// <param name="length">The usable length when known; otherwise null.</param>
        /// <returns>False while the natural length still has to be found.</returns>
        /// <exception cref="InvalidOperationException"><see cref="Start"/> is negative.</exception>
        public bool TryGetUsableLength(out TimeSpan? length)
        {
            length = null;
            if (!TryGetNaturalLength(out TimeSpan? natural)) return false;

            length = ResolveWindow(natural).Length;
            if (length < TimeSpan.Zero) length = TimeSpan.Zero;
            return true;
        }

        /// <summary>A deep copy, made by saving and loading this source.</summary>
        /// <remarks>History is suppressed: the copy has nothing to undo. A nested timeline is shared, not copied.</remarks>
        /// <returns>The copy.</returns>
        public virtual Source Duplicate() => Transaction.Suppressed(() => SourceSerializer.Deserialize(SourceSerializer.Serialize(this), ReferencedTimeline));

        /// <summary>This source's keyframeable values.</summary>
        /// <remarks>The node holding the source reports them as its own, so keyframe editing, thumbnails and head trims reach them.</remarks>
        [System.Text.Json.Serialization.JsonIgnore]
        public virtual IEnumerable<IAnimatable> Animatables => [];

        //the nested timeline with `id` if this source refers to it, so Duplicate can share it
        private protected virtual Timeline? ReferencedTimeline(Guid id) => null;

        //adds what identifies this source's content to a clip fingerprint; Start is left out because
        //it's the thumbnail anchor, not content. Kinds add their own identity (a path, a seed) on top
        internal virtual void AddFingerprint(ref HashCode hash)
        {
            hash.Add(GetType());
            hash.Add(Duration);
            hash.Add(Loop);
        }

        /// <summary>The window of the source's own time this source offers: from <see cref="Start"/>, for <see cref="Duration"/>, cut short by the end of the material.</summary>
        /// <param name="naturalLength">The material's length; null if it has no end.</param>
        /// <returns>Where the window starts, and its length; a null length has no end, a negative one starts past the end.</returns>
        /// <exception cref="InvalidOperationException"><see cref="Start"/> is negative.</exception>
        protected (TimeSpan Start, TimeSpan? Length) ResolveWindow(TimeSpan? naturalLength)
        {
            TimeSpan start = Start ?? TimeSpan.Zero;

            if (start < TimeSpan.Zero)
                throw new InvalidOperationException($"{GetType().Name}: Start ({start}) is negative.");

            TimeSpan? remaining = naturalLength - start;

            TimeSpan? length = (Duration, remaining) switch
            {
                ({ } d, { } r) => d < r ? d : r,
                ({ } d, null) => d,
                (null, { } r) => r,
                _ => null,
            };

            return (start, length);
        }

        /// <summary>Maps content time to the source's own time: adds <see cref="Start"/>, and wraps around the window when <see cref="Loop"/> is set.</summary>
        /// <remarks>Kinds that map time their own way don't have to use this.</remarks>
        /// <param name="contentTime">Time since the in-point, at 1x.</param>
        /// <param name="naturalLength">The material's length; null if it has no end.</param>
        /// <returns>The time in the source's own material.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="contentTime"/> is negative.</exception>
        /// <exception cref="SourceUnavailableException"><see cref="SourceUnavailableReason.EndOfSource"/>: the time is past the end of the window and Loop is off, or Start is at or past the end of the material.</exception>
        /// <exception cref="InvalidOperationException"><see cref="Start"/> is negative.</exception>
        protected TimeSpan ToSourceTime(TimeSpan contentTime, TimeSpan? naturalLength)
        {
            if (contentTime < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(contentTime), contentTime, "Content time cannot be negative.");

            (TimeSpan start, TimeSpan? length) = ResolveWindow(naturalLength);

            if (length is not { } window)
                return start + contentTime;

            if (window <= TimeSpan.Zero)
                throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource,
                    $"{GetType().Name}: Start ({start}) is at or past the end of the source ({naturalLength}).");

            if (contentTime < window)
                return start + contentTime;

            if (!Loop)
                throw new SourceUnavailableException(SourceUnavailableReason.EndOfSource,
                    $"{GetType().Name}: {contentTime} is past the end of its {window} window.");

            return start + TimeSpan.FromTicks(contentTime.Ticks % window.Ticks);
        }
    }
}
