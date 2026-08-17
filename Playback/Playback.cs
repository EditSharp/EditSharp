using EditSharp.Components;
using EditSharp.Render;
using System;
using System.Collections.Generic;
using System.Text;

namespace EditSharp.Playback
{
    //audio-based playback for timelines
    public class Playback
    {
        //timeline to be played
        public required Timeline Timeline;

        //speed at which to play back the timeline (can be positive or negative)
        public float Speed = 1f;

        //how far along the playback is through the timeline
        public TimeSpan Position { get; private set; } = TimeSpan.Zero;


        public event EventHandler? AudioSample;

        //returns audio sample at playback position
        protected virtual void OnAudioSample(EventArgs e)
        {
            AudioSample?.Invoke(this, e);
        }

        public event EventHandler? VideoFrame;

        //returns a new video frame is when it is ready for playback
        protected virtual void OnVideoFrame(EventArgs e)
        {
            VideoFrame?.Invoke(this, e);
        }

        public event EventHandler? EndReached;

        //raised when either end of the timeline is reached
        //(i.e. beginning reached if playback is reversed)
        protected virtual void OnEndReached(EventArgs e)
        {
            EndReached?.Invoke(this, e);
        }

        public void Play()
        {
            //start playback

            //wait for pipes to open
            //start feeding audio and frames
        }

        public void Stop()
        {
            //stop playback
        }
    }
}
