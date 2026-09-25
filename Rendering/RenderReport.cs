using EditSharp.Components.Media;
using EditSharp.Components;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EditSharp.Rendering;

/// <summary>The sources that failed during a render that still completed.</summary>
/// <remarks>A source that can't provide content is rendered as a labelled placeholder instead of stopping the export; this lists each one.</remarks>
/// <param name="Problems">One entry per source node and reason, earliest first.</param>
public sealed record RenderReport(IReadOnlyList<SourceProblem> Problems)
{
    /// <summary>Whether any source failed.</summary>
    public bool HasProblems => Problems.Count > 0;
}

/// <summary>One source node that failed for one reason during a render, summarised.</summary>
/// <param name="NodeId">The source node's id.</param>
/// <param name="Source">The source described for a person, such as "media-video: C:/clips/a.mp4".</param>
/// <param name="Reason">Why it failed.</param>
/// <param name="Message">The first failure's message.</param>
/// <param name="FirstAt">The timeline time of the first failure.</param>
/// <param name="LastAt">The timeline time of the last failure.</param>
/// <param name="Frames">How many frames or audio blocks failed.</param>
public sealed record SourceProblem(
    Guid NodeId,
    string Source,
    SourceUnavailableReason Reason,
    string Message,
    TimeSpan FirstAt,
    TimeSpan LastAt,
    int Frames);

//collects SourceProblems during a render, one per node and reason; thread-safe
internal sealed class RenderReportBuilder
{
    private readonly Dictionary<(Guid, SourceUnavailableReason), SourceProblem> _problems = new();
    private readonly object _lock = new();

    //records a failure; true the first time this node fails for this reason (worth a log line)
    public bool Record(Guid nodeId, string source, SourceUnavailableReason reason, string message, TimeSpan at)
    {
        lock (_lock)
        {
            if (_problems.TryGetValue((nodeId, reason), out SourceProblem? known))
            {
                _problems[(nodeId, reason)] = known with
                {
                    FirstAt = at < known.FirstAt ? at : known.FirstAt,
                    LastAt = at > known.LastAt ? at : known.LastAt,
                    Frames = known.Frames + 1,
                };
                return false;
            }

            _problems[(nodeId, reason)] = new SourceProblem(nodeId, source, reason, message, at, at, 1);
            return true;
        }
    }

    public RenderReport Build()
    {
        lock (_lock) return new RenderReport(_problems.Values.OrderBy(p => p.FirstAt).ToList());
    }
}
