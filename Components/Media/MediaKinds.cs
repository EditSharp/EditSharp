using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EditSharp.Components.Media
{
    /// <summary>A kind of media a picker can offer.</summary>
    /// <param name="Id">The id saved files contain.</param>
    /// <param name="DisplayName">The kind's name in pickers.</param>
    /// <param name="Type">The kind's class.</param>
    public sealed record MediaKindInfo(string Id, string DisplayName, Type Type);

    /// <summary>The media kinds pickers offer.</summary>
    public static class MediaKinds
    {
        private static readonly IReadOnlyList<MediaKindInfo> All = [.. ComponentSerializer.MediaKinds.Select(k =>
        {
            MediaKindAttribute attribute = k.Type.GetCustomAttribute<MediaKindAttribute>()!;
            return (Info: new MediaKindInfo(k.Id, attribute.DisplayName ?? k.Type.Name, k.Type), attribute.Listed);
        })
        .Where(k => k.Listed)
        .Select(k => k.Info)
        .OrderBy(k => k.DisplayName, StringComparer.CurrentCulture)];

        /// <summary>The listed kinds that can stand in for a media type.</summary>
        /// <param name="baseType">The type the media must be, such as <see cref="VideoMedia"/> or <see cref="AudioMedia"/>.</param>
        /// <returns>The kinds, by name.</returns>
        public static IReadOnlyList<MediaKindInfo> For(Type baseType) => [.. All.Where(k => baseType.IsAssignableFrom(k.Type))];

        /// <summary>The kind a media is, listed or not.</summary>
        /// <param name="media">The media.</param>
        /// <returns>Its kind; null for a class without a [MediaKind].</returns>
        public static MediaKindInfo? Of(IMedia media) =>
            All.FirstOrDefault(k => k.Type == media.GetType())
            ?? (media.GetType().GetCustomAttribute<MediaKindAttribute>() is { } a ? new MediaKindInfo(a.Id, a.DisplayName ?? media.GetType().Name, media.GetType()) : null);
    }
}
