using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Sources
{
    /// <summary>
    /// Something that can PROVIDE a clip's raw content (a media file, a
    /// rendered scene, an instrument) independently of how that content is
    /// later composited or mixed. Compositing never knows what kind of source
    /// it is reading from; it only prepares a source, opens readers on it and
    /// reacts to the SourceUnavailableReason a failing read reports.
    ///
    /// Every source is plain data: its [Editable] properties are exactly what
    /// SourceSerializer saves, and Duplicate is a round-trip through that same
    /// serializer, so a new property is copied precisely when it is saved.
    /// Nothing prepared or opened (probe results, decoders) is ever stored on
    /// the source itself; several Playbacks may read one source at once.
    ///
    /// ADDING A KIND: derive from VideoSource or AudioSource, mark the class
    /// [SourceKind("stable-id")] (the id is what saved files contain, so never
    /// change it), implement PrepareAsync and GetNaturalLengthAsync, and add
    /// anything identifying its content to AddFingerprint. See
    /// BlenderSceneVideoSource/VSTInstrumentSource for the bare shape.
    /// </summary>
    public abstract class Source
    {
        //how far into the source's own material to start using it
        TimeSpan? _start;
        [Editable("In point")]
        public TimeSpan? Start { get => _start; set => Transaction.Set(this, ref _start, value, static (o, v) => o._start = v); }

        //how much of the source to use from Start; null uses everything there is
        TimeSpan? _duration;
        [Editable("Duration")]
        public TimeSpan? Duration { get => _duration; set => Transaction.Set(this, ref _duration, value, static (o, v) => o._duration = v); }

        //repeat the trimmed window instead of ending
        bool _loop;
        [Editable("Loop")]
        public bool Loop { get => _loop; set => Transaction.Set(this, ref _loop, value, static (o, v) => o._loop = v); }

        /// <summary>
        /// How long the source's own material is, ignoring Start/Duration.
        /// Null means unbounded; a still image or a generator has no inherent
        /// end, so only Duration (or the clip) limits it.
        /// </summary>
        public abstract Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default);

        /// <summary>
        /// How much material the source offers from Start: Duration, clamped
        /// to what really follows Start. Null when unbounded (no Duration and no
        /// natural length); zero when Start is at or past the end.
        /// </summary>
        public async Task<TimeSpan?> GetUsableLengthAsync(CancellationToken ct = default)
        {
            TimeSpan? length = ResolveWindow(await GetNaturalLengthAsync(ct)).Length;
            return length < TimeSpan.Zero ? TimeSpan.Zero : length;
        }

        /// <summary>
        /// GetNaturalLengthAsync's answer when it's known right away; false
        /// while it still has to be found (a file not probed yet).
        /// </summary>
        public virtual bool TryGetNaturalLength(out TimeSpan? length)
        {
            Task<TimeSpan?> task = GetNaturalLengthAsync();
            length = task.IsCompletedSuccessfully ? task.Result : null;
            return task.IsCompletedSuccessfully;
        }

        /// <summary>GetUsableLengthAsync's answer when it's known right away; see TryGetNaturalLength.</summary>
        public bool TryGetUsableLength(out TimeSpan? length)
        {
            length = null;
            if (!TryGetNaturalLength(out TimeSpan? natural)) return false;

            length = ResolveWindow(natural).Length;
            if (length < TimeSpan.Zero) length = TimeSpan.Zero;
            return true;
        }

        /// <summary>
        /// Deep copy, through SourceSerializer; see class remarks. History is
        /// suppressed: a fresh copy has no past to undo.
        /// </summary>
        public virtual Source Duplicate() => Transaction.Suppressed(() => SourceSerializer.Deserialize(SourceSerializer.Serialize(this), ReferencedTimeline));

        /// <summary>
        /// This source's keyframed values. The node holding the source reports
        /// them as its own, so keyframe editing, fingerprints and head-trim
        /// keyframe shifting reach them.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public virtual IEnumerable<IAnimatable> Animatables => [];

        /// <summary>The nested timeline with `id` if this source refers to it; lets Duplicate share it.</summary>
        private protected virtual Timeline? ReferencedTimeline(Guid id) => null;

        /// <summary>
        /// Adds whatever identifies this source's CONTENT to a clip fingerprint
        /// (see ClipFingerprint). Start is deliberately left out: it is the
        /// thumbnail anchor, not content. Kinds add their own identity (a path,
        /// a scene, a preset) on top of this.
        /// </summary>
        internal virtual void AddFingerprint(ref HashCode hash)
        {
            hash.Add(GetType());
            hash.Add(Duration);
            hash.Add(Loop);
        }

        /// <summary>
        /// The window of source-local time this source actually offers: it
        /// begins at Start and runs for Duration, clamped to whatever material
        /// really follows Start. A null Length is unbounded.
        /// </summary>
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

        /// <summary>
        /// The standard content-time to source-time mapping, for kinds that
        /// want it: adds Start, wraps around the trimmed window when Loop is
        /// set, and otherwise reports EndOfSource once content time runs past
        /// it. Exotic kinds are free to ignore this and map time themselves.
        /// </summary>
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
