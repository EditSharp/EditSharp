using System;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components.Sources;

namespace EditSharp.Rendering;

/// <summary>
/// What went wrong with sources during a render that still completed. A
/// failing source is rendered as a labeled placeholder rather than aborting
/// the export; this is how the caller finds out.
/// </summary>
public sealed record RenderReport(IReadOnlyList<SourceProblem> Problems)
{
    public bool HasProblems => Problems.Count > 0;
}

/// <summary>
/// One source input (graph node) that failed for one reason, summarised:
/// where on the timeline it first and last happened and on how many frames.
/// Source describes it for a person ("media-video: C:/clips/a.mp4").
/// </summary>
public sealed record SourceProblem(
    Guid NodeId,
    string Source,
    SourceUnavailableReason Reason,
    string Message,
    TimeSpan FirstAt,
    TimeSpan LastAt,
    int Frames);

/// <summary>Collects SourceProblems during a render, one per node + reason; thread-safe.</summary>
internal sealed class RenderReportBuilder
{
    private readonly Dictionary<(Guid, SourceUnavailableReason), SourceProblem> _problems = new();
    private readonly object _lock = new();

    /// <summary>Records a failure; returns true the first time this node fails for this reason (worth a log line).</summary>
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
