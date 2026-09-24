using System;
using EditSharp.Audio.Engine;

namespace EditSharp.Components.Sources.Audio
{
    /// <summary>
    /// How one reader should read. Samples come back interleaved at exactly
    /// SampleRate/Channels; converting is the source's job, never the mixer's.
    /// StartAt is the content time (since the in-point, at 1x) of the first
    /// sample. Readers always deliver 1x content; retiming for Clip.Speed
    /// happens downstream.
    /// Session is the reading session, for kinds that render other sources
    /// themselves (a nested timeline).
    /// </summary>
    internal sealed record AudioReaderOptions(int SampleRate, int Channels, TimeSpan StartAt, AudioSession? Session = null);

    /// <summary>A source readied for one session; opens readers on demand and owns whatever they share.</summary>
    internal interface IPreparedAudioSource : IDisposable
    {
        IAudioSampleReader OpenReader(AudioReaderOptions options);
    }

    internal interface IAudioSampleReader : IDisposable
    {
        /// <summary>
        /// Fills `destination` (interleaved, a whole number of frames) with the
        /// next samples and returns how many frames were written. Fewer than
        /// requested means the source ended inside this block; the NEXT call
        /// then throws SourceUnavailableException(EndOfSource). Looping sources
        /// never end. Other failures throw with their own reason.
        /// </summary>
        int Read(Span<float> destination);
    }
}
