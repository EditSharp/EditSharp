using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace EditSharp.Components.Sources
{
    /// <summary>
    /// Animatable&lt;T&gt; as its static value plus, when it has a track, every
    /// keyframe exactly as it was: time, value, interpolation, tangent modes
    /// and handles.
    /// </summary>
    internal sealed class AnimatableJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Animatable<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(AnimatableJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }

    internal sealed class AnimatableJsonConverter<T> : JsonConverter<Animatable<T>>
    {
        public override void Write(Utf8JsonWriter writer, Animatable<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            JsonSerializer.Serialize(writer, value.StaticValue, options);

            if (value.Track is { } track)
            {
                writer.WriteStartArray("keyframes");
                foreach (Keyframe<T> keyframe in track.Keyframes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("start", keyframe.Start.ToString("c", CultureInfo.InvariantCulture));
                    writer.WritePropertyName("value");
                    JsonSerializer.Serialize(writer, keyframe.Value, options);
                    writer.WriteString("in", keyframe.InInterpolation.ToString());
                    writer.WriteString("out", keyframe.OutInterpolation.ToString());
                    writer.WriteString("inTangent", keyframe.InTangentMode.ToString());
                    writer.WriteString("outTangent", keyframe.OutTangentMode.ToString());
                    WriteHandle(writer, "inHandle", keyframe.InHandle, options);
                    WriteHandle(writer, "outHandle", keyframe.OutHandle, options);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        private static void WriteHandle(Utf8JsonWriter writer, string name, KeyframeHandle<T>? handle, JsonSerializerOptions options)
        {
            if (handle is null) return;

            writer.WriteStartObject(name);
            writer.WriteString("time", handle.TimeOffset.ToString("c", CultureInfo.InvariantCulture));
            writer.WritePropertyName("value");
            JsonSerializer.Serialize(writer, handle.ValueOffset, options);
            writer.WriteEndObject();
        }

        public override Animatable<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;

            var animatable = new Animatable<T>(root.GetProperty("value").Deserialize<T>(options)!);
            if (!root.TryGetProperty("keyframes", out JsonElement keyframes)) return animatable;

            KeyframeTrack<T> track = animatable.GetOrCreateTrack();
            var added = new List<(Keyframe<T> Keyframe, JsonElement Json)>();

            foreach (JsonElement json in keyframes.EnumerateArray())
            {
                TimeSpan start = TimeSpan.ParseExact(json.GetProperty("start").GetString()!, "c", CultureInfo.InvariantCulture);
                added.Add((track.AddKeyframe(start, json.GetProperty("value").Deserialize<T>(options)!), json));
            }

            //only once every keyframe is in: adding one recomputes its neighbours' automatic handles
            foreach ((Keyframe<T> keyframe, JsonElement json) in added)
            {
                keyframe.InInterpolation = Enum.Parse<InterpolationType>(json.GetProperty("in").GetString()!);
                keyframe.OutInterpolation = Enum.Parse<InterpolationType>(json.GetProperty("out").GetString()!);
                keyframe.InTangentMode = Enum.Parse<TangentMode>(json.GetProperty("inTangent").GetString()!);
                keyframe.OutTangentMode = Enum.Parse<TangentMode>(json.GetProperty("outTangent").GetString()!);
                keyframe.InHandle = ReadHandle(json, "inHandle", options);
                keyframe.OutHandle = ReadHandle(json, "outHandle", options);
            }

            return animatable;
        }

        private static KeyframeHandle<T>? ReadHandle(JsonElement json, string name, JsonSerializerOptions options) =>
            json.TryGetProperty(name, out JsonElement handle)
                ? new KeyframeHandle<T>(
                    TimeSpan.ParseExact(handle.GetProperty("time").GetString()!, "c", CultureInfo.InvariantCulture),
                    handle.GetProperty("value").Deserialize<T>(options)!)
                : null;
    }

    /// <summary>SKColor as "#AARRGGBB".</summary>
    internal sealed class SKColorJsonConverter : JsonConverter<SKColor>
    {
        public override SKColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            SKColor.TryParse(reader.GetString(), out SKColor color) ? color : throw new JsonException($"'{reader.GetString()}' is not a colour.");

        public override void Write(Utf8JsonWriter writer, SKColor value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    /// <summary>Vector2 as {"x", "y"}; its X and Y are fields, which the serializer otherwise skips.</summary>
    internal sealed class Vector2JsonConverter : JsonConverter<Vector2>
    {
        public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            return new Vector2(root.GetProperty("x").GetSingle(), root.GetProperty("y").GetSingle());
        }

        public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.X);
            writer.WriteNumber("y", value.Y);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// A Timeline saved as its Id only. Loading hands the id to the resolver
    /// passed to SourceSerializer.Deserialize; a timeline it can't find fails
    /// the load rather than inventing an empty one.
    /// </summary>
    internal sealed class TimelineReferenceJsonConverter : JsonConverter<Timeline>
    {
        [ThreadStatic] internal static Func<Guid, Timeline?>? Resolver;

        public override Timeline Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Guid id = reader.GetGuid();
            return Resolver?.Invoke(id) ?? throw new JsonException($"Timeline {id} is referenced but wasn't supplied to the loader.");
        }

        public override void Write(Utf8JsonWriter writer, Timeline value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Id);
    }
}
