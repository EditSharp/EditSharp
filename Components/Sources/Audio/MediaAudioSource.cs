using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Editing;
using EditSharp.History;
using EditSharp.Video;

namespace EditSharp.Components.Sources.Audio;

/// <summary>
/// The audio stream of a media file on disk (an audio file, or a video file's
/// soundtrack), streamed through ffmpeg. A file with no audio stream reads as
/// silence for its length.
/// </summary>
[SourceKind("media-audio", DisplayName = "Media")]
public class MediaAudioSource : AudioSource, IFileBackedSource
{
    //the directory path to the file
    string _path = "";
    [Editable("File", Editor = PropertyEditor.Path)]
    public required string Path { get => _path; set => Transaction.Set(this, ref _path, value, static (o, v) => o._path = v); }

    string IFileBackedSource.FilePath => Path;

    public override MediaAudioSource Duplicate() => (MediaAudioSource)base.Duplicate();

    //from the probe cache; a file not probed yet starts its probe and reads as unknown
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

    /// <summary>The trimmed window in file time, through the live Start/Duration; see Source.ResolveWindow.</summary>
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
