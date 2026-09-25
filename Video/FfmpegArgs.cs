using EditSharp;
using System;
using System.Globalization;

namespace EditSharp.Video
{
    //number formatting for ffmpeg arguments, and the global filter options every call adds
    internal static class FfmpegArgs
    {
        public static string Sec(TimeSpan t) => Num(t.TotalSeconds);

        public static string Num(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

        public static double Clamp(double value, double min, double max) =>
            Math.Max(min, Math.Min(value, Math.Max(min, max)));

        //global options, so they go before any -i: filter thread counts (both knobs, since
        //-filter_complex_threads covers -filter_complex graphs and -filter_threads covers -vf)
        //and swscale's backend. See EditSharpConfig.FilterThreads and SwsBackends
        public static string[] FilterThreadingArgs()
        {
            string threads = Math.Max(1, EditSharpConfig.FilterThreads)
                .ToString(CultureInfo.InvariantCulture);

            return
            [
                "-filter_threads", threads,
                "-filter_complex_threads", threads,
                "-sws_backends", EditSharpConfig.SwsBackends,
            ];
        }
    }
}
