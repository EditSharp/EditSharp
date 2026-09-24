namespace EditSharp.Components.Clips
{
    /// <summary>How a clip's audio keeps its pitch when its speed isn't 1x.</summary>
    public enum PitchPreservation
    {
        /// <summary>Varispeed: faster is higher, slower is lower, like tape.</summary>
        None,

        /// <summary>Waveform-similarity overlap-add. Cheap; clean on speech and most music.</summary>
        WSOLA,

        /// <summary>Phase vocoder with phase locking. Smoother on sustained, tonal material; softer transients.</summary>
        PhaseVocoder,
    }
}
