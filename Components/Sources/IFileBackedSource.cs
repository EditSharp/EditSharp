namespace EditSharp.Components.Sources
{
    /// <summary>A source whose content is one file on disk.</summary>
    /// <remarks>File-level services (proxies, grouping clips that share a file) work through this rather than a particular kind, so a new file-backed kind gets them by implementing it.</remarks>
    public interface IFileBackedSource
    {
        /// <summary>The full path of the file.</summary>
        string FilePath { get; }
    }
}
