using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EditSharp.Components;
using EditSharp.Components.Clips;

namespace EditSharp.Audio.Engine
{
    /// <summary>A block of master audio, the timeline position its first frame came from, and how many timeline frames each of its frames covers.</summary>
    internal sealed record AudioBlock(float[] Samples, int Frames, Time Position, double Pace);

    /// <summary>
    /// Renders master audio on its own thread, staying at most
    /// EditSharpConfig.AudioLatency ahead of whoever takes the blocks (the
    /// sink): when the queue is full it waits, so edits are heard within that
    /// latency. Ends at the timeline's end going forward, at its start in
    /// reverse.
    /// </summary>
    internal sealed class AudioEngine : IDisposable
    {
        private readonly Timeline _timeline;
        private readonly AudioSession _session;
        private readonly MasterAudioStream _master;
        private readonly Channel<AudioBlock> _blocks;
        private readonly CancellationTokenSource _stop = new();
        private Thread? _thread;

        public AudioEngine(Timeline timeline, AudioSession session, Time start, double speed, PitchPreservation pitch)
        {
            _timeline = timeline;
            _session = session;
            _master = new MasterAudioStream(timeline, session, session.FrameOf(start), speed, pitch);

            Time block = Time.FromSamples(session.BlockFrames, session.Format.SampleRate);
            int capacity = Math.Max(2, (int)((EditSharpConfig.AudioLatency.Ticks + block.Ticks - 1) / block.Ticks));
            _blocks = Channel.CreateBounded<AudioBlock>(new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = true });
        }

        /// <summary>Prepares every source audible at the start (waiting for them), then starts rendering.</summary>
        public void Start()
        {
            _master.PrepareStart();
            _thread = new Thread(Run) { IsBackground = true, Name = "EditSharp-Audio", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        /// <summary>How fast blocks rendered from now on play, without the sign; blocks already queued keep their own.</summary>
        public void SetPace(double pace) => _master.Step = pace;

        /// <summary>The next block, or null once the timeline has run out.</summary>
        public async ValueTask<AudioBlock?> TakeAsync(CancellationToken ct)
        {
            try { return await _blocks.Reader.ReadAsync(ct); }
            catch (ChannelClosedException) { return null; }
        }

        private void Run()
        {
            int channels = _session.Format.Channels;
            long end = _session.FrameOf(_timeline.Duration);

            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    double position = _master.Position;
                    double pace = _master.Step;

                    //frames left before the timeline runs out in this direction
                    double remaining = _master.Forward ? (end - position) / pace : position / pace;
                    int frames = (int)Math.Min(_session.BlockFrames, Math.Ceiling(remaining));
                    if (frames <= 0) break;

                    var samples = new float[frames * channels];
                    _master.Read(samples, pace);

                    var block = new AudioBlock(samples, frames, _session.TimeOf((long)Math.Round(position)), pace);
                    _blocks.Writer.WriteAsync(block, _stop.Token).AsTask().GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                EditSharpConfig.Logger.LogError($"Audio engine stopped: {ex}");
            }
            finally
            {
                _blocks.Writer.TryComplete();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _thread?.Join();
            _master.Dispose();
            _stop.Dispose();
        }
    }
}
