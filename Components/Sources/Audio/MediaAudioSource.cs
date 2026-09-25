using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Editing;
using EditSharp.History;
using EditSharp.Video;

namespace EditSharp.Components.Sources.Audio;

/// <summary>The audio of a media file on disk: an audio file, or a video file's soundtrack.</summary>
/// <remarks>It's streamed through ffmpeg. A file with no audio stream plays silence for its length.</remarks>
[SourceKind("media-audio", DisplayName = "Media")]
public class MediaAudioSource : AudioSource, IFileBackedSource, IPropertyDefaults
{
    string _path = "";
    /// <summary>The full path of the file.</summary>
    [Editable("File", Editor = PropertyEditor.Path)]
    public required string Path { get => _path; set { Transaction.Set(this, ref _path, value, static (o, v) => o._path = v); EndMayHaveMoved(); } }

    string IFileBackedSource.FilePath => Path;

    /// <inheritdoc/>
    public override MediaAudioSource Duplicate() => (MediaAudioSource)base.Duplicate();

    /// <inheritdoc/>
    /// <remarks>Duration resets to the file's length once it's probed.</remarks>
    public bool TryGetDefault(string propertyName, out object? value)
    {
        value = null;
        if (propertyName != nameof(Duration) || !TryGetNaturalLength(out TimeSpan? length) || length is null) return false;

        value = length;
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Answers from the probe cache; a file not probed yet starts its probe in the background.</remarks>
    public override bool TryGetNaturalLength(out TimeSpan? length)
    {
        length = null;

        if (!MediaProbe.TryGetCached(Path, out MediaInfo info))
        {
            if (File.Exists(Path)) _ = MediaProbe.ProbeCachedAsync(Path).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            return false;
        }

        length = info.Duration;
        return true;
    }

    /// <summary>How long the file is, from probing it.</summary>
    /// <param name="ct">Cancels waiting for the probe.</param>
    /// <returns>The file's length; null if it has none.</returns>
    /// <exception cref="SourceUnavailableException"><see cref="SourceUnavailableReason.MediaOffline"/> when no file is chosen or it's missing; <see cref="SourceUnavailableReason.DecodeError"/> when it can't be probed.</exception>
    public override async Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default) =>
        (await ProbeAsync(Path, ct)).Duration;

    internal override async Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default)
    {
        string path = Path;
        MediaInfo info = await ProbeAsync(path, ct);
        return new PreparedMediaAudio(this, path, info);
    }

    internal override void AddFingerprint(ref HashCode hash)
    {
        base.AddFingerprint(ref hash);
        hash.Add(Path);
    }

    //the trimmed window in file time, through the live Start/Duration; see Source.ResolveWindow
    internal (TimeSpan Start, TimeSpan? Length) Window(TimeSpan? naturalLength) => ResolveWindow(naturalLength);

    private static async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path))
            throw new SourceUnavailableException(SourceUnavailableReason.MediaOffline, "No file is chosen.");

        try
        {
            return await MediaProbe.ProbeCachedAsync(path).WaitAsync(ct);
        }
        catch (FileNotFoundException ex)
        {
            throw new SourceUnavailableException(SourceUnavailableReason.MediaOffline, $"'{path}' is missing.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"Could not probe '{path}'.", ex);
        }
    }
}
