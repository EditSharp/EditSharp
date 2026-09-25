using System;
 
namespace EditSharp.Playback;
 
public enum PlaybackMode
{
    //ensure every frame is played 
    //delays audio to hit target
    EveryFrame,
    //ensure target frame rate is hit
    //skips audio/video to hit target
    FrameDropping,
    //ensure frame rate matches the pace of audio
    //delays/skips frames to hit target
    SyncToAudio
}
 