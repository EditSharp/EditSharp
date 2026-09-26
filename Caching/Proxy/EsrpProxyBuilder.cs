using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SkiaSharp;
using EditSharp.Video;

namespace EditSharp.Caching.Proxy
{
    /// <summary>What a proxy build needs to know, resolved once by ProxyCache before any builder runs.</summary>
    internal sealed record ProxyBuildPlan(
        string SourcePath, string Hash, MediaInfo Info, ProxyFormat Format,
        int Width, int Height, Rational FrameRate, int TotalFrames, HardwareAccelerator HwAccel);

    /// <summary>
    /// Builds (or resumes) an .esrp proxy: one SourceDecoder pass at the
    /// source's own frame rate and proxy size. Decoding runs on one thread,
    /// encoding and compression on every core, and frames are appended to
    /// the EsrpWriter strictly in order, each readable as soon as it lands. An
    /// IndexedDelta7 build first samples frames spread across the whole source
    /// to build its one shared palette; a resumed build reuses the palette
    /// already in the file.
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
                await EncodeRemainingAsync(plan, writer, decode, onFramesWritten, ct);

            writer.Complete();
        }, ct);

        private static async Task EncodeRemainingAsync(
            ProxyBuildPlan plan, EsrpWriter writer, DecodeHwAccelPlan decode, Action<int> onFramesWritten, CancellationToken ct)
        {
            EsrpFormat.Header header = writer.Header;
            IndexedDelta7Encoder? delta7 = header.PixelFormat == EsrpPixelFormat.IndexedDelta7 ? new IndexedDelta7Encoder(writer.Palette) : null;
            int workers = Environment.ProcessorCount;

            var decoded = Channel.CreateBounded<(int Index, byte[] Pixels)>(workers * 2);
            var encoded = new ConcurrentDictionary<int, byte[]>();
            using var landed = new SemaphoreSlim(0);

            //one decoder, in order
            Task produce = Task.Run(() =>
            {
                try
                {
                    using SourceDecoder decoder = SourceDecoder.Start(
                        plan.SourcePath, Time.FromFrame(writer.FramesWritten, plan.FrameRate), plan.FrameRate, plan.Width, plan.Height, decode);

                    for (int index = writer.FramesWritten; index < header.Capacity; index++)
                    {
                        using SKImage frame = decoder.NextFrame();
                        if (decoder.IsExhausted) break;

                        using SKPixmap pixmap = frame.PeekPixels()
                            ?? throw new InvalidOperationException("Could not read a decoded frame's pixels while building a proxy.");

                        decoded.Writer.WriteAsync((index, pixmap.GetPixelSpan().ToArray()), ct).AsTask().GetAwaiter().GetResult();
                    }

                    decoded.Writer.Complete();
                }
                catch (Exception ex)
                {
                    decoded.Writer.Complete(ex);
                }
            }, ct);

            //every core encodes
            Task encode = Parallel.ForEachAsync(
                decoded.Reader.ReadAllAsync(ct),
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                (item, _) =>
                {
                    encoded[item.Index] = EncodeFrame(item.Pixels, header, delta7);
                    landed.Release();
                    return ValueTask.CompletedTask;
                });

            //appended strictly in order
            int next = writer.FramesWritten;
            while (true)
            {
                while (encoded.TryRemove(next, out byte[]? stored))
                {
                    writer.Append(stored);
                    onFramesWritten(writer.FramesWritten);
                    next++;
                }

                if (encode.IsCompleted && !encoded.ContainsKey(next)) break;

                await Task.WhenAny(landed.WaitAsync(ct), encode);
            }

            await encode;
            await produce;
        }

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
                $"Building {plan.Format} proxy for '{plan.SourcePath}' ({plan.Width}x{plan.Height} @ {plan.FrameRate}fps, " +
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
            //PaletteSampleFrames over the source's length, at most that many per second
            Rational seconds = new Rational(plan.TotalFrames) / plan.FrameRate;
            if (seconds < Rational.One) seconds = Rational.One;
            Rational sampleRate = new Rational(PaletteSampleFrames) / seconds;
            var histogram = new Dictionary<uint, int>();

            using (SourceDecoder sampler = SourceDecoder.Start(plan.SourcePath, Time.Zero, sampleRate, plan.Width, plan.Height, decode))
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

        private static byte[] EncodeFrame(byte[] pixels, EsrpFormat.Header header, IndexedDelta7Encoder? delta7)
        {
            if (delta7 is null) return Compress(pixels, header.CompressionScheme);

            byte[] codes = new byte[header.Width * header.Height];
            delta7.Encode(pixels, header.Width, header.Height, codes);
            return Compress(codes, header.CompressionScheme);
        }

        private static byte[] Compress(ReadOnlySpan<byte> plane, EsrpCompressionScheme scheme) => scheme switch
        {
            EsrpCompressionScheme.None => plane.ToArray(),
            EsrpCompressionScheme.Zstd => EsrpZstd.Encode(plane),
            _ => throw new InvalidOperationException($"Unknown EsrpCompressionScheme '{scheme}'."),
        };
    }
}
