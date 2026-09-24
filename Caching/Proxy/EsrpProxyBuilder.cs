using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Video;

namespace EditSharp.Caching.Proxy
{
    /// <summary>What a proxy build needs to know, resolved once by ProxyCache before any builder runs.</summary>
    internal sealed record ProxyBuildPlan(
        string SourcePath, string Hash, MediaInfo Info, ProxyFormat Format,
        int Width, int Height, double FrameRate, int TotalFrames, HardwareAccelerator HwAccel);

    /// <summary>
    /// Builds (or resumes) an .esrp proxy: one SourceDecoder pass at the
    /// source's own frame rate and proxy size, each frame encoded and handed
    /// to EsrpWriter, which makes it readable immediately. An IndexedDelta7
    /// build first samples frames spread across the whole source to build its
    /// one shared palette; a resumed build reuses the palette already in the
    /// file.
    /// </summary>
    internal static class EsrpProxyBuilder
    {
        private const int PaletteSampleFrames = 32;

        public static Task BuildAsync(ProxyBuildPlan plan, string path, Action<int> onFramesWritten, CancellationToken ct) => Task.Run(async () =>
        {
            DecodeHwAccelPlan decode = await FfmpegRunner.GetDecodePlanAsync(plan.SourcePath, plan.HwAccel);

            using EsrpWriter writer = TryResume(plan, path) ?? Create(plan, path, decode);
            onFramesWritten(writer.FramesWritten);

            if (writer.FramesWritten < writer.Header.Capacity)
            {
                double startSeconds = writer.FramesWritten / plan.FrameRate;

                using SourceDecoder decoder = SourceDecoder.Start(
                    plan.SourcePath, startSeconds, plan.FrameRate, plan.Width, plan.Height, decode);

                while (writer.FramesWritten < writer.Header.Capacity)
                {
                    ct.ThrowIfCancellationRequested();

                    using SKImage frame = decoder.NextFrame();
                    if (decoder.IsExhausted) break;

                    writer.Append(EncodeFrame(frame, writer.Header, writer.Palette));
                    onFramesWritten(writer.FramesWritten);
                }
            }

            writer.Complete();
        }, ct);

        /// <summary>An interrupted build of this same plan, reopened; or null (and the stale file removed) if there isn't a usable one.</summary>
        private static EsrpWriter? TryResume(ProxyBuildPlan plan, string path)
        {
            if (!File.Exists(path)) return null;

            try
            {
                (EsrpFormat.Header header, EsrpMeta meta) = EsrpReader.ReadHeaderAndMeta(path);

                bool matches = meta.SourceHash == plan.Hash && meta.SchemaVersion == ProxyCache.SchemaVersion &&
                    !header.Complete && header.Width == plan.Width && header.Height == plan.Height &&
                    header.PixelFormat == PixelFormatFor(plan.Format) && header.FrameRate == plan.FrameRate;

                if (matches)
                {
                    EsrpWriter writer = EsrpWriter.Resume(path);
                    EditSharpConfig.Logger.Log($"Resuming proxy for '{plan.SourcePath}' at frame {writer.FramesWritten}/{header.Capacity}.");
                    return writer;
                }
            }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogWarning($"Can't resume the proxy at '{path}', starting over: {ex.Message}");
            }

            File.Delete(path);
            return null;
        }

        private static EsrpWriter Create(ProxyBuildPlan plan, string path, DecodeHwAccelPlan decode)
        {
            EsrpPixelFormat pixelFormat = PixelFormatFor(plan.Format);

            byte[] palette = pixelFormat == EsrpPixelFormat.IndexedDelta7
                ? BuildPalette(plan, decode)
                : [];

            var meta = new EsrpMeta
            {
                SourceHash = plan.Hash,
                KnownSourcePaths = { Path.GetFullPath(plan.SourcePath) },
                OriginalWidth = plan.Info.Width,
                OriginalHeight = plan.Info.Height,
                MaxDimensionAtBuild = EditSharpConfig.ProxyMaxDimension,
                CreatedAtUtc = DateTime.UtcNow,
            };

            EditSharpConfig.Logger.Log(
                $"Building {plan.Format} proxy for '{plan.SourcePath}' ({plan.Width}x{plan.Height} @ {plan.FrameRate:0.###}fps, " +
                $"{plan.TotalFrames} frames).");

            return EsrpWriter.Create(
                path, plan.Width, plan.Height, pixelFormat, EditSharpConfig.EsrpCompressionScheme,
                plan.FrameRate, plan.TotalFrames, meta, palette);
        }

        public static EsrpPixelFormat PixelFormatFor(ProxyFormat format) => format switch
        {
            ProxyFormat.EsrpDelta7 => EsrpPixelFormat.IndexedDelta7,
            ProxyFormat.EsrpRgba => EsrpPixelFormat.Rgba8888,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not an .esrp format."),
        };

        /// <summary>
        /// One palette for the whole file, median-cut from a fixed number of
        /// frames spread evenly across the source; decoded at a low rate so a
        /// ten-minute source costs about the same to sample as a ten-second one.
        /// </summary>
        private static byte[] BuildPalette(ProxyBuildPlan plan, DecodeHwAccelPlan decode)
        {
            double seconds = Math.Max(plan.TotalFrames / plan.FrameRate, 1.0);
            double sampleRate = Math.Min(PaletteSampleFrames / seconds, PaletteSampleFrames);
            var histogram = new Dictionary<uint, int>();

            using (SourceDecoder sampler = SourceDecoder.Start(plan.SourcePath, 0, sampleRate, plan.Width, plan.Height, decode))
            {
                for (int i = 0; i < PaletteSampleFrames; i++)
                {
                    using SKImage sample = sampler.NextFrame();
                    if (sampler.IsExhausted) break;

                    using SKPixmap? pixmap = sample.PeekPixels();
                    if (pixmap is not null)
                        IndexedDelta7Codec.AccumulateHistogram(pixmap.GetPixelSpan(), plan.Width, plan.Height, histogram);
                }
            }

            byte[] palette = new byte[EsrpFormat.Delta7PaletteByteSize];
            IndexedDelta7Codec.BuildPaletteFromHistogram(histogram, palette);
            return palette;
        }

        private static byte[] EncodeFrame(SKImage frame, EsrpFormat.Header header, byte[] palette)
        {
            using SKPixmap pixmap = frame.PeekPixels()
                ?? throw new InvalidOperationException("Could not read a decoded frame's pixels while building a proxy.");

            ReadOnlySpan<byte> raw = pixmap.GetPixelSpan();

            if (header.PixelFormat == EsrpPixelFormat.IndexedDelta7)
            {
                byte[] codes = new byte[header.Width * header.Height];
                IndexedDelta7Codec.EncodeWithPalette(raw, header.Width, header.Height, palette, codes);
                return Compress(codes, header.CompressionScheme);
            }

            return Compress(raw, header.CompressionScheme);
        }

        private static byte[] Compress(ReadOnlySpan<byte> plane, EsrpCompressionScheme scheme) => scheme switch
        {
            EsrpCompressionScheme.None => plane.ToArray(),
            EsrpCompressionScheme.Zstd => EsrpZstd.Encode(plane),
            _ => throw new InvalidOperationException($"Unknown EsrpCompressionScheme '{scheme}'."),
        };
    }
}
