using System;

namespace EditSharp.Components.Sources
{
    /// <summary>Why a source can't provide content right now; compositing picks what to show from this.</summary>
    public enum SourceUnavailableReason
    {
        /// <summary>The reader is still opening and has nothing to hand back yet.</summary>
        Opening,

        /// <summary>The read needs a proxy frame that a queued or running build hasn't reached yet.</summary>
        ProxyPending,

        /// <summary>The read needs a proxy frame, and no build is queued or running to make it.</summary>
        ProxyMissing,

        /// <summary>The material is missing or unreadable (a file gone, a device unplugged).</summary>
        MediaOffline,

        /// <summary>The material is there but can't be decoded or rendered.</summary>
        DecodeError,

        /// <summary>The requested time is past the end of the source's window and Loop is off.</summary>
        EndOfSource,
    }

    /// <summary>How a source reports that it can't provide content.</summary>
    /// <remarks>Sources never draw placeholders themselves; they throw this and the caller decides by <see cref="Reason"/>.</remarks>
    /// <param name="reason">Why the content isn't available.</param>
    /// <param name="message">What went wrong, for logs and reports.</param>
    /// <param name="inner">The exception that caused this one, if any.</param>
    public sealed class SourceUnavailableException(SourceUnavailableReason reason, string message, Exception? inner = null)
        : Exception(message, inner)
    {
        /// <summary>Why the content isn't available.</summary>
        public SourceUnavailableReason Reason { get; } = reason;
    }
}
