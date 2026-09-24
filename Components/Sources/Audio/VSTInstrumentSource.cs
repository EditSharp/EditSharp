using System;
using System.Threading;
using System.Threading.Tasks;

namespace EditSharp.Components.Sources.Audio;

// EXAMPLE CODE: DOES NOTHING; the bare shape of an audio kind
[SourceKind("vst-instrument")]
public class VSTInstrumentSource : AudioSource
{
    public override VSTInstrumentSource Duplicate() => (VSTInstrumentSource)base.Duplicate();

    public override Task<TimeSpan?> GetNaturalLengthAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    internal override Task<IPreparedAudioSource> PrepareAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}
