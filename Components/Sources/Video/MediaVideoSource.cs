using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Caching.Proxy;
using EditSharp.Editing;
using EditSharp.History;
using EditSharp.Video;

namespace EditSharp.Components.Sources.Video;

/// <summary>A video file or a still image on disk.</summary>
/// <remarks>
/// Which one it is comes from probing the file, and isn't saved. A still has no
/// natural length and no proxy, and is decoded once per session. A video reads
/// its proxy or the original according to the session's <see cref="SourceMode"/>;
/// random access always reads the proxy.
/// </remarks>
[SourceKind("media-video", DisplayName = "Media")]
public class MediaVideoSource : VideoSource, IFileBackedSource, IPropertyDefaults
{
    string _path = "";
    /// <summary>The full path of the file.</summary>
    [Editable("File", Editor = PropertyEditor.Path)]
    public required string Path { get => _path; set { Transaction.Set(this, ref _path, value, static (o, v) => o._path = v); EndMayHaveMoved(); } }

    string IFileBackedSource.FilePath => Path;

    /// <inheritdoc/>
    public override MediaVideoSource Duplicate() => (MediaVideoSource)base.Duplicate();

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

        if (info.IsStillImage) return true;
        length = info.Duration;
        return length is not null;
    }

    /// <summary>How long the video is, from probing the file.</summary>
    /// <param name="ct">Cancels waiting for the probe.</param>
    /// <returns>The video's length; null for a still image.</returns>
    /// <exception cref="SourceUnavailableException"><see cref="SourceUnavailableReason.MediaOffline"/> when no file is chosen or it's missing; <see cref="SourceUnavailableReason.DecodeError"/> when it can't be probed or has no duration.</exception>
    public override async Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default)
    {
        MediaInfo info = await ProbeAsync(Path, ct);

        if (info.IsStillImage) return null;

        return info.Duration
            ?? throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"'{Path}' has no readable duration.");
    }

    internal override async Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default)
    {
        string path = Path;
        MediaInfo info = await ProbeAsync(path, ct);

        if (!info.HasVideo)
            throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"'{path}' has no video stream.");

        if (info.IsStillImage)
            return new PreparedMediaImage(this, LoadImage(path), (info.Width, info.Height));

        if (info.Duration is null)
            throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"'{path}' has no readable duration.");

        //random access reads the proxy in every mode, so look for one whatever the mode
        ProxyEntry? proxy = await TryGetProxyAsync(path, ct);

        DecodeHwAccelPlan plan = context.Mode == SourceMode.ProxiesOnly
            ? DecodeHwAccelPlan.Software
            : await FfmpegRunner.GetDecodePlanAsync(path, context.HwAccel);

        return new PreparedMediaVideo(this, path, info, plan, context.Mode, proxy);
    }

    /// <inheritdoc/>
    /// <remarks>A still image is read directly in every mode, since it has no proxy.</remarks>
    public override async Task<SKImage> GetFrameAtAsync(
        TimeSpan contentTime, SourceMode mode = SourceMode.SourceOnly, int maxWidth = 0, int maxHeight = 0,
        CancellationToken ct = default)
    {
        MediaInfo info = await ProbeAsync(Path, ct);

        //stills have no proxy: every mode reads the image itself
        if (!info.IsStillImage) return await base.GetFrameAtAsync(contentTime, mode, maxWidth, maxHeight, ct);

        MapTime(contentTime, null);
        return LoadImage(Path);
    }

    internal override void AddFingerprint(ref HashCode hash)
    {
        base.AddFingerprint(ref hash);
        hash.Add(Path);
    }

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

    //a failed lookup just means no proxy yet; readers keep checking for one appearing
    private static async Task<ProxyEntry?> TryGetProxyAsync(string path, CancellationToken ct)
    {
        try
        {
            return await ProxyCache.TryGetAsync(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EditSharpConfig.Logger.LogWarning($"Proxy lookup failed for '{path}': {ex.Message}");
            return null;
        }
    }

    internal static SKImage LoadImage(string path)
    {
        using SKData? data = SKData.Create(path);

        if (data is null)
            throw new SourceUnavailableException(
                File.Exists(path) ? SourceUnavailableReason.DecodeError : SourceUnavailableReason.MediaOffline,
                $"Could not read '{path}'.");

        return SKImage.FromEncodedData(data)
            ?? throw new SourceUnavailableException(SourceUnavailableReason.DecodeError, $"Could not decode image '{path}'.");
    }
}
