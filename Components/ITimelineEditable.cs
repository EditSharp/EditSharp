using System;
using EditSharp.Components.Channels;
using EditSharp.Components.Clips;

namespace EditSharp.Components
{
    /// <summary>
    /// The core behavior contract shared by Clip and LinkGroup — see the
    /// schema doc's "The core behavior rule" section.
    ///
    /// Called on a Clip, every member here affects only that clip, exactly
    /// like an unlinked clip's behavior always has, regardless of whether it
    /// belongs to a LinkGroup. Called on a LinkGroup, the same member
    /// coordinates the edit across every member, handling type/constraint
    /// mismatches internally. This is a universal contract — any operation
    /// added here in the future automatically gets that same scoping from
    /// both implementations, not a per-operation special case.
    ///
    /// Exact member shapes are this rewrite's own call (the schema doc
    /// explicitly deferred "exact ITimelineEditable signatures"); Split
    /// deliberately returns void rather than the new fragments — the
    /// encapsulation principle means a channel's Clips collection is the
    /// source of truth for what exists after a split, not a value handed
    /// back from the call that caused it.
    /// </summary>
    public interface ITimelineEditable
    {
        /// <summary>
        /// Overwrite semantics at the destination — see Placement Integrity
        /// in the schema doc. targetChannel defaults to the clip's current
        /// channel; a different, type-compatible channel is a cross-channel
        /// move.
        /// </summary>
        void Move(TimeSpan newStart, Channel? targetChannel = null);

        /// <summary>Same as Move, but destination-side neighbors shift later instead of being overwritten.</summary>
        void RippleMove(TimeSpan newStart, Channel? targetChannel = null);

        /// <summary>Shrinks from the head — never creates overlap, so there is no Ripple variant.</summary>
        void TrimStart(TimeSpan amount);

        /// <summary>Shrinks from the tail — never creates overlap, so there is no Ripple variant.</summary>
        void TrimEnd(TimeSpan amount);

        /// <summary>Grows from the head (Overwrite default) — can newly overlap a neighbor.</summary>
        void ExtendStart(TimeSpan amount);

        /// <summary>Grows from the tail (Overwrite default) — can newly overlap a neighbor.</summary>
        void ExtendEnd(TimeSpan amount);

        void RippleExtendStart(TimeSpan amount);

        void RippleExtendEnd(TimeSpan amount);

        /// <summary>
        /// Cuts at a point in time. On a Clip, splits just that clip (both
        /// halves keep the same LinkGroupId). On a LinkGroup, splits every
        /// member spanning the point and resolves into two groups — see the
        /// schema doc's Split section for the full DaVinci-Resolve-matching
        /// behavior.
        /// </summary>
        void Split(TimeSpan at);

        void Delete();
    }
}
