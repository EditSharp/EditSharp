using System;
 
namespace EditSharp.Playback
{
    /// <summary>
    /// One chunk (~100ms) of mixed timeline audio, raw s16le interleaved
    /// PCM at SampleRate/ChannelCount.
    ///
    /// Buffer OWNERSHIP: same contract as VideoFrameEventArgs — Buffer is
    /// reused by PlaybackAudioEngine on the very next chunk, so a subscriber
    /// that needs the samples past the handler returning (queuing onto an
    /// audio device buffer on another thread, etc.) MUST copy out of
    /// Buffer[..Length] synchronously.
    /// </summary>
    public sealed class AudioSampleEventArgs : EventArgs
    {
        public byte[] Buffer { get; }
        public int Length { get; }
        public int SampleRate { get; }
        public int ChannelCount { get; }
        public TimeSpan Position { get; }
 
        public AudioSampleEventArgs(byte[] buffer, int length, int sampleRate, int channelCount, TimeSpan position)
        {
            Buffer = buffer;
            Length = length;
            SampleRate = sampleRate;
            ChannelCount = channelCount;
            Position = position;
        }
    }
}
 