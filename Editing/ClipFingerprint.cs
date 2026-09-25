using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Sources;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;

namespace EditSharp.Editing
{
    /// <summary>A number that changes when, and only when, a clip would render differently.</summary>
    /// <remarks>
    /// It covers the clip's speed, the shape of its graph and every value in it,
    /// found through <see cref="EditableAttribute"/> metadata plus every node's
    /// keyframed values. It leaves out where the clip sits on the timeline and
    /// its head in-point: a head trim moves the in-point and slides every
    /// keyframe by the same amount, so keyframe times are hashed against
    /// <see cref="Anchor"/>, where they stay put. A nested timeline hashes as its
    /// reference, not its contents. Thumbnail caches compare fingerprints to
    /// find out which clips changed.
    /// </remarks>
    public static class ClipFingerprint
    {
        private const int MaxDepth = 6;

        /// <summary>The clip's fingerprint.</summary>
        /// <param name="clip">The clip.</param>
        /// <returns>A hash that changes whenever the clip's rendered content would.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="clip"/> is null.</exception>
        public static int Of(Clip clip)
        {
            ArgumentNullException.ThrowIfNull(clip);

            var hash = new HashCode();
            hash.Add(clip.Speed);

            TimeSpan anchor = Anchor(clip);
            AddGraph(ref hash, clip.Graph, anchor, 0);

            return hash.ToHashCode();
        }

        /// <summary>The head in-point the clip's content is anchored to.</summary>
        /// <param name="clip">The clip.</param>
        /// <returns>The first trimmable input's in-point (they all shift together), or zero when the clip has none.</returns>
        public static TimeSpan Anchor(Clip clip)
            => clip.Graph.AllNodes.OfType<ITrimmableInput>().FirstOrDefault()?.InPoint ?? TimeSpan.Zero;

        private static void AddGraph(ref HashCode hash, Graph graph, TimeSpan anchor, int depth)
        {
            foreach (Node node in graph.Nodes)
            {
                hash.Add(node.GetType());
                hash.Add(node.Id);
                hash.Add(node.Enabled);

                foreach (PropertyDescriptor descriptor in Inspect.Of(node))
                    AddDescriptor(ref hash, descriptor, node, anchor, depth);

                foreach (IAnimatable animatable in node.Animatables)
                    AddAnimatable(ref hash, animatable, anchor);

                if (node is CompositeNode composite && depth < MaxDepth)
                    AddGraph(ref hash, composite.Inner, anchor, depth + 1);
            }

            foreach (Connection connection in graph.Connections)
            {
                hash.Add(connection.FromNodeId);
                hash.Add(connection.FromPort);
                hash.Add(connection.ToNodeId);
                hash.Add(connection.ToPort);
            }
        }

        private static void AddDescriptor(ref HashCode hash, PropertyDescriptor descriptor, object target, TimeSpan anchor, int depth)
        {
            if (descriptor.GetAnimatable(target) is IAnimatable animatable) AddAnimatable(ref hash, animatable, anchor);
            else if (descriptor.IsCollection) AddValue(ref hash, descriptor.GetList(target), anchor, depth + 1);
            else AddValue(ref hash, descriptor.GetValue(target), anchor, depth + 1);
        }

        private static void AddAnimatable(ref HashCode hash, IAnimatable animatable, TimeSpan anchor)
        {
            AddValue(ref hash, animatable.GetStaticValue(), anchor, MaxDepth);

            foreach (IKeyframe keyframe in animatable.Keyframes)
            {
                hash.Add(keyframe.Start + anchor);
                AddValue(ref hash, keyframe.Value, anchor, MaxDepth);
            }
        }

        private static void AddValue(ref HashCode hash, object? value, TimeSpan anchor, int depth)
        {
            switch (value)
            {
                case null:
                    hash.Add(0);
                    break;

                case IAnimatable animatable:
                    AddAnimatable(ref hash, animatable, anchor);
                    break;

                //the in-point is the anchor, not content; the rest is
                case Source source:
                    source.AddFingerprint(ref hash);
                    break;

                case string s:
                    hash.Add(s);
                    break;

                case IList list when depth < MaxDepth:
                    hash.Add(list.Count);
                    foreach (object? item in list) AddValue(ref hash, item, anchor, depth + 1);
                    break;

                case Node node:
                    hash.Add(node.Id);
                    break;

                default:
                    Type type = value.GetType();

                    //a plain value hashes as itself; an object with editable
                    //properties hashes as those, one level in
                    if (type.IsPrimitive || type.IsEnum || type.IsValueType || depth >= MaxDepth)
                    {
                        hash.Add(value);
                        break;
                    }

                    IReadOnlyList<PropertyDescriptor> descriptors = Inspect.Of(type);

                    if (descriptors.Count == 0)
                    {
                        hash.Add(value);
                        break;
                    }

                    foreach (PropertyDescriptor descriptor in descriptors)
                        AddDescriptor(ref hash, descriptor, value, anchor, depth);

                    break;
            }
        }
    }
}
