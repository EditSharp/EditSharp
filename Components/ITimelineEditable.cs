using System;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;

namespace EditSharp.Components
{
    /// <summary>Timeline edits that work on one clip or on a linked group of clips.</summary>
    /// <remarks>On a <see cref="Clips.Clip"/>, an edit affects only that clip, linked or not. On a <see cref="LinkGroup"/>, it's applied to every member together, limited by whichever member has the least room.</remarks>
    public interface ITimelineEditable
    {
        /// <summary>Moves to a new start, overwriting whatever is there.</summary>
        /// <param name="newStart">The new start; for a group, where its earliest member goes, the rest keeping their offsets.</param>
        /// <param name="targetChannel">The channel to move to; null stays on the current one.</param>
        /// <exception cref="InvalidOperationException">A clip isn't placed.</exception>
        /// <exception cref="ArgumentException"><paramref name="targetChannel"/> holds the other kind of clip.</exception>
        void Move(TimeSpan newStart, Channel? targetChannel = null);

        /// <summary>Moves to a new start, moving clips from there on later instead of overwriting them.</summary>
        /// <param name="newStart">The new start; for a group, where its earliest member goes, the rest keeping their offsets.</param>
        /// <param name="targetChannel">The channel to move to; null stays on the current one.</param>
        /// <exception cref="InvalidOperationException">A clip isn't placed.</exception>
        /// <exception cref="ArgumentException"><paramref name="targetChannel"/> holds the other kind of clip.</exception>
        void RippleMove(TimeSpan newStart, Channel? targetChannel = null);

        /// <summary>Shortens from the start; the content stays where it is on the timeline.</summary>
        /// <param name="amount">How much to trim; it stops at <see cref="Clips.Clip.MinimumDuration"/>.</param>
        void TrimStart(TimeSpan amount);

        /// <summary>Shortens from the end.</summary>
        /// <param name="amount">How much to trim; it stops at <see cref="Clips.Clip.MinimumDuration"/>.</param>
        void TrimEnd(TimeSpan amount);

        /// <summary>Lengthens from the start, overwriting whatever it grows into.</summary>
        /// <param name="amount">How much to extend; it stops where a source runs out (<see cref="Clips.Clip.HeadExtendLimit"/>).</param>
        /// <exception cref="InvalidOperationException">A clip isn't placed.</exception>
        void ExtendStart(TimeSpan amount);

        /// <summary>Lengthens from the end, overwriting whatever it grows into.</summary>
        /// <param name="amount">How much to extend; it stops where a source runs out (<see cref="Clips.Clip.TailExtendLimit"/>).</param>
        /// <exception cref="InvalidOperationException">A clip isn't placed.</exception>
        void ExtendEnd(TimeSpan amount);

        /// <summary>Lengthens from the start; clips from the new start on move later by the same amount instead of being overwritten.</summary>
        /// <param name="amount">How much to extend; it stops where a source runs out (<see cref="Clips.Clip.HeadExtendLimit"/>).</param>
        /// <exception cref="InvalidOperationException">A clip isn't placed.</exception>
        void RippleExtendStart(TimeSpan amount);

        /// <summary>Lengthens from the end; clips after it move later by the same amount instead of being overwritten.</summary>
        /// <param name="amount">How much to extend; it stops where a source runs out (<see cref="Clips.Clip.TailExtendLimit"/>).</param>
        /// <exception cref="InvalidOperationException">A clip isn't placed.</exception>
        void RippleExtendEnd(TimeSpan amount);

        /// <summary>Cuts in two at a timeline time.</summary>
        /// <remarks>A clip's halves keep its name and link group. A group splits every member that spans the time; members from the time on form a new group, and a side left with one clip is unlinked.</remarks>
        /// <param name="at">Where to cut; for a single clip, strictly inside it.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="at"/> isn't strictly inside the clip.</exception>
        /// <exception cref="InvalidOperationException">A clip isn't placed.</exception>
        void Split(TimeSpan at);

        /// <summary>Removes from the channel, leaving a gap.</summary>
        /// <remarks>A link group left with one member is unlinked.</remarks>
        /// <exception cref="InvalidOperationException">A clip isn't placed.</exception>
        void Delete();
    }
}
