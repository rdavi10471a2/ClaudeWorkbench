namespace AIMonitor.Data;

public sealed record IndexedProjectRow(
    string StableKey,
    string Name,
    string ProjectPath,
    string Language,
    string TargetFramework,
    string TargetFrameworks,
    string OutputType,
    string Sdk,
    string AssemblyName,
    string RootNamespace,
    string Nullable,
    string ImplicitUsings,
    string LangVersion,
    string PreprocessorSymbols);
