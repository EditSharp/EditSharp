using EditSharp.Components.Media;
using EditSharp.Components;
using System;
using System.Collections.Generic;
using System.Threading;

namespace EditSharp.Compositing.Sources
{
    /// <summary>
    /// Decodes a reader's frames ahead of the playhead (behind it, in reverse)
    /// on its own thread, so compositing takes a finished frame instead of
    /// waiting on a decode. Keyed by TIMELINE frame index: the producer walks
    /// timeline frames in the session's direction, asks the wrapped reader for
    /// each one's content time, and queues the result; frame or failure.
    ///
    /// The wrapped reader must be opened with CallerOwnsFrames, and is only
    /// ever touched by the producer thread. Taken frames are held (and handed
    /// out again for a repeated request) until the next Take, then disposed.
    ///
    /// A request that isn't next in line (a seek, a loop in
    /// the timeline, an edit that changed the clip's timing so a queued
    /// frame's content time no longer matches) drops the queue and restarts
    /// production at the requested frame. Production stops at the edge of
    /// `inRange` (the clip plus whatever a transition can reach), except for
    /// a frame compositing is actually waiting on.
    /// </summary>
    internal sealed class BufferedVideoReader : IDisposable
    {
        private sealed class Entry(int frame, Time time)
        {
            public int Frame { get; } = frame;
            public Time Time { get; } = time;
            public VideoFrame Content { get; set; }
            public bool HasContent { get; set; }
            public SourceUnavailableException? Error { get; set; }

            public void Release()
            {
                if (HasContent && Content.Transient) Content.Image.Dispose();
                HasContent = false;
            }
        }

        private readonly IVideoFrameReader _inner;
        private readonly Func<int, Time> _timeAt;
        private readonly Func<int, bool> _inRange;
        private readonly int _direction;
        private readonly int _capacity;
        private readonly Thread _worker;

        private readonly object _lock = new();
        private readonly LinkedList<Entry> _queue = new();
        private Entry? _held;
        private int _next;
        private int? _inFlight;
        private int? _demand;
        private int _generation;
        private bool _disposed;

        public BufferedVideoReader(
            IVideoFrameReader inner, int firstFrame, int direction, int capacity,
            Func<int, Time> contentTimeAt, Func<int, bool> inRange)
        {
            _inner = inner;
            _next = firstFrame;
            _direction = direction >= 0 ? 1 : -1;
            _capacity = Math.Max(1, capacity);
            _timeAt = contentTimeAt;
            _inRange = inRange;

            _worker = new Thread(Produce) { IsBackground = true, Name = "EditSharp-FrameBuffer" };
            _worker.Start();
        }

        /// <summary>Waits up to `timeout` (infinite: Time.MaxValue) for `frame` to be ready.</summary>
        public bool WaitReady(int frame, Time time, Time timeout)
        {
            long deadline = timeout == Time.MaxValue ? long.MaxValue : Environment.TickCount64 + (long)Math.Ceiling(timeout.Milliseconds);

            lock (_lock)
            {
                if (Matches(_held, frame, time)) return true;

                while (true)
                {
                    Align(frame, time);
                    if (Matches(_queue.First?.Value, frame, time)) return true;
                    if (_disposed) return false;

                    Demand(frame);

                    long remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0) return false;

                    Monitor.Wait(_lock, (int)Math.Min(remaining, int.MaxValue));
                }
            }
        }

        /// <summary>
        /// The frame at timeline `frame` (content time `time`), waiting for it
        /// if necessary. Reader-held (non-transient): valid until the next Take.
        /// Throws the SourceUnavailableException the reader gave for it.
        /// </summary>
        public VideoFrame Take(int frame, Time time)
        {
            lock (_lock)
            {
                if (!Matches(_held, frame, time))
                {
                    WaitReady(frame, time, Time.MaxValue);
                    ObjectDisposedException.ThrowIf(_disposed, this);

                    _held?.Release();
                    _held = _queue.First!.Value;
                    _queue.RemoveFirst();
                    _demand = null;
                    Monitor.PulseAll(_lock);
                }

                if (_held!.Error is { } error) throw error;
                return new VideoFrame(_held.Content.Image, Transient: false);
            }
        }

        private static bool Matches(Entry? entry, int frame, Time time) =>
            entry is not null && entry.Frame == frame && entry.Time == time;

        //called under _lock: drop what's behind `frame`, restart if `frame` isn't coming next
        private void Align(int frame, Time time)
        {
            while (_queue.First is { } head && (frame - head.Value.Frame) * _direction > 0)
            {
                head.Value.Release();
                _queue.RemoveFirst();
            }

            Entry? first = _queue.First?.Value;

            //with nothing queued, the producer is either decoding something (in flight) or about to start on _next
            int upcoming = first?.Frame ?? _inFlight ?? _next;

            bool stale = first is not null && first.Frame == frame && first.Time != time;
            bool passed = (upcoming - frame) * _direction > 0;

            if (stale || passed) Restart(frame);

            Monitor.PulseAll(_lock);
        }

        //called under _lock. a demand ahead of what's queued and in flight
        //moves production straight to it: the frames between would only be
        //dropped on arrival, and a reader that can seek gets there at once
        private void Demand(int frame)
        {
            _demand = frame;

            bool queuedAhead = _queue.Last is { } last && (last.Value.Frame - frame) * _direction >= 0;
            bool inFlightIsIt = _inFlight == frame;
            if (!queuedAhead && !inFlightIsIt && (frame - _next) * _direction > 0) _next = frame;

            Monitor.PulseAll(_lock);
        }

        //called under _lock
        private void Restart(int frame)
        {
            _generation++;
            _inFlight = null;
            foreach (Entry entry in _queue) entry.Release();
            _queue.Clear();
            _next = frame;
        }

        private void Produce()
        {
            while (true)
            {
                int frame;
                int generation;

                lock (_lock)
                {
                    while (!_disposed && (_queue.Count >= _capacity || !(_inRange(_next) || _next == _demand)))
                        Monitor.Wait(_lock);

                    if (_disposed) return;

                    frame = _next;
                    _next += _direction;
                    _inFlight = frame;
                    generation = _generation;
                }

                var entry = new Entry(frame, _timeAt(frame));

                try
                {
                    entry.Content = _inner.GetFrame(entry.Time);
                    entry.HasContent = true;
                }
                catch (SourceUnavailableException ex)
                {
                    entry.Error = ex;
                }
                catch (Exception ex)
                {
                    entry.Error = new SourceUnavailableException(SourceUnavailableReason.DecodeError, "Reading a frame failed.", ex);
                }

                lock (_lock)
                {
                    if (generation != _generation || _disposed)
                    {
                        entry.Release();
                        continue;
                    }

                    _inFlight = null;
                    _queue.AddLast(entry);
                    Monitor.PulseAll(_lock);
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                Monitor.PulseAll(_lock);
            }

            //the producer may be mid-decode; the reader is only safe to dispose once it's out
            _worker.Join();

            lock (_lock)
            {
                foreach (Entry entry in _queue) entry.Release();
                _queue.Clear();
                _held?.Release();
                _held = null;
            }

            _inner.Dispose();
        }
    }
}
