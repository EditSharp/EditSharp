using System;

namespace EditSharp.Playback;

/// <summary>What playback gives up when it can't keep up in real time.</summary>
public enum PlaybackMode
{
    /// <summary>Every video frame is shown; when video falls behind, playback slows down and audio waits with it.</summary>
    EveryFrame,

    /// <summary>Video keeps real time by skipping frames that would be late, and audio skips blocks that would be late.</summary>
    FrameDropping,

    /// <summary>Audio sets the pace; video shows the frame for audio's position, waiting or skipping to stay on it.</summary>
    SyncToAudio
}
