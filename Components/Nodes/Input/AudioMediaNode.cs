using System;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Components.Media;
using EditSharp.Editing;
using EditSharp.History;

namespace EditSharp.Components.Nodes.Input
{
    /// <summary>Feeds an <see cref="AudioMedia"/>'s samples into the graph.</summary>
    /// <remarks>The media is shared: any number of nodes may read the same one, and it's saved by its Id. Setting it trims the clip if the new media ends sooner. With no media the node reports <see cref="SourceUnavailableReason.NoMedia"/>.</remarks>
    [NodeKind("media-audio", DisplayName = "Audio")]
    public sealed class AudioMediaNode : AudioInputNode, IPropertyDefaults
    {
        AudioMedia? _media;
        /// <summary>Where the samples come from; null plays nothing and reports no media selected.</summary>
        [Editable("Media", Editor = PropertyEditor.Media)]
        public AudioMedia? Media
        {
            get => _media;
            set
            {
                if (ReferenceEquals(_media, value)) return;

                AudioMedia? previous = _media;
                Transaction.Set(this, ref _media, value, static (o, v) => o._media = v);
                previous?.RemoveUser(this);
                value?.AddUser(this);
                EndMayHaveMoved();
            }
        }

        /// <inheritdoc/>
        /// <remarks>Duration resets to the media's length once it's known.</remarks>
        public bool TryGetDefault(string propertyName, out object? value)
        {
            value = null;
            if (propertyName != nameof(Duration) || !TryGetNaturalLength(out Time? length) || length is null) return false;

            value = length;
            return true;
        }

        /// <inheritdoc/>
        public override bool TryGetNaturalLength(out Time? length)
        {
            if (Media is { } media) return media.TryGetNaturalLength(out length);

            length = null;
            return true;
        }

        /// <inheritdoc/>
        /// <remarks>Reads the media's <see cref="Audio.Analysis.AudioAnalysis"/> at the content time mapped through the in-point, duration and loop; silence when the analysis isn't in memory or the time is outside the media.</remarks>
        public override void DescribeSpectrum(in Audio.Analysis.SpectralContext context, ReadOnlySpan<Audio.Analysis.SpectralFrame> inputs, Audio.Analysis.SpectralFrame output, ref object? state)
        {
            output.Clear();

            if (Media is not { } media || string.IsNullOrEmpty(media.Path) || !Audio.Analysis.AudioAnalysisCache.TryGet(media.Path, out Audio.Analysis.AudioAnalysis analysis)) return;

            Time material;
            if (context.ContentTime < Time.Zero)
            {
                //before the in-point: the room a head extend would reach into
                material = (Start ?? Time.Zero) + context.ContentTime;
                if (material < Time.Zero) return;
            }
            else
            {
                try { material = ToMaterialTime(context.ContentTime, analysis.Duration); }
                catch (SourceUnavailableException) { return; }
            }

            int frame = analysis.FrameAt(material);
            analysis.BandsOf(frame).CopyTo(output.Bands);
            output.Peak = analysis.PeakOf(frame);
        }

        /// <inheritdoc/>
        public override Task<Time?> GetNaturalLengthAsync(CancellationToken ct = default) =>
            Media?.GetNaturalLengthAsync(ct) ?? Task.FromResult<Time?>(null);

        internal override async Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default)
        {
            if (Media is not { } media)
                throw new SourceUnavailableException(SourceUnavailableReason.NoMedia, "No media is selected.");

            IPreparedAudioSource prepared = await media.PrepareAsync(ct);

            try
            {
                return new WindowedAudio(prepared, this, await media.GetNaturalLengthAsync(ct));
            }
            catch
            {
                prepared.Dispose();
                throw;
            }
        }

        /// <inheritdoc/>
        protected internal override IMedia? ReferencedMedia(Guid id) => Media?.Id == id ? Media : null;

        internal override object ContentIdentity => Media ?? (object)this;

        internal override string Description => Media is { } media ? $"{base.Description}: {media.Path}" : base.Description;
    }
}
