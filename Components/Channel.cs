using EditSharp.Components.Clips;
using EditSharp.Components.Transitions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EditSharp.Components
{
    public class Channel
    {
        public string Name = "Channel";

        //how this channel combines with everything beneath it
        public ChannelBlendMode BlendMode { get; set; } = ChannelBlendMode.SrcOver;

        //how loud the channel's audio should be
        public float Volume { get; set; } = 1f;

        //clips of the channel, keyed by their own Start.
        //INVARIANT: no two clips overlap, and every key equals its clip's Start.
        //Clip.Start is publicly settable, so mutating it directly leaves the key
        //stale — go through AppendClip/InsertClip rather than editing in place
        public SortedDictionary<TimeSpan, Clip> Clips = [];

        // (clip to transition out of, Transition to use)
        public List<(Clip, Transition)> Transitions { get; set; } = [];

        //the time at which the last clip in this channel ends
        public TimeSpan End => Clips.Count == 0 ? TimeSpan.Zero : Clips.Values.Max(c => c.End);

        //adds clip to the end of the channel
        public void AppendClip(Clip clip)
        {
            clip.Start = End;
            Clips.Add(clip.Start, clip);
        }

        /// <summary>
        /// Places a clip at its own Start, making room by trimming, splitting, or
        /// removing whatever it lands on. Overlaps are not representable, so every
        /// clip already present is reduced to whatever part of it the new clip
        /// doesn't cover.
        ///
        /// Four mutually exclusive cases, decided by strict comparisons so that a
        /// clip merely touching an edge is never treated as an overlap:
        ///   - covered entirely      -> removed
        ///   - covered in the middle  -> split into two fragments
        ///   - overlapped at its head -> head trimmed, end stays put
        ///   - overlapped at its tail -> tail trimmed, start stays put
        ///
        /// The actual trimming lives on Clip, so media in-points and keyframe
        /// times are corrected consistently no matter which case fires.
        /// </summary>
        public void InsertClip(Clip clip)
        {
            SortedDictionary<TimeSpan, Clip> result = [];
            List<Clip> discarded = [];

            foreach (Clip target in Clips.Values)
            {
                //untouched — including clips merely adjacent to the new one,
                //whose intersection is exactly zero
                if (target.IntersectionWith(clip) <= TimeSpan.Zero)
                {
                    result.Add(target.Start, target);
                    continue;
                }

                bool startsBefore = target.Start < clip.Start;
                bool endsAfter = target.End > clip.End;

                //new clip covers this one entirely -> it doesn't survive
                if (!startsBefore && !endsAfter)
                {
                    discarded.Add(target);
                }
                //new clip lands strictly inside this one -> split into head + tail.
                //both fragments are duplicates, so the original clip's identity is
                //gone and anything referencing it (a transition) is dropped below
                else if (startsBefore && endsAfter)
                {
                    (Clip head, Clip tail) = target.SplitAround(clip.Start, clip.End);

                    result.Add(head.Start, head);
                    result.Add(tail.Start, tail);
                    discarded.Add(target);
                }
                //overlapped at its head -> trim the front, End stays where it is
                else if (endsAfter)
                {
                    target.TrimStart(clip.End - target.Start);
                    result.Add(target.Start, target);
                }
                //overlapped at its tail -> trim the back, Start stays where it is
                else
                {
                    target.TrimEnd(target.End - clip.Start);
                    result.Add(target.Start, target);
                }
            }

            //added last, once every surviving clip has been re-keyed to its new
            //Start — adding first would collide with any clip whose key is still
            //the stale pre-trim value
            result.Add(clip.Start, clip);
            Clips = result;

            //a transition belongs to a specific clip. if that clip was removed, or
            //replaced by the two fragments of a split, the transition has nothing
            //left to transition out of
            Transitions.RemoveAll(t => discarded.Contains(t.Item1));
        }

        /// <summary>
        /// Removes a clip outright, leaving a gap where it was. Any transition
        /// belonging to it goes too.
        /// </summary>
        public bool RemoveClip(Clip clip)
        {
            if (!Clips.Remove(clip.Start)) return false;

            Transitions.RemoveAll(t => ReferenceEquals(t.Item1, clip));
            return true;
        }
    }
}
