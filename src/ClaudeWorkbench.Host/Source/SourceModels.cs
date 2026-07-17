namespace ClaudeWorkbench.Host.Source;

// View-models for the source browser, ported from CodexAppServerDemo. Backed by
// the AIMonitor engine index (source-only; no test projection).

public sealed record SourceWorkspaceSnapshot(
    string WorkspaceRoot,
    string WatchedSolutionPath,
    string IndexDatabasePath,
    IReadOnlyList<SourceFileEntry> Files,
    IReadOnlyList<SourceTreeNode> Tree,
    SourceFileDocument? SelectedFile,
    string Filter,
    string Message)
{
    public static SourceWorkspaceSnapshot Empty(string message)
    {
        return new SourceWorkspaceSnapshot(string.Empty, string.Empty, string.Empty, [], [], null, string.Empty, message);
    }
}

public sealed record SourceFileEntry(
    string RelativePath,
    string FullPath,
    string Language,
    long Size,
    DateTime LastWriteTime);

public sealed record SourceFileDocument(
    string RelativePath,
    string FullPath,
    string Language,
    string Text,
    int SelectedLine,
    IReadOnlyList<SourceSymbolEntry> Outline,
    long Size);

public sealed record SourceSelection(
    string RelativePath,
    int Line);

public sealed record SourceSymbolEntry(
    string Name,
    string Kind,
    int Line);

public sealed record SourceTreeNode(
    string Name,
    string Kind,
    SourceFileEntry? File,
    SourceFileEntry? TargetFile,
    int Line,
    IReadOnlyList<SourceTreeNode> Children);

internal sealed class MutableSourceTreeNode
{
    private readonly Dictionary<string, MutableSourceTreeNode> children = new(StringComparer.OrdinalIgnoreCase);

    public MutableSourceTreeNode(string name, string kind, SourceFileEntry? file)
    {
        Name = name;
        Kind = kind;
        File = file;
    }

    public string Name { get; }

    public string Kind { get; private set; }

    public SourceFileEntry? File { get; private set; }

    public SourceFileEntry? TargetFile { get; private set; }

    public int Line { get; private set; } = 1;

    public IEnumerable<MutableSourceTreeNode> Children => children.Values;

    public MutableSourceTreeNode GetOrAdd(
        string name,
        string kind,
        SourceFileEntry? file,
        SourceFileEntry? targetFile = null,
        int line = 1)
    {
        if (children.TryGetValue(name, out MutableSourceTreeNode? child))
        {
            if (file is not null)
            {
                child.Kind = kind;
                child.File = file;
            }

            if (targetFile is not null)
            {
                child.TargetFile = targetFile;
                child.Line = line;
            }

            return child;
        }

        child = new MutableSourceTreeNode(name, kind, file)
        {
            TargetFile = targetFile,
            Line = line,
        };
        children[name] = child;
        return child;
    }
}
