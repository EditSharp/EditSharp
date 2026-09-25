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
    /// <summary>Feeds an AudioSource's samples into the graph, whatever kind it is. Replaces the old AudioClip.Source property directly.</summary>
    public sealed class AudioSourceNode : InputNode, ITrimmableInput
    {
        AudioSource _source = null!;
        [Editable("Source")]
        public required AudioSource Source
        {
            get => _source;
            set
            {
                Transaction.Set(this, ref _source, value, static (o, v) => { o._source = v; v.Holder = o; });
                value.Holder = this;
                OwnerClip?.TrimToSources();
            }
        }

        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override IEnumerable<IAnimatable> Animatables => Source.Animatables;
        internal override IAudioProcessor CreateAudioProcessor(AudioSession session) =>
            new ContentInputProcessor(() => Source, () => new SourceContentAudio(Source, Id, session), session);
        public override Node Duplicate() => Transaction.Suppressed(() => new AudioSourceNode { Enabled = Enabled, Source = Source.Duplicate() });

        public TimeSpan InPoint
        {
            get => Source.Start ?? TimeSpan.Zero;
            set => Source.Start = value;
        }

        public TimeSpan MaxHeadroom => Source.Start ?? TimeSpan.Zero;

        public TimeSpan? ContentLength => !Source.Loop && Source.TryGetUsableLength(out TimeSpan? length) ? length : null;
    }
}
