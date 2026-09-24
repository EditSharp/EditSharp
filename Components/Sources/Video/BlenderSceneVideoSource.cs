using System;
using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Components.Sources.Video;

// EXAMPLE CODE: DOES NOTHING; the bare shape of a video kind
[SourceKind("blender-scene", DisplayName = "Blender scene", Listed = false)]
public class BlenderSceneVideoSource : VideoSource
{
    public override BlenderSceneVideoSource Duplicate() => (BlenderSceneVideoSource)base.Duplicate();

    public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    internal override Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}
