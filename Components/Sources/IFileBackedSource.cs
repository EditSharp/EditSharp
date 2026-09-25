namespace EditSharp.Components.Sources
{
    /// <summary>
    /// A source whose content lives in one file on disk. File-level services
    /// (proxies, grouping clips that share a file) work
    /// against this rather than any concrete kind, so a new file-backed kind
    /// gets them by implementing it.
    /// </summary>
    public interface IFileBackedSource
    {
        string FilePath { get; }
    }
}
