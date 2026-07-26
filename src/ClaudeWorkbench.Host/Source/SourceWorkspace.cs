using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.McpServer;
using AIMonitor.Workflow;
using ClaudeWorkbench.Host.Services;

namespace ClaudeWorkbench.Host.Source;

// Builds the source-browser snapshot from the AIMonitor engine index (in-process)
// and rebuilds that index. Ported from CodexAppServerDemo's SourceWorkspaceService,
// simplified to source-only (no test projection) and retargeted at our engine.
// Reads the current workspace's services through the WorkspaceManager so it
// retargets when the operator switches watched workspaces.
public sealed class SourceWorkspace
{
    private const long MaxReadableFileBytes = 768 * 1024;

    private readonly WorkspaceManager workspace;
    private readonly IndexRebuildStatus rebuildStatus;
    private readonly GitService git;

    public SourceWorkspace(WorkspaceManager workspace, IndexRebuildStatus rebuildStatus, GitService git)
    {
        this.workspace = workspace;
        this.rebuildStatus = rebuildStatus;
        this.git = git;
        workspace.Changed += OnWorkspaceChanged;
    }

    private void OnWorkspaceChanged()
    {
        loaded = false;
        Refresh();
        _ = ReloadTrackedFilesAsync();
    }

    // --- retained state (singleton) so the Source view survives tab switches,
    //     component re-creation, and browser refresh within a host session ----
    private SourceWorkspaceSnapshot current = SourceWorkspaceSnapshot.Empty("Loading source index...");
    private string filter = string.Empty;
    private string filesFilter = string.Empty;
    private string? selectedPath;
    private int? selectedLine;
    private bool rebuilding;
    private bool building;
    private bool loaded;

    // The Files tree is a plain file browser (VS Explorer style), independent of the code index: the
    // git-tracked set (plus new-but-not-ignored files) under the watched folder, loaded once and cached
    // so clicking a file doesn't re-shell git. Symbols tree stays index-backed; only this list is git-fed.
    private IReadOnlyList<SourceFileEntry> trackedFiles = [];

    public event Action? Changed;

    public SourceWorkspaceSnapshot Snapshot => current;

    public string Filter => filter;

    public string FilesFilter => filesFilter;

    public string? SelectedPath => selectedPath;

    public bool Rebuilding => rebuilding;

    public bool Building => building;

    public void EnsureLoaded()
    {
        if (!loaded)
        {
            loaded = true;
            Refresh();
            // Populate the Files tree in the background (git subprocess); Refresh again when it lands.
            _ = ReloadTrackedFilesAsync();
        }
    }

    public void SetFilter(string value)
    {
        filter = value ?? string.Empty;
    }

    public void ApplyFilter()
    {
        Refresh();
    }

    public void SetFilesFilter(string value)
    {
        filesFilter = value ?? string.Empty;
    }

    public void ApplyFilesFilter()
    {
        Refresh();
    }

