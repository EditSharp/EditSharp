namespace EditSharp.Playback;

/// <summary>
/// The coarse, mutually-exclusive state a Playback instance is in at any
/// given moment — REPLACES the earlier `IsPlaying`/`IsPaused` boolean pair
/// (decided in conversation: "a Playback should only be able to be in one
/// of these states at a time," so a single enum is the correct shape, not
/// two independently-settable booleans a caller had to combine themselves
/// — e.g. `state == PlaybackState.Playing` instead of
/// `IsPlaying &amp;&amp; !IsPaused`).
///
/// Scrubbing is included as a real, first-class state rather than an
/// orthogonal flag layered on top of the other three: Playback.ScrubToAsync
/// is only ever allowed when State is NOT Playing (see that method's own
/// guard), and for the duration of composing/delivering one scrubbed-to
/// frame, State is Scrubbing — reverting back to whatever it was
/// immediately before (Inactive, or Paused if a play session was paused at
/// the time) once that single scrub render finishes, succeeds or fails.
/// </summary>
public enum PlaybackState
{
    /// <summary>No play session exists — never played, or Stop() was called (or natural end was reached).</summary>
    Inactive,

    /// <summary>A play session exists and is actively advancing (video/audio loop running, not paused).</summary>
    Playing,

    /// <summary>A play session exists but is paused — everything it owns stays alive, nothing new is produced.</summary>
    Paused,

    /// <summary>A ScrubToAsync call is currently composing and delivering one frame at an arbitrary position.</summary>
    Scrubbing,
}