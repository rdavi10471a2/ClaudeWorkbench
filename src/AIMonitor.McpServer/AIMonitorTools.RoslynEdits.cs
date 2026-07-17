using AIMonitor.Core;
using AIMonitor.Data;
using AIMonitor.Indexing;
using AIMonitor.Logging;
using AIMonitor.Runtime;
using AIMonitor.Workflow;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIMonitor.McpServer;

public sealed partial class AIMonitorTools
{
    [McpServerTool]
    [Description("Replace one C# symbol in the monitor-owned Working candidate using a Roslyn selector.")]
    public RoslynEditResult SubmitSymbol(string path, string symbolSelectorJson, string code, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.SubmitSymbol(fullPath, symbolSelectorJson, code, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "submit-symbol", result);
        return result;
    }

    [McpServerTool]
    [Description("Add a using directive to the monitor-owned Working candidate.")]
    public RoslynEditResult AddUsing(string path, string @namespace, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.AddUsing(fullPath, @namespace, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "add-using", result);
        return result;
    }

    [McpServerTool]
    [Description("Remove a using directive from the monitor-owned Working candidate.")]
    public RoslynEditResult RemoveUsing(string path, string @namespace, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.RemoveUsing(fullPath, @namespace, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "remove-using", result);
        return result;
    }

    [McpServerTool]
    [Description("Add or remove the partial modifier on a C# type in the monitor-owned Working candidate.")]
    public RoslynEditResult SetTypePartial(string path, string containingType, bool isPartial, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.SetTypePartial(fullPath, containingType, isPartial, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "set-type-partial", result);
        return result;
    }

    [McpServerTool]
    [Description("Add a C# member or nested type to a containing type in the monitor-owned Working candidate.")]
    public RoslynEditResult AddSymbol(string path, string containingType, string symbolType, string code, string? afterSymbol = null, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.AddSymbol(fullPath, containingType, symbolType, code, afterSymbol, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "add-symbol", result);
        return result;
    }

    [McpServerTool]
    [Description("Add a C# field to a containing type in the monitor-owned Working candidate.")]
    public RoslynEditResult AddField(string path, string containingType, string declaration, string? afterSymbol = null, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.AddField(fullPath, containingType, declaration, afterSymbol, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "add-field", result);
        return result;
    }

    [McpServerTool]
    [Description("Add a C# property to a containing type in the monitor-owned Working candidate.")]
    public RoslynEditResult AddProperty(string path, string containingType, string declaration, string? afterSymbol = null, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.AddProperty(fullPath, containingType, declaration, afterSymbol, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "add-property", result);
        return result;
    }

    [McpServerTool]
    [Description("Add a C# method to a containing type in the monitor-owned Working candidate.")]
    public RoslynEditResult AddMethod(string path, string containingType, string declaration, string? afterSymbol = null, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.AddMethod(fullPath, containingType, declaration, afterSymbol, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "add-method", result);
        return result;
    }

    [McpServerTool]
    [Description("Add a C# constructor to a containing type in the monitor-owned Working candidate.")]
    public RoslynEditResult AddConstructor(string path, string containingType, string declaration, string? afterSymbol = null, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.AddConstructor(fullPath, containingType, declaration, afterSymbol, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "add-constructor", result);
        return result;
    }

    [McpServerTool]
    [Description("Add a C# nested type to a containing type in the monitor-owned Working candidate.")]
    public RoslynEditResult AddNestedType(string path, string containingType, string declaration, string? afterSymbol = null, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.AddNestedType(fullPath, containingType, declaration, afterSymbol, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "add-nested-type", result);
        return result;
    }

    [McpServerTool]
    [Description("Remove one C# symbol from the monitor-owned Working candidate using a Roslyn selector.")]
    public RoslynEditResult RemoveSymbol(string path, string symbolSelectorJson, string? sessionId = null, string? manifestJson = null)
    {
        runtimeState.Touch();
        string fullPath = ResolveWatchedPath(path);
        EnsurePlannedMutationAllowed(sessionId, fullPath);
        bool deferOverlayValidation = ShouldDeferPlannedOverlayValidation(sessionId, fullPath);
        RoslynEditResult result = roslynEditService.RemoveSymbol(fullPath, symbolSelectorJson, manifestJson, !deferOverlayValidation);
        RecordRoslynSessionEvent(sessionId, "remove-symbol", result);
        return result;
    }

}
