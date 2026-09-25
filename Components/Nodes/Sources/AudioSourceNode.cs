using EditSharp.Audio.Processors;
using EditSharp.Audio.Engine;
using System;
using System.Collections.Generic;
using EditSharp.Components.Sources.Audio;
using EditSharp.Components.Nodes;
using EditSharp.History;
using EditSharp.Editing;

namespace EditSharp.Components.Nodes.Sources
{
    /// <summary>Feeds a <see cref="AudioSource"/>'s samples into the graph, whatever kind of source it is.</summary>
    /// <remarks>Output: Audio.</remarks>
    public sealed class AudioSourceNode : InputNode, ITrimmableInput
    {
        AudioSource _source = null!;
        /// <summary>Where the samples come from.</summary>
        /// <remarks>Setting it trims the clip if the new source ends sooner.</remarks>
        [Editable("Source")]
        public required AudioSource Source
        {
            get => _source;
            set
            {
                Timeline.Reembed(OwnerClip, Timeline.EmbeddedIn(_source), Timeline.EmbeddedIn(value));
                Transaction.Set(this, ref _source, value, static (o, v) => { o._source = v; if (v is not null) v.Holder = o; });
                value.Holder = this;
                OwnerClip?.TrimToSources();
            }
        }

        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => Source.Animatables;
        internal override IAudioProcessor CreateAudioProcessor(AudioSession session) =>
            new ContentInputProcessor(() => Source, () => new SourceContentAudio(Source, Id, session), session);
        /// <inheritdoc/>
        public override Node Duplicate() => Transaction.Suppressed(() => new AudioSourceNode { Enabled = Enabled, Source = Source.Duplicate() });

        /// <summary>The source's <see cref="Components.Sources.Source.Start"/>, zero when unset.</summary>
        public TimeSpan InPoint
        {
            get => Source.Start ?? TimeSpan.Zero;
            set => Source.Start = value;
        }

        /// <summary>How far the in-point can move earlier: back to the start of the source.</summary>
        public TimeSpan MaxHeadroom => Source.Start ?? TimeSpan.Zero;

        /// <summary>The source's usable length; null when it loops, has no end, or isn't known yet.</summary>
        public TimeSpan? ContentLength => !Source.Loop && Source.TryGetUsableLength(out TimeSpan? length) ? length : null;
    }
}
