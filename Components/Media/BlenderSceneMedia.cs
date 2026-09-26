using System;
using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Components.Media
{
    //an example that does nothing: the bare shape of a video kind that isn't a plain file
    [MediaKind("blender-scene", DisplayName = "Blender scene", Listed = false)]
    internal class BlenderSceneMedia : VideoMedia
    {
        //a scene has no soundtrack
        public BlenderSceneMedia() => Audio = null;

        public override BlenderSceneMedia Duplicate() => (BlenderSceneMedia)base.Duplicate();

        public override Task<Time?> GetNaturalLengthAsync(CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        internal override Task<IPreparedVideoSource> PrepareAsync(VideoPrepareContext context, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
