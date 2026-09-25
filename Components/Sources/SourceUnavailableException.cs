using System;

namespace EditSharp.Components.Sources
{
    /// <summary>Why a source could not provide content right now; compositing picks what to show from this.</summary>
    public enum SourceUnavailableReason
    {
        /// <summary>The reader is still opening and has nothing to hand back yet.</summary>
        Opening,

        /// <summary>The read needs a proxy frame that a queued or running build hasn't reached yet.</summary>
        ProxyPending,

        /// <summary>The read needs a proxy frame, and no build is queued or running to make it.</summary>
        ProxyMissing,

        /// <summary>The underlying material is missing or unreadable (file gone, device unplugged).</summary>
        MediaOffline,

        /// <summary>The material is present but could not be decoded or rendered.</summary>
        DecodeError,

        /// <summary>The requested time is past the end of the source's window and Loop is off.</summary>
        EndOfSource,
    }

    /// <summary>
    /// The one way a source reports that it cannot provide content. Sources
    /// never substitute placeholders themselves; they throw this and let the
    /// caller decide by Reason.
    /// </summary>
    public sealed class SourceUnavailableException(SourceUnavailableReason reason, string message, Exception? inner = null)
        : Exception(message, inner)
    {
        public SourceUnavailableReason Reason { get; } = reason;
    }
}
