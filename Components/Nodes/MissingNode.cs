using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EditSharp.Audio.Engine;

namespace EditSharp.Components.Nodes
{
    /// <summary>A node a project names but this build can't load, such as one from an extension that isn't installed.</summary>
    /// <remarks>
    /// It keeps the node's saved JSON word for word, so saving the project writes it back unchanged, and it
    /// keeps the ports its wires used. It passes its first input of the graph's kind straight through; with
    /// none it shows or plays nothing.
    /// </remarks>
    [NodeKind("editsharp.missing", DisplayName = "Missing node", Listed = false)]
    public sealed class MissingNode : Node
    {
        private readonly List<NodePort> _ports;

        /// <summary>The kind id the project names.</summary>
        public string Kind { get; }

        /// <summary>Why it couldn't be loaded.</summary>
        public string Reason { get; }

        //the node exactly as it was saved
        internal JsonObject Saved { get; }

        internal MissingNode(string kind, string reason, JsonObject saved, List<NodePort> ports)
        {
            Kind = kind;
            Reason = reason;
            Saved = saved;
            _ports = ports;
        }

        /// <inheritdoc/>
        public override IReadOnlyList<NodePort> Ports => _ports;

        /// <inheritdoc/>
        public override Node Duplicate() => new MissingNode(Kind, Reason, (JsonObject)Saved.DeepClone(), [.. _ports]);

        internal override IAudioProcessor? CreateAudioProcessor(AudioSession session) => new PassThrough();

        //the first audio input to the first audio output
        private sealed class PassThrough : IAudioProcessor
        {
            public void Process(in AudioTick tick, AudioPortBuffers ports)
            {
                if (ports.Outputs.Length == 0) return;

                Span<float> output = ports.Outputs[0].AsSpan(0, tick.Samples);
                if (ports.Inputs.Length > 0) ports.Inputs[0].AsSpan(0, tick.Samples).CopyTo(output);
                else output.Clear();
            }

            public void Dispose() { }
        }
    }
}
