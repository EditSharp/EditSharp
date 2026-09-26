using EditSharp.Components.Media;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Nodes.Input
{
    /// <summary>A solid colour filling the canvas; the colour can be keyframed.</summary>
    [NodeKind("color", DisplayName = "Color")]
    public sealed class ColorNode : VideoInputNode
    {
        Animatable<SKColor> _color = new(SKColors.Black);
        /// <summary>The colour; black by default.</summary>
        [Editable("Color")]
        public Animatable<SKColor> Color { get => _color; set => Transaction.Set(this, ref _color, value, static (o, v) => o._color = v); }

        /// <inheritdoc/>
        public override IEnumerable<IAnimatable> Animatables => [Color];

        /// <summary>Always null: a colour has no end of its own.</summary>
        /// <param name="ct">Unused.</param>
        /// <returns>Null.</returns>
        public override Task<Time?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult<Time?>(null);

        internal override Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default) =>
            Task.FromResult<IPreparedVideoSource>(new PreparedGenerator(this, (contentTime, size) =>
            {
                SKColor colour = Color.Evaluate(contentTime);
                return GeneratedFrames.Record(size, canvas => canvas.Clear(colour));
            }));
    }
}
