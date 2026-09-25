using System;
using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Components.Sources.Video;

//an example that does nothing: the bare shape of a video kind
[SourceKind("blender-scene", DisplayName = "Blender scene", Listed = false)]
internal class BlenderSceneVideoSource : VideoSource
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
