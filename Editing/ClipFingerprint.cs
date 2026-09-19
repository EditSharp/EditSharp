using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EditSharp.Components;
using EditSharp.Components.Clips;
using EditSharp.Components.Nodes;

namespace EditSharp.Editing
{
    /// <summary>
    /// A number that changes when, and only when, a clip would render
    /// differently: its speed, the shape of its graph, and every value in it.
    /// </summary>
    /// <remarks>
    /// What it leaves out is where the clip sits on the timeline, and the
    /// head in-point of its media. A head trim moves the in-point and slides
    /// every keyframe by the same amount, and the pictures along the content
    /// are the same pictures under a different clip-relative time - so
    /// keyframe times are hashed against <see cref="Anchor"/>, where they
    /// stay put. A thumbnail cache keys its frames by anchored time for the
    /// same reason, and compares this after every history entry to learn
    /// which clips to drop.
    ///
    /// Values are found through the <see cref="Editable"/> metadata, plus
    /// every <see cref="Node.Animatables"/> whether attributed or not; a
    /// nested timeline hashes as its reference, not its contents.
    /// </remarks>
    public static class ClipFingerprint
    {
        private const int MaxDepth = 6;

        public static int Of(Clip clip)
        {
            ArgumentNullException.ThrowIfNull(clip);

            var hash = new HashCode();
            hash.Add(clip.Speed);

            TimeSpan anchor = Anchor(clip);
            AddGraph(ref hash, clip.Graph, anchor, 0);

            return hash.ToHashCode();
        }

        /// <summary>
        /// The head in-point the clip's content is anchored to: the first
        /// trimmable input's, since every trimmable input in a graph shifts
        /// together. A generator clip has none and anchors at zero.
        /// </summary>
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
                    hash.Add(source.Path);
                    hash.Add(source.Type);
                    hash.Add(source.Duration);
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
