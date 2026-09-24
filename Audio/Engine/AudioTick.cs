using System;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;

namespace EditSharp.Audio.Engine
{
    /// <summary>Sample rate and channel count every buffer in a session shares.</summary>
    internal readonly record struct AudioFormat(int SampleRate, int Channels);

    /// <summary>
    /// One block of work for a clip's graph: which timeline frames it covers
    /// and the clip's content position at the first of them, both as a time
    /// (for automation) and as an exact content frame (for reading audio;
    /// TimeSpan's 100 ns steps would put reads off by fractions of a sample).
    /// ContentStep is how much content time each output frame advances
    /// (Clip.Speed / rate). Graph is the
    /// clip's graph snapshot for this tick; Pitch is how its inputs keep pitch
    /// when ContentStep isn't 1x.
    /// </summary>
    internal readonly record struct AudioTick(
        AudioFormat Format,
        long TimelineFrame,
        int Frames,
        TimeSpan ContentStart,
        double ContentFrame,
        double ContentStep,
        Graph Graph,
        PitchPreservation Pitch = PitchPreservation.WSOLA)
    {
        public int Samples => Frames * Format.Channels;

        public TimeSpan ContentTimeAt(int frame) => ContentStart + TimeSpan.FromSeconds(frame * ContentStep);
    }

    /// <summary>
    /// What a processor gets each tick: one buffer per audio input port and
    /// one per audio output port, in the node's port order. An unconnected
    /// input is silence. Only the first Samples of each buffer are live.
    /// </summary>
    internal sealed class AudioPortBuffers(float[][] inputs, float[][] outputs)
    {
        public float[][] Inputs { get; } = inputs;
        public float[][] Outputs { get; } = outputs;
    }

    /// <summary>
    /// A node's DSP, created by the node (Node.CreateAudioProcessor) and kept
    /// for as long as the node stays in its clip's graph, so filter history,
    /// envelopes and phases carry from block to block.
    /// </summary>
    internal interface IAudioProcessor : IDisposable
    {
        void Process(in AudioTick tick, AudioPortBuffers ports);
    }
}
