using System;
using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Components.Media
{
    //an example that does nothing: the bare shape of an audio kind that isn't a plain file
    [MediaKind("vst-instrument", DisplayName = "VST instrument", Listed = false)]
    internal class VstInstrumentMedia : AudioMedia
    {
        public override VstInstrumentMedia Duplicate() => (VstInstrumentMedia)base.Duplicate();

        public override Task<Time?> GetNaturalLengthAsync(CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        internal override Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
