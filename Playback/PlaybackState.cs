namespace EditSharp.Playback;

/// <summary>What a <see cref="Playback"/> is doing; it's in exactly one of these at a time.</summary>
/// <remarks><see cref="Playback.ScrubToAsync"/> only runs while not Playing; while its frame is composed the state is Scrubbing, then returns to what it was.</remarks>
public enum PlaybackState
{
    /// <summary>No play session: never played, stopped, or finished.</summary>
    Inactive,

    /// <summary>A play session is advancing.</summary>
    Playing,

    /// <summary>A play session is paused: everything it holds stays open, and nothing new is produced.</summary>
    Paused,

    /// <summary>A scrub is composing and delivering one frame.</summary>
    Scrubbing,
}