    // Re-read the git-tracked file set for the Files tree. Runs `git ls-files --cached --others
    // --exclude-standard` in the watched folder: tracked files PLUS new-but-not-ignored ones (so a
    // just-created file shows up in the governed loop) MINUS anything .gitignore excludes (bin/obj,
    // generated corpus, .git). Paths come back relative to the watched folder — the same relative space
    // the Symbols tree uses — so selection needs no special-casing. Best-effort: a non-repo or missing
    // git leaves the Files tree empty rather than failing.
    private async Task ReloadTrackedFilesAsync()
    {
        try
        {
            if (!workspace.HasWorkspace)
            {
                trackedFiles = [];
                return;
            }

            string root = Path.GetFullPath(workspace.Settings.WatchedProjectFolder);
            if (!Directory.Exists(root))
            {
                trackedFiles = [];
                return;
            }

            GitResult result = await git.RunAsync(
                root,
                ["-c", "core.quotePath=false", "ls-files", "--cached", "--others", "--exclude-standard"]);
            if (!result.Ok)
            {
                trackedFiles = [];
                Refresh();
                return;
            }

            List<SourceFileEntry> entries = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string relative = NormalizePath(line.Trim());
                if (relative.Length == 0 || !seen.Add(relative))
                {
                    continue;
                }

                string full = Path.GetFullPath(Path.Combine(root, relative));
                if (!File.Exists(full))
                {
                    continue;
                }

                entries.Add(new SourceFileEntry(
                    relative,
                    full,
                    GetLanguage(Path.GetExtension(full)),
                    GetFileSize(full),
                    File.GetLastWriteTime(full)));
            }

            trackedFiles = entries;
            Refresh();
        }
        catch (Exception)
        {
            // Best-effort: the Files tree just stays empty if git is unavailable or errors.
            trackedFiles = [];
        }
    }

    // Re-read the current source WITHOUT a reindex: rebuild the snapshot from the existing index DB
    // plus fresh file contents from disk (LoadDocument reads the selected file off disk). Cheap; for
    // when the index is current but you want to see the latest saved source. Distinct from
    // RebuildAsync, which re-provisions/reindexes the whole solution.
    public void Reload()
    {
        Refresh();
        // A reload may follow files being added/removed on disk, so refresh the tracked set too.
        _ = ReloadTrackedFilesAsync();
    }

    public void Select(SourceSelection selection)
    {
        selectedPath = selection.RelativePath;
        selectedLine = selection.Line;
        Refresh();
    }

    // Follow a relative link clicked inside a rendered markdown doc — a mini in-viewer docs browser.
    // Resolves the href against the SOURCE doc's folder (so ../architecture/Architecture.md from
    // docs/guide/x.md lands correctly), confines the result to the watched folder, and selects it if it
    // exists on disk. Anything off-tree or missing is ignored — the click never navigates the app tab
    // (that guard is in JS), so a dead link is simply inert, never a crash.
    public void NavigateRelative(DocLinkNavigation navigation)
    {
        if (!workspace.HasWorkspace || string.IsNullOrWhiteSpace(navigation.Href))
        {
            return;
        }

        // Drop any #fragment or ?query, and URL-decode (%20 etc.).
        string href = navigation.Href;
        int cut = href.IndexOfAny(['#', '?']);
        if (cut >= 0)
        {
            href = href[..cut];
        }

        href = Uri.UnescapeDataString(href.Trim());
        if (href.Length == 0)
        {
            return;
        }

        string root = Path.GetFullPath(workspace.Settings.WatchedProjectFolder);
        string fromFull = Path.GetFullPath(Path.Combine(root, navigation.FromRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string baseDirectory = Path.GetDirectoryName(fromFull) ?? root;

        string targetFull;
        try
        {
            targetFull = Path.GetFullPath(Path.Combine(baseDirectory, href.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception)
        {
            return;
        }

        // Confine to the watched folder and require the target to exist.
        string relative = Path.GetRelativePath(root, targetFull);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) || !File.Exists(targetFull))
        {
            return;
        }

        selectedPath = NormalizePath(relative);
        selectedLine = 1;
        Refresh();
    }

    public async Task RebuildAsync()
    {
        rebuilding = true;
        Changed?.Invoke();
        // Also drive the shared status so the global toolbar spinner reflects a manual
        // Source-tab rebuild, consistent with the background startup rebuild.
        using IDisposable scope = rebuildStatus.Begin();
        try
        {
            if (workspace.HasWorkspace)
            {
                await workspace.ProvisionAsync();
            }
        }
        finally
        {
            rebuilding = false;
            selectedPath = null;
            selectedLine = null;
            loaded = true;
            Refresh();
            _ = ReloadTrackedFilesAsync();
        }
    }

    // Operator Build: real `dotnet build` into the watched tree's own bin/<config> — the same
    // SolutionBuildService the accept dialog's "Build after accept" uses, just on demand from the
    // Source tab. Shells the SDK off the UI thread; the Building flag drives the button spinner.
    public async Task<SolutionBuildService.BuildResult> BuildAsync(string configuration)
    {
        if (!workspace.HasWorkspace)
        {
            return new SolutionBuildService.BuildResult("no-workspace", true, configuration, string.Empty, 0, [], 0, "No watched workspace.");
        }

        building = true;
        Changed?.Invoke();
        try
        {
            return await Task.Run(() => new SolutionBuildService().Build(workspace.Settings, configuration));
        }
        finally
        {
            building = false;
            Changed?.Invoke();
        }
    }

    // Operator Run: build, then launch the executable — F5 semantics, mirroring the accept dialog's
    // build-then-run. A build failure short-circuits into a RunResult so the caller shows one message.
    // When projectPath is given (the Source tab's project dropdown) that exact project is launched;
    // otherwise it falls back to auto-detecting a single executable.
    public async Task<SolutionRunService.RunResult> RunAsync(string configuration, string? projectPath = null)
    {
        if (!workspace.HasWorkspace)
        {
            return new SolutionRunService.RunResult(true, "No watched workspace.", null);
        }

        building = true;
        Changed?.Invoke();
        try
        {
            SolutionBuildService.BuildResult build = await Task.Run(() => new SolutionBuildService().Build(workspace.Settings, configuration));
            if (build.IsError)
            {
                string detail = build.Diagnostics.Count > 0 ? build.Diagnostics[0] : build.Message;
                return new SolutionRunService.RunResult(true, "Build failed — " + detail, null);
            }

            return await Task.Run(() => string.IsNullOrWhiteSpace(projectPath)
                ? new SolutionRunService().Run(workspace.Settings, configuration)
                : new SolutionRunService().RunProject(workspace.Settings, configuration, projectPath));
        }
        finally
        {
            building = false;
            Changed?.Invoke();
        }
    }

    private void Refresh()
    {
        current = BuildSnapshot(selectedPath, selectedLine, filter);
        Changed?.Invoke();
    }

    public SourceWorkspaceSnapshot BuildSnapshot(string? selectedRelativePath, int? selectedLine, string? filter)
    {
        if (!workspace.HasWorkspace)
        {
            return SourceWorkspaceSnapshot.Empty("Select a watched workspace to browse source.");
        }

        MonitorStatusResult status = workspace.Query.GetMonitorStatus();
        if (!status.DatabaseExists)
        {
            return WithMessage(status, filter, "Solution index is missing. Rebuild the index to load source.");
        }

        IReadOnlyList<IndexedProjectRow> projects = workspace.Query.ListProjects();
        IReadOnlyList<IndexedDocumentRow> documents = workspace.Query.ListDocuments();
        if (projects.Count == 0 || documents.Count == 0)
        {
            return WithMessage(status, filter, "Solution index is empty or stale. Rebuild the index to load source.");
        }

        IReadOnlyList<IndexedSymbolRow> symbols = workspace.Query.ListSymbols();
        string watchedRoot = workspace.Settings.WatchedProjectFolder;
        IReadOnlyList<SourceFileEntry> files = BuildFiles(watchedRoot, documents, filter);
        IReadOnlyList<SourceTreeNode> tree = BuildTree(projects, documents, symbols, watchedRoot, files);
        IReadOnlyList<SourceFileEntry> fileEntries = BuildFilesEntries(filesFilter);
        SourceFileDocument? selectedFile = ResolveSelected(files, fileEntries, symbols, watchedRoot, selectedRelativePath, selectedLine);

        return new SourceWorkspaceSnapshot(
            watchedRoot,
            workspace.Settings.WatchedSolutionPath,
            status.DatabasePath,
            files,
            tree,
            selectedFile,
            filter ?? string.Empty,
            files.Count == 0 ? "No indexed source files matched the current filter." : string.Empty,
            BuildRunnableProjects(projects),
            BuildPlainFileTree(fileEntries),
            fileEntries.Count,
            filesFilter);
    }

    // The Files tree's file set: the cached git-tracked entries, filtered + ranked like the index list.
    private IReadOnlyList<SourceFileEntry> BuildFilesEntries(string? currentFilesFilter)
    {
        IEnumerable<SourceFileEntry> query = trackedFiles;
        if (!string.IsNullOrWhiteSpace(currentFilesFilter))
        {
            query = query.Where(file => file.RelativePath.IndexOf(currentFilesFilter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        return query
            .OrderBy(file => GetFileRank(file.RelativePath))
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // A plain folder/file tree (no symbols) from a flat entry list — the Files tab. Same node type +
    // renderer as the Symbols tree, so collapse/expand and click-to-open work identically.
    private static IReadOnlyList<SourceTreeNode> BuildPlainFileTree(IReadOnlyList<SourceFileEntry> entries)
    {
        MutableSourceTreeNode root = new(string.Empty, "root", null);
        foreach (SourceFileEntry entry in entries)
        {
            string[] segments = entry.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            MutableSourceTreeNode node = root;
            for (int index = 0; index < segments.Length; index++)
            {
                bool isFile = index == segments.Length - 1;
                node = node.GetOrAdd(segments[index], isFile ? "file" : "folder", isFile ? entry : null);
            }
        }

        return root.Children
            .OrderBy(child => GetTreeNodeRank(child.Kind))
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ConvertNode)
            .ToArray();
    }

    // Resolve the file shown in the (shared) viewer from a selected relative path. Looks in the index
    // file list first (so an indexed file keeps its outline), then the Files-tab entries, then falls
    // back to a raw on-disk path — so a Files-only file loads straight into Monaco with an empty outline.
    // With nothing selected, defaults to the first indexed file so the viewer isn't blank on open.
    private SourceFileDocument? ResolveSelected(
        IReadOnlyList<SourceFileEntry> indexFiles,
        IReadOnlyList<SourceFileEntry> fileEntries,
        IReadOnlyList<IndexedSymbolRow> symbols,
        string watchedRoot,
        string? selectedRelativePath,
        int? line)
    {
        SourceFileEntry? entry = null;
        if (!string.IsNullOrWhiteSpace(selectedRelativePath))
        {
            string normalized = NormalizePath(selectedRelativePath);
            entry = indexFiles.FirstOrDefault(file => file.RelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                ?? fileEntries.FirstOrDefault(file => file.RelativePath.Equals(normalized, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                string full = Path.GetFullPath(Path.Combine(watchedRoot, normalized));
                if (File.Exists(full))
                {
                    entry = new SourceFileEntry(normalized, full, GetLanguage(Path.GetExtension(full)), GetFileSize(full), File.GetLastWriteTime(full));
                }
            }
        }

        entry ??= indexFiles.FirstOrDefault();
        return entry is null ? null : LoadDocument(entry, symbols, line);
    }

    // The executable projects the operator can Run, straight from the index's OutputType — no disk
    // rescan, so the dropdown lists exactly the apps the tree already shows. Sorted for a stable menu.
    private static IReadOnlyList<RunnableProjectEntry> BuildRunnableProjects(IReadOnlyList<IndexedProjectRow> projects)
    {
        return projects
            .Where(project => project.OutputType.Equals("Exe", StringComparison.OrdinalIgnoreCase)
                || project.OutputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(project => new RunnableProjectEntry(project.Name, Path.GetFullPath(project.ProjectPath)))
            .ToArray();
    }

    // Index-missing / empty paths still get a usable Files tab — it's git-fed, not index-fed, so it
    // works even before the first index build. The selected file loads by path with no outline.
    private SourceWorkspaceSnapshot WithMessage(MonitorStatusResult status, string? filter, string message)
    {
        string watchedRoot = workspace.Settings.WatchedProjectFolder;
        IReadOnlyList<SourceFileEntry> fileEntries = BuildFilesEntries(filesFilter);
        SourceFileDocument? selectedFile = ResolveSelected([], fileEntries, [], watchedRoot, selectedPath, selectedLine);

        return new SourceWorkspaceSnapshot(
            watchedRoot,
            workspace.Settings.WatchedSolutionPath,
            status.DatabasePath,
            [],
            [],
            selectedFile,
            filter ?? string.Empty,
            message,
            [],
            BuildPlainFileTree(fileEntries),
            fileEntries.Count,
            filesFilter);
    }

    private static IReadOnlyList<SourceFileEntry> BuildFiles(
        string watchedRoot,
        IReadOnlyList<IndexedDocumentRow> documents,
        string? filter)
    {
        return documents
            .Select(document => document.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new SourceFileEntry(
                NormalizePath(Path.GetRelativePath(watchedRoot, path)),
                Path.GetFullPath(path),
                GetLanguage(Path.GetExtension(path)),
                GetFileSize(path),
                File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue))
            .Where(file => string.IsNullOrWhiteSpace(filter)
                || file.RelativePath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(file => GetFileRank(file.RelativePath))
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<SourceTreeNode> BuildTree(
        IReadOnlyList<IndexedProjectRow> projects,
        IReadOnlyList<IndexedDocumentRow> documents,
        IReadOnlyList<IndexedSymbolRow> symbols,
        string watchedRoot,
        IReadOnlyList<SourceFileEntry> visibleFiles)
    {
        Dictionary<string, SourceFileEntry> visibleByFullPath = visibleFiles
            .ToDictionary(file => Path.GetFullPath(file.FullPath), StringComparer.OrdinalIgnoreCase);
        List<SourceTreeNode> projectNodes = [];

        foreach (IndexedProjectRow project in projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
        {
            MutableSourceTreeNode projectNode = new(project.Name, "project", null);
            string projectDirectory = Path.GetDirectoryName(project.ProjectPath) ?? watchedRoot;
            foreach (IndexedDocumentRow document in documents
                .Where(document => PathEquals(document.ProjectPath, project.ProjectPath))
                .OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                string fullDocumentPath = Path.GetFullPath(document.FilePath);
                if (!visibleByFullPath.TryGetValue(fullDocumentPath, out SourceFileEntry? fileEntry))
                {
                    continue;
                }

                string relativeToProject = NormalizePath(Path.GetRelativePath(projectDirectory, fullDocumentPath));
                string[] segments = relativeToProject.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0 || segments.Any(IsHiddenBuildFolder))
                {
                    continue;
                }

                MutableSourceTreeNode current = projectNode;
                for (int index = 0; index < segments.Length; index++)
                {
                    bool isFile = index == segments.Length - 1;
                    current = current.GetOrAdd(
                        segments[index],
                        isFile ? "file" : "folder",
                        isFile ? fileEntry : null);
                }

                AddSymbolNodes(current, document, symbols, fileEntry);
            }

            projectNodes.Add(ConvertNode(projectNode));
        }

        return projectNodes;
    }

    private static SourceTreeNode ConvertNode(MutableSourceTreeNode node)
    {
        IReadOnlyList<SourceTreeNode> children = node.Children
            .OrderBy(child => GetTreeNodeRank(child.Kind))
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ConvertNode)
            .ToArray();

        return new SourceTreeNode(node.Name, node.Kind, node.File, node.TargetFile, node.Line, children);
    }

    private static void AddSymbolNodes(
        MutableSourceTreeNode documentNode,
        IndexedDocumentRow document,
        IReadOnlyList<IndexedSymbolRow> symbols,
        SourceFileEntry fileEntry)
    {
        Dictionary<string, MutableSourceTreeNode> typeNodes = new(StringComparer.Ordinal);
        foreach (IndexedSymbolRow symbol in symbols
            .Where(symbol => PathEquals(symbol.FilePath, document.FilePath))
            .OrderBy(symbol => symbol.StartLine)
            .ThenBy(symbol => symbol.Name, StringComparer.Ordinal))
        {
            if (IsTypeLikeSymbol(symbol))
            {
                MutableSourceTreeNode typeNode = documentNode.GetOrAdd(
                    FormatSymbolNode(symbol),
                    "type",
                    null,
                    fileEntry,
                    Math.Max(symbol.StartLine, 1));
                typeNodes[symbol.Signature] = typeNode;
                continue;
            }

            MutableSourceTreeNode parent = documentNode;
            if (!string.IsNullOrWhiteSpace(symbol.ContainingType)
                && typeNodes.TryGetValue(symbol.ContainingType, out MutableSourceTreeNode? typeParent))
            {
                parent = typeParent;
            }

            MutableSourceTreeNode group = parent.GetOrAdd(
                FormatSymbolGroupName(symbol),
                "group",
                null,
                fileEntry,
                Math.Max(symbol.StartLine, 1));
            group.GetOrAdd(
                FormatSymbolNode(symbol),
                GetSymbolNodeKind(symbol),
                null,
                fileEntry,
                Math.Max(symbol.StartLine, 1));
        }
    }

    private static SourceFileDocument LoadDocument(SourceFileEntry entry, IReadOnlyList<IndexedSymbolRow> symbols, int? selectedLine)
    {
        string fullPath = Path.GetFullPath(entry.FullPath);
        FileInfo info = new(fullPath);
        int safeSelectedLine = Math.Max(selectedLine ?? 1, 1);
        if (!info.Exists)
        {
            return new SourceFileDocument(entry.RelativePath, fullPath, GetLanguage(info.Extension),
                "Indexed file no longer exists on disk.", safeSelectedLine, [], 0);
        }

        if (info.Length > MaxReadableFileBytes)
        {
            return new SourceFileDocument(entry.RelativePath, fullPath, GetLanguage(info.Extension),
                $"File is too large to preview ({info.Length:n0} bytes).", safeSelectedLine,
                BuildOutlineFromIndex(symbols, fullPath), info.Length);
        }

        string text = File.ReadAllText(fullPath);
        int lineCount = Math.Max(1, text.Count(character => character == '\n') + 1);
        return new SourceFileDocument(entry.RelativePath, fullPath, GetLanguage(info.Extension), text,
            Math.Min(safeSelectedLine, lineCount), BuildOutlineFromIndex(symbols, fullPath), info.Length);
    }

    private static IReadOnlyList<SourceSymbolEntry> BuildOutlineFromIndex(IReadOnlyList<IndexedSymbolRow> symbols, string fullPath)
    {
        return symbols
            .Where(symbol => PathEquals(symbol.FilePath, fullPath))
            .OrderBy(symbol => symbol.StartLine)
            .ThenBy(symbol => symbol.Name, StringComparer.Ordinal)
            .Take(160)
            .Select(symbol => new SourceSymbolEntry(
                string.IsNullOrWhiteSpace(symbol.Signature) ? symbol.Name : SimplifySignature(symbol.Signature),
                symbol.Kind,
                Math.Max(symbol.StartLine, 1)))
            .ToArray();
    }

    private static string SimplifySignature(string signature)
    {
        return signature
            .Replace("System.Collections.Generic.", string.Empty, StringComparison.Ordinal)
            .Replace("System.Threading.Tasks.", string.Empty, StringComparison.Ordinal)
            .Replace("System.Threading.", string.Empty, StringComparison.Ordinal)
            .Replace("System.", string.Empty, StringComparison.Ordinal);
    }

    private static long GetFileSize(string path)
    {
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private static string GetLanguage(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".cs" => "C#",
            ".razor" => "Razor",
            ".cshtml" => "Razor",
            ".csproj" => "MSBuild",
            ".sln" => "Solution",
            ".slnx" => "Solution",
            ".json" => "JSON",
            ".md" => "Markdown",
            ".xml" => "XML",
            ".xaml" => "XAML",
            ".css" => "CSS",
            ".html" => "HTML",
            ".js" => "JavaScript",
            ".ts" => "TypeScript",
            _ => "Text",
        };
    }

    private static int GetFileRank(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);
        if (fileName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static int GetTreeNodeRank(string kind)
    {
        return kind switch
        {
            "project" => 0,
            "folder" => 1,
            "file" => 2,
            "type" => 3,
            "group" => 4,
            "constructor" => 5,
            "method" => 5,
            "property" => 5,
            "field" => 5,
            "symbol" => 5,
            _ => 10,
        };
    }

    private static string GetSymbolNodeKind(IndexedSymbolRow symbol)
    {
        if (IsConstructorSymbol(symbol))
        {
            return "constructor";
        }

        if (symbol.Kind.Equals("Method", StringComparison.OrdinalIgnoreCase))
        {
            return "method";
        }

        if (symbol.Kind.Equals("Property", StringComparison.OrdinalIgnoreCase))
        {
            return "property";
        }

        if (symbol.Kind.Equals("Field", StringComparison.OrdinalIgnoreCase))
        {
            return "field";
        }

        return "symbol";
    }

    private static string FormatSymbolNode(IndexedSymbolRow symbol)
    {
        return $"{FormatLocalSignature(symbol)} [{symbol.StartLine}-{symbol.EndLine}]";
    }

    private static string FormatLocalSignature(IndexedSymbolRow symbol)
    {
        if (IsTypeLikeSymbol(symbol))
        {
            return symbol.Name;
        }

        string signature = string.IsNullOrWhiteSpace(symbol.Signature) ? symbol.Name : symbol.Signature;
        return SimplifySignatureTypes(RemoveContainingTypePrefix(signature, symbol));
    }

    private static string RemoveContainingTypePrefix(string signature, IndexedSymbolRow symbol)
    {
        if (!string.IsNullOrWhiteSpace(symbol.ContainingType))
        {
            string containingPrefix = symbol.ContainingType + ".";
            if (signature.StartsWith(containingPrefix, StringComparison.Ordinal))
            {
                return signature[containingPrefix.Length..];
            }
        }

        if (!string.IsNullOrWhiteSpace(symbol.Namespace))
        {
            string namespacePrefix = symbol.Namespace + ".";
            if (signature.StartsWith(namespacePrefix, StringComparison.Ordinal))
            {
                return signature[namespacePrefix.Length..];
            }
        }

        return signature;
    }

    private static string SimplifySignatureTypes(string signature)
    {
        int parameterStart = signature.IndexOf('(', StringComparison.Ordinal);
        int parameterEnd = signature.LastIndexOf(')');
        if (parameterStart < 0 || parameterEnd <= parameterStart)
        {
            return GetUnqualifiedTypeName(signature);
        }

        string name = signature[..parameterStart];
        string parameters = signature[(parameterStart + 1)..parameterEnd];
        string suffix = signature[(parameterEnd + 1)..];
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return $"{GetUnqualifiedTypeName(name)}(){suffix}";
        }

        string[] parts = parameters.Split(',');
        for (int index = 0; index < parts.Length; index++)
        {
            parts[index] = SimplifyParameter(parts[index].Trim());
        }

        return $"{GetUnqualifiedTypeName(name)}({string.Join(", ", parts)}){suffix}";
    }

    private static string SimplifyParameter(string parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
        {
            return parameter;
        }

        string[] tokens = parameter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < tokens.Length; index++)
        {
            tokens[index] = SimplifyTypeExpression(tokens[index]);
        }

        return string.Join(" ", tokens);
    }

    private static string SimplifyTypeExpression(string text)
    {
        return text
            .Replace("System.Collections.Generic.", string.Empty, StringComparison.Ordinal)
            .Replace("System.Threading.Tasks.", string.Empty, StringComparison.Ordinal)
            .Replace("System.Threading.", string.Empty, StringComparison.Ordinal)
            .Replace("System.", string.Empty, StringComparison.Ordinal);
    }

    private static string GetUnqualifiedTypeName(string name)
    {
        int genericIndex = name.IndexOf('<', StringComparison.Ordinal);
        string rootName = genericIndex >= 0 ? name[..genericIndex] : name;
        int lastDot = rootName.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < rootName.Length - 1)
        {
            rootName = rootName[(lastDot + 1)..];
        }

        return genericIndex >= 0 ? rootName + name[genericIndex..] : rootName;
    }

    private static string FormatSymbolGroupName(IndexedSymbolRow symbol)
    {
        string access = string.IsNullOrWhiteSpace(symbol.Accessibility) ? "access unknown" : symbol.Accessibility.ToLowerInvariant();
        if (IsConstructorSymbol(symbol))
        {
            return $"{access} constructors";
        }

        if (symbol.Kind.Equals("Method", StringComparison.OrdinalIgnoreCase))
        {
            return $"{access} methods";
        }

        return $"{access} members";
    }

    private static bool IsTypeLikeSymbol(IndexedSymbolRow symbol)
    {
        return symbol.Kind.Equals("NamedType", StringComparison.OrdinalIgnoreCase)
            || symbol.Kind.Equals("Class", StringComparison.OrdinalIgnoreCase)
            || symbol.Kind.Equals("Struct", StringComparison.OrdinalIgnoreCase)
            || symbol.Kind.Equals("Interface", StringComparison.OrdinalIgnoreCase)
            || symbol.Kind.Equals("Enum", StringComparison.OrdinalIgnoreCase)
            || symbol.Kind.Equals("Record", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConstructorSymbol(IndexedSymbolRow symbol)
    {
        return symbol.Kind.Equals("Method", StringComparison.OrdinalIgnoreCase)
            && (symbol.MethodKind.Equals("Constructor", StringComparison.OrdinalIgnoreCase)
                || symbol.MethodKind.Equals("StaticConstructor", StringComparison.OrdinalIgnoreCase)
                || symbol.Name.Equals(".ctor", StringComparison.Ordinal)
                || symbol.Name.Equals(".cctor", StringComparison.Ordinal));
    }

    private static bool IsHiddenBuildFolder(string segment)
    {
        return segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("CSSourceBackups", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
}
