using System;
using System.Collections.Generic;
using EditSharp.Components.Clips;
using EditSharp.Components; // Source, Animatable
 
namespace EditSharp.Components.Effects
{
    /// <summary>Feeds the audio mixer. Mandatory, not removable — the one fixed anchor left in an Audio-domain graph.</summary>
    public sealed class AudioOutputNode : EffectNode
    {
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Input)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new AudioOutputNode();
    }
 
    // -----------------------------------------------------------------
    // Input nodes — "clips are graphs" rewrite. These replace the old
    // fixed AudioSourceNode anchor AND the old AudioClip.Source-is-the-
    // whole-clip / TimelineAudioClip Clip subtype: each is now just an
    // ordinary InputNode inside an AudioClip's single EffectGraph. See
    // InputNode's own remarks and AudioClip's static factory methods.
    // -----------------------------------------------------------------
 
    /// <summary>A real audio (or video-with-audio) file. Replaces the old AudioClip.Source property directly.</summary>
    public sealed class MediaAudioSourceNode : InputNode, ITrimmableInput
    {
        public required Source Source { get; set; }
 
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new MediaAudioSourceNode { Enabled = Enabled, Source = Source.Duplicate() };
 
        public TimeSpan InPoint
        {
            get => Source.Start ?? TimeSpan.Zero;
            set => Source.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Source.Start ?? TimeSpan.Zero;
    }
 
    public enum Waveform { Sine, Square, Sawtooth, Triangle }
 
    /// <summary>
    /// A synthesized tone — NEW node type, the audio-domain equivalent of
    /// ColorGeneratorInputNode on the video side (there was no prior
    /// "AudioGeneratorClip"; a "// future:" comment was the only trace of
    /// this idea before this rewrite). No in-point concept — procedural,
    /// generates for however long it's asked, same as NoiseInputNode.
    /// </summary>
    public sealed class ToneGeneratorInputNode : InputNode
    {
        public Waveform Waveform { get; set; } = Waveform.Sine;
        public Animatable<float> Frequency { get; set; } = new(440f);
        public Animatable<float> Amplitude { get; set; } = new(1f);
 
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new ToneGeneratorInputNode
        {
            Enabled = Enabled,
            Waveform = Waveform,
            Frequency = Frequency.Duplicate(),
            Amplitude = Amplitude.Duplicate(),
        };
    }
 
    /// <summary>
    /// Embeds another Timeline's fully mixed audio. Replaces the old
    /// TimelineAudioClip Clip subtype — TimelineReference itself is
    /// unchanged, it just lives on a node now.
    /// </summary>
    public sealed class TimelineAudioInputNode : InputNode, ITrimmableInput
    {
        public required TimelineReference Reference { get; set; }
 
        private static readonly NodePort[] StaticPorts = [new("Audio", PortType.Audio, PortDirection.Output)];
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new TimelineAudioInputNode { Enabled = Enabled, Reference = Reference.Duplicate() };
 
        public TimeSpan InPoint
        {
            get => Reference.Start ?? TimeSpan.Zero;
            set => Reference.Start = value;
        }
 
        public TimeSpan MaxHeadroom => Reference.Start ?? TimeSpan.Zero;
    }
 
    /// <summary>
    /// Auto-created and wired in by default (see EffectGraph.CreateAudioGraph)
    /// — this is what replaces the old flat AudioClip.Volume field. A
    /// normal, removable node beyond that default: an artist can delete it,
    /// add several gain stages at different points in a chain, or route
    /// around it entirely.
    /// </summary>
    public sealed class GainNode : EffectNode
    {
        public Animatable<float> Gain { get; set; } = new(1f);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Audio", PortType.Audio, PortDirection.Input),
 
            //optional Value modulation input (see ValueNodes.cs's own
            //remarks) — when connected, MULTIPLIES against Gain's own
            //keyframed value rather than replacing it
            new("Modulation", PortType.Value, PortDirection.Input, optional: true),
 
            new("Audio", PortType.Audio, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
        public override EffectNode Duplicate() => new GainNode { Enabled = Enabled, Gain = Gain.Duplicate() };
    }
 
    public sealed class EQBand
    {
        public Animatable<float> FrequencyHz { get; set; } = new(1000f);
        public Animatable<float> GainDb { get; set; } = new(0f);
        public Animatable<float> Q { get; set; } = new(1f);
 
        public EQBand Duplicate() => new()
        {
            FrequencyHz = FrequencyHz.Duplicate(),
            GainDb = GainDb.Duplicate(),
            Q = Q.Duplicate(),
        };
    }
 
    public sealed class EQNode : EffectNode
    {
        public List<EQBand> Bands { get; set; } = [];
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Audio", PortType.Audio, PortDirection.Input),
            new("Audio", PortType.Audio, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new EQNode
        {
            Enabled = Enabled,
            Bands = [.. Bands.ConvertAll(b => b.Duplicate())],
        };
    }
 
    public sealed class CompressorNode : EffectNode
    {
        public Animatable<float> Threshold { get; set; } = new(-18f);
        public Animatable<float> Ratio { get; set; } = new(4f);
        public Animatable<float> AttackMs { get; set; } = new(10f);
        public Animatable<float> ReleaseMs { get; set; } = new(100f);
        public Animatable<float> MakeupGainDb { get; set; } = new(0f);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("Audio", PortType.Audio, PortDirection.Input),
            new("Audio", PortType.Audio, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new CompressorNode
        {
            Enabled = Enabled,
            Threshold = Threshold.Duplicate(),
            Ratio = Ratio.Duplicate(),
            AttackMs = AttackMs.Duplicate(),
            ReleaseMs = ReleaseMs.Duplicate(),
            MakeupGainDb = MakeupGainDb.Duplicate(),
        };
    }
 
    /// <summary>
    /// The audio-domain equivalent of MergeNode — enables parallel
    /// processing (e.g. parallel compression: split the signal, compress
    /// one branch heavily, mix it back under the dry signal), and the
    /// canonical way to combine TWO InputNodes in one audio graph now that
    /// a graph can have more than one.
    /// </summary>
    public sealed class AudioMixNode : EffectNode
    {
        public Animatable<float> MixA { get; set; } = new(1f);
        public Animatable<float> MixB { get; set; } = new(1f);
 
        private static readonly NodePort[] StaticPorts =
        [
            new("A", PortType.Audio, PortDirection.Input),
            new("B", PortType.Audio, PortDirection.Input),
 
            //optional Value modulation inputs (see ValueNodes.cs's own
            //remarks) — when connected, MULTIPLY against MixA/MixB's own
            //keyframed values rather than replacing them
            new("MixAModulation", PortType.Value, PortDirection.Input, optional: true),
            new("MixBModulation", PortType.Value, PortDirection.Input, optional: true),
 
            new("Result", PortType.Audio, PortDirection.Output),
        ];
 
        public override IReadOnlyList<NodePort> Ports => StaticPorts;
 
        public override EffectNode Duplicate() => new AudioMixNode
        {
            Enabled = Enabled,
            MixA = MixA.Duplicate(),
            MixB = MixB.Duplicate(),
        };
    }
}
 