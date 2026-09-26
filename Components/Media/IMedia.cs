using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EditSharp.Editing;
using EditSharp.History;
using EditSharp.Video;

namespace EditSharp.Components.Media
{
    /// <summary>Material an input node reads: a file on disk, opened the way its kind knows how.</summary>
    /// <remarks>
    /// A media knows nothing about clips or nodes. It reports how long its material
    /// is and opens readers on it in its own time, from the start of the material;
    /// the input node holding it maps the clip's content time to that. Any number
    /// of nodes, in any number of clips, can share one media, and <see cref="UsedBy"/>
    /// lists them. Nothing prepared or opened is kept on the media.
    /// <para>
    /// A media is plain data with history. Its [Editable] properties are exactly
    /// what <see cref="ComponentSerializer"/> saves, and <see cref="Duplicate"/>
    /// round-trips through it, so a new property is copied exactly when it's saved.
    /// Nodes save a media as its <see cref="Id"/>: a project keeps the media
    /// themselves and hands them to the loader.
    /// </para>
    /// <para>
    /// To add a kind, derive from <see cref="VideoMedia"/> or <see cref="AudioMedia"/>,
    /// mark the class [MediaKind("stable-id")], and override what differs: how the
    /// length is found, how readers are opened. The id is written into saved files,
    /// so it must never change. BlenderSceneMedia and VstInstrumentMedia show the
    /// bare shape.
    /// </para>
    /// </remarks>
    public abstract class IMedia
    {
        /// <summary>Identifies the media; it's saved, and nodes refer to the media by it.</summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        string? _name;
        /// <summary>The media's name, as editors show it; the file's name unless one is given.</summary>
        /// <remarks>Setting it empty goes back to the file's name.</remarks>
        [Editable("Name", Order = -100)]
        public string Name
        {
            get => string.IsNullOrEmpty(_name) ? DefaultName : _name;
            set => Transaction.Set(this, ref _name, string.IsNullOrEmpty(value) ? null : value, static (o, v) => o._name = v);
        }

        //the name as given, so a default name keeps following the file; what's saved as "name"
        internal string? CustomName { get => _name; set => _name = value; }

        string _path = "";
        /// <summary>The full path of the file.</summary>
        /// <remarks>File-level services (proxies, grouping clips that share a file) work through this, so every kind gets them.</remarks>
        [Editable("File", Editor = PropertyEditor.Path)]
        public required string Path { get => _path; set { Transaction.Set(this, ref _path, value, static (o, v) => o._path = v); PathChanged(); ContentChanged(); } }

        /// <summary>Called when <see cref="Path"/> changes, before the clips reading this media are trimmed; for a kind whose parts follow the file.</summary>
        protected virtual void PathChanged() { }

        /// <summary>The name shown when none has been given: the file's name, or the kind's when no file is chosen.</summary>
        protected virtual string DefaultName =>
            System.IO.Path.GetFileName(Path) is { Length: > 0 } file ? file : MediaKinds.Of(this)?.DisplayName ?? GetType().Name;

        private List<string> _tags = [];
        /// <summary>Labels the user has put on the media, for editors to sort and filter by.</summary>
        [Editable("Tags", Order = -90)]
        public List<string> Tags { get => _tags; set => Transaction.Set(this, ref _tags, value ?? [], static (o, v) => o._tags = v); }

        /// <summary>Adds a tag.</summary>
        /// <remarks>Nothing happens for a blank tag or one the media already has.</remarks>
        /// <param name="tag">The tag.</param>
        public void AddTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag) || _tags.Contains(tag)) return;
            Transaction.Apply(() => _tags.Add(tag), () => _tags.Remove(tag), "tag media");
        }

        /// <summary>Removes a tag.</summary>
        /// <remarks>Nothing happens when the media doesn't have it.</remarks>
        /// <param name="tag">The tag.</param>
        public void RemoveTag(string tag)
        {
            int index = _tags.IndexOf(tag);
            if (index < 0) return;
            Transaction.Apply(() => _tags.Remove(tag), () => _tags.Insert(Math.Min(index, _tags.Count), tag), "untag media");
        }

        private int _probing;

        /// <summary>What probing the file found, if it has been probed.</summary>
        /// <remarks>A file not probed yet starts its probe in the background, and <see cref="InfoAvailable"/> fires when that finishes.</remarks>
        /// <param name="info">The findings when they're known.</param>
        /// <returns>True when <paramref name="info"/> is set.</returns>
        public bool TryGetInfo(out MediaInfo info)
        {
            if (MediaProbe.TryGetCached(Path, out info)) return true;

            if (!string.IsNullOrEmpty(Path) && File.Exists(Path) && Interlocked.Exchange(ref _probing, 1) == 0)
            {
                _ = MediaProbe.ProbeCachedAsync(Path).ContinueWith(t =>
                {
                    _ = t.Exception;
                    _probing = 0;
                    InfoAvailable?.Invoke(this);
                }, TaskScheduler.Default);
            }

            return false;
        }

        /// <summary>Fires when a probe started by <see cref="TryGetInfo"/> has finished, whether or not it succeeded, on a thread pool thread.</summary>
        public event Action<IMedia>? InfoAvailable;

        private readonly List<Nodes.Node> _usedBy = [];
        /// <summary>Every node holding this media, whether or not its clip is placed.</summary>
        public IReadOnlyList<Nodes.Node> UsedBy => _usedBy;

        internal void AddUser(Nodes.Node node) =>
            Transaction.Apply(() => _usedBy.Add(node), () => _usedBy.Remove(node), "use media");

        internal void RemoveUser(Nodes.Node node)
        {
            if (!_usedBy.Contains(node)) return;
            Transaction.Apply(() => _usedBy.Remove(node), () => _usedBy.Add(node), "release media");
        }

        /// <summary>Trims every clip reading this media to its end, after an edit that may have changed the material or its length.</summary>
        /// <remarks>Call it from the setter of any property that changes what the material is.</remarks>
        protected void ContentChanged()
        {
            foreach (Nodes.Node node in _usedBy) node.OwnerClip?.TrimToSources();
        }

        /// <summary>How long the material is.</summary>
        /// <param name="ct">Cancels finding the length.</param>
        /// <returns>The length, or null when the material has no end of its own (a still image).</returns>
        /// <exception cref="SourceUnavailableException">The material can't be read; <see cref="SourceUnavailableReason.NoMedia"/> when no file is chosen.</exception>
        public abstract Task<Time?> GetNaturalLengthAsync(CancellationToken ct = default);

        /// <summary><see cref="GetNaturalLengthAsync"/>'s answer, if it's known right away.</summary>
        /// <param name="length">The length when known; otherwise null.</param>
        /// <returns>False while the length still has to be found, such as for a file not probed yet.</returns>
        public virtual bool TryGetNaturalLength(out Time? length)
        {
            Task<Time?> task = GetNaturalLengthAsync();
            length = task.IsCompletedSuccessfully ? task.Result : null;
            return task.IsCompletedSuccessfully;
        }

        /// <summary>A deep copy with a new <see cref="Id"/>, made by saving and loading this media.</summary>
        /// <remarks>History is suppressed: the copy has nothing to undo. Nothing reads the copy yet.</remarks>
        /// <returns>The copy.</returns>
        public virtual IMedia Duplicate() => ComponentSerializer.Copy(this);

        //adds what identifies this media's content to a clip fingerprint: the kind and the file. The Id and
        //the name are left out because two media of one file show the same thing
        internal virtual void AddFingerprint(ref HashCode hash)
        {
            hash.Add(GetType());
            hash.Add(Path);
        }
    }
}
