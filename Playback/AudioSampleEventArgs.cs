using System;

namespace EditSharp.Playback
{
    /// <summary>One block of mixed playback audio: interleaved 16-bit little-endian PCM.</summary>
    /// <remarks><see cref="Buffer"/> is reused for the next block, so copy <c>Buffer[..Length]</c> before the handler returns to keep the samples.</remarks>
    public sealed class AudioSampleEventArgs : EventArgs
    {
        /// <summary>The samples; valid only during the handler.</summary>
        public byte[] Buffer { get; }

        /// <summary>How many bytes of <see cref="Buffer"/> hold samples.</summary>
        public int Length { get; }

        /// <summary>Frames per second.</summary>
        public int SampleRate { get; }

        /// <summary>Samples per frame.</summary>
        public int ChannelCount { get; }

        /// <summary>The timeline time of the block's first frame.</summary>
        public Time Position { get; }

        /// <summary>Wraps one block of samples.</summary>
        /// <param name="buffer">The samples.</param>
        /// <param name="length">How many bytes of <paramref name="buffer"/> hold samples.</param>
        /// <param name="sampleRate">Frames per second.</param>
        /// <param name="channelCount">Samples per frame.</param>
        /// <param name="position">The timeline time of the first frame.</param>
        public AudioSampleEventArgs(byte[] buffer, int length, int sampleRate, int channelCount, Time position)
        {
            Buffer = buffer;
            Length = length;
            SampleRate = sampleRate;
            ChannelCount = channelCount;
            Position = position;
        }
    }
}
