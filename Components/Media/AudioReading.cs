using System;
using EditSharp.Audio.Engine;

namespace EditSharp.Components.Media
{
    /// <summary>How one reader should read.</summary>
    /// <remarks>Readers always deliver content at 1x; retiming for Clip.Speed happens downstream. Converting to the requested format is the source's job, never the mixer's.</remarks>
    /// <param name="SampleRate">The sample rate samples come back at.</param>
    /// <param name="Channels">The channel count samples come back interleaved in.</param>
    /// <param name="StartAt">The content time (since the in-point, at 1x) of the first sample.</param>
    /// <param name="Session">The reading session, for kinds that mix other sources themselves (a nested timeline).</param>
    internal sealed record AudioReaderOptions(int SampleRate, int Channels, Time StartAt, AudioSession? Session = null);

    /// <summary>A source readied for one session; opens readers on demand and owns whatever they share.</summary>
    internal interface IPreparedAudioSource : IDisposable
    {
        /// <summary>Opens a reader.</summary>
        /// <param name="options">How the reader should read.</param>
        /// <returns>The reader; the caller disposes it.</returns>
        /// <exception cref="SourceUnavailableException">The reader can't be opened.</exception>
        IAudioSampleReader OpenReader(AudioReaderOptions options);
    }

    /// <summary>Reads samples from a prepared source, block by block.</summary>
    internal interface IAudioSampleReader : IDisposable
    {
        /// <summary>Fills a block with the next samples.</summary>
        /// <remarks>Fewer frames than requested means the source ended inside this block, and the next call throws with <see cref="SourceUnavailableReason.EndOfSource"/>. A looping source never ends.</remarks>
        /// <param name="destination">Where the samples go: interleaved, a whole number of frames long.</param>
        /// <returns>How many frames were written.</returns>
        /// <exception cref="SourceUnavailableException">The source has ended, or can't be read.</exception>
        int Read(Span<float> destination);
    }
}
