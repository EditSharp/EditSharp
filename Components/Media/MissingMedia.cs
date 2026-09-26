using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Components.Media
{
    /// <summary>A media a project names but this build can't load, such as a kind from an extension that isn't installed.</summary>
    /// <remarks>It keeps the media's saved JSON word for word, so saving the project writes it back unchanged. It has no content.</remarks>
    [MediaKind("editsharp.missing", DisplayName = "Missing media", Listed = false)]
    public sealed class MissingMedia : IMedia
    {
        /// <summary>The kind id the project names.</summary>
        public string Kind { get; }

        /// <summary>Why it couldn't be loaded.</summary>
        public string Reason { get; }

        //the media exactly as it was saved
        internal JsonObject Saved { get; }

        [SetsRequiredMembers]
        internal MissingMedia(string kind, string reason, JsonObject saved)
        {
            Id = saved["id"]?.GetValue<Guid>() ?? Guid.NewGuid();
            Path = saved["path"]?.GetValue<string>() ?? "";
            Kind = kind;
            Reason = reason;
            Saved = saved;
        }

        /// <inheritdoc/>
        public override Task<Time?> GetNaturalLengthAsync(CancellationToken ct = default) => Task.FromResult<Time?>(null);

        /// <inheritdoc/>
        public override IMedia Duplicate()
        {
            //a copy is a new media
            var copy = (JsonObject)Saved.DeepClone();
            copy.Remove("id");
            return new MissingMedia(Kind, Reason, copy);
        }
    }
}
