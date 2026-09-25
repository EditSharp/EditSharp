using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditSharp.Caching.Proxy
{
    /// <summary>
    /// The JSON blob embedded in every .esrp file (see EsrpFormat): which
    /// original it was built from and at what settings. Progress is not
    /// here; it's the index itself (see EsrpFormat).
    /// </summary>
    internal sealed class EsrpMeta
    {
        public int SchemaVersion { get; set; } = ProxyCache.SchemaVersion;
        public string SourceHash { get; set; } = "";
        public List<string> KnownSourcePaths { get; set; } = new();
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        public int MaxDimensionAtBuild { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    internal static class EsrpMetaSerializer
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

        public static byte[] SerializeToUtf8Bytes(EsrpMeta meta) => JsonSerializer.SerializeToUtf8Bytes(meta, JsonOptions);

        public static EsrpMeta Deserialize(ReadOnlySpan<byte> utf8Json, string diagnosticPath)
        {
            try
            {
                return JsonSerializer.Deserialize<EsrpMeta>(utf8Json, JsonOptions)
                    ?? throw new InvalidDataException($"'{diagnosticPath}' has a null embedded meta blob.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"'{diagnosticPath}' has an unreadable embedded meta blob: {ex.Message}", ex);
            }
        }
    }
}
