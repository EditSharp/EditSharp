using System;

namespace EditSharp.Playback;

public enum PlaybackMode
{
    //ensure every frame is played, delaying audio if needed
    EveryFrame,
    //ensure target frame rate is hit, skipping audio/video if needed
    FrameDropping,
    //ensure frame rate matches the pace of audio, delaying frame delivery if needed
    SyncToAudio
}
