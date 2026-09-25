using EditSharp.Components.Media;
using System;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Compositing.Generators;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Nodes.Input
{
    /// <summary>A field of gradient noise filling the canvas, changing over time.</summary>
    [NodeKind("noise", DisplayName = "Noise")]
    public sealed class NoiseNode : VideoInputNode
    {
        int _seed = Random.Shared.Next();
        /// <summary>Picks the pattern; the same seed draws the same noise. Random by default.</summary>
        [Editable("Seed")]
        public int Seed { get => _seed; set => Transaction.Set(this, ref _seed, value, static (o, v) => o._seed = v); }

        float _detail = 0.03f;
        /// <summary>How fine the noise is, from 0 to 1; higher packs more cells across the canvas.</summary>
        [Editable("Detail", Min = 0, Max = 1, Step = 0.001)]
        public float Detail { get => _detail; set => Transaction.Set(this, ref _detail, value, static (o, v) => o._detail = v); }

        float _seetheRate = 0.03f;
        /// <summary>How fast the noise changes over time, from 0 (still) to 1.</summary>
        [Editable("Seethe rate", Min = 0, Max = 1, Step = 0.001)]
        public float SeetheRate { get => _seetheRate; set => Transaction.Set(this, ref _seetheRate, value, static (o, v) => o._seetheRate = v); }

        /// <summary>Always null: noise has no end of its own.</summary>
        /// <param name="ct">Unused.</param>
        /// <returns>Null.</returns>
        public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult<TimeSpan?>(null);

        internal override Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default) =>
            Task.FromResult<IPreparedVideoSource>(new PreparedGenerator(this, (contentTime, size) =>
                GeneratedFrames.Record(size, canvas =>
                    NoiseGenerator.Draw(canvas, Seed, Detail, SeetheRate, contentTime.TotalSeconds, size.Width, size.Height))));
    }
}
