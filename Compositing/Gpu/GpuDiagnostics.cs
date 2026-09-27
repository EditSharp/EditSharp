using System.Collections.Concurrent;
using System.Linq;

namespace EditSharp.Compositing.Gpu
{
    /// <summary>Counts for spotting GPU resources that outlive what made them.</summary>
    public static class GpuDiagnostics
    {
        private static readonly ConcurrentDictionary<object, string> Origins = new();

        /// <summary>GPU contexts made and not yet disposed, across every playback and cache.</summary>
        public static int LiveContexts => GpuContext.LiveCount;

        /// <summary>Whether each new context remembers the stack that made it, for <see cref="LiveOrigins"/>; off by default, since it's slow.</summary>
        public static bool TrackCreation { get; set; }

        /// <summary>The stacks that made the contexts still alive, while <see cref="TrackCreation"/> is on.</summary>
        public static string[] LiveOrigins => Origins.Values.ToArray();

        internal static void Created(object context, string stack) => Origins[context] = stack;

        internal static void Disposed(object context) => Origins.TryRemove(context, out _);
    }
}
