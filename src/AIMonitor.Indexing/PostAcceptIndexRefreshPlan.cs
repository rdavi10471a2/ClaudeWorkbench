namespace AIMonitor.Indexing;

public sealed class PostAcceptIndexRefreshPlan
{
    public IReadOnlyList<string> OwningProjectPaths { get; set; } = [];

    public IReadOnlyList<string> ChangedFilePaths { get; set; } = [];

    // Engine-derived refresh closure beyond the edited project: the OTHER projects that hold inbound references
    // (symbol_references / call_sites / symbol_relationships) into a symbol declared in the edited project. When this
    // is non-empty, a project-scoped refresh would cascade-delete those inbound rows, so the engine falls back to a
    // full rebuild (MVP). Populated at refresh time from the index so it is inspectable in indexRefresh telemetry.
    public IReadOnlyList<string> InboundReferencingProjects { get; set; } = [];
}
