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
            if (propertyName != nameof(Duration) || !TryGetNaturalLength(out TimeSpan? length) || length is null) return false;

            value = length;
            return true;
        }

        /// <inheritdoc/>
        public override bool TryGetNaturalLength(out TimeSpan? length)
        {
            if (Media is { } media) return media.TryGetNaturalLength(out length);

            length = null;
            return true;
        }

        /// <summary>The media's length.</summary>
        /// <param name="ct">Cancels finding it.</param>
        /// <returns>The length; null if the media has none, or when no media is selected.</returns>
        /// <exception cref="SourceUnavailableException">The media can't be read.</exception>
        public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) =>
            Media?.GetNaturalLengthAsync(ct) ?? Task.FromResult<TimeSpan?>(null);

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
