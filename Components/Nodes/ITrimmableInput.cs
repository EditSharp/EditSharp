using System;

namespace EditSharp.Components.Nodes
{
    /// <summary>An input with an in-point that moves when its clip's head is trimmed.</summary>
    /// <remarks>Trimming a clip's head moves every trimmable input in its graph by the same amount, limited by whichever has the least room.</remarks>
    public interface ITrimmableInput
    {
        /// <summary>How far into the content the clip starts.</summary>
        TimeSpan InPoint { get; set; }

        /// <summary>How far <see cref="InPoint"/> can move earlier; <see cref="TimeSpan.MaxValue"/> if there's no limit.</summary>
        TimeSpan MaxHeadroom { get; }

        /// <summary>How much content follows the in-point; null when there's no end, it loops, or it isn't known yet.</summary>
        TimeSpan? ContentLength { get; }
    }
}
