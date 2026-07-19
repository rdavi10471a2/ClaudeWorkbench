using System.Text;
using System.Text.Json;
using ClaudeWorkbench.Host.Console;

namespace ClaudeWorkbench.Host.Services;

// Turns a raw gate (tool name + JSON input) into a human-readable approval:
// an action title, a set of labelled detail rows (code-like fields rendered as
// multi-line blocks), and a pretty-printed copy of the raw payload for the
// collapsible view. Resilient to unknown tools and field names.
public static class ApprovalFormatter
{
    private static readonly HashSet<string> CodeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "declaration", "content", "text", "newText", "oldText", "replacement",
        "code", "body", "source", "snippet",
    };

    private static readonly HashSet<string> PathFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "sourceFilePath", "filePath", "file", "targetPath",
    };

    private static readonly string[] SearchFieldOrder = ["pattern", "query", "q"];
    private static readonly string[] PathFieldOrder = ["file_path", "path", "sourceFilePath", "filePath", "file", "targetPath"];
    private static readonly string[] SymbolFieldOrder = ["afterSymbol", "name", "symbol", "containingType"];

    // Compact one-line label for a tool call in the transcript/activity feed:
    // the tool name plus its most informative argument (search term, file name,
    // or symbol). Falls back to just the tool name.
    public static string ShortLabel(string tool, System.Text.Json.JsonElement? input)
    {
        string? detail = PrimaryDetail(input);
        return string.IsNullOrEmpty(detail) ? tool : $"{tool}: {detail}";
    }

    private static string? PrimaryDetail(System.Text.Json.JsonElement? input)
    {
        if (input is not System.Text.Json.JsonElement element
            || element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        foreach (string key in SearchFieldOrder)
        {
            if (TryGetNonEmptyString(element, key, out string term))
            {
                return Shorten(term);
            }
        }

        foreach (string key in PathFieldOrder)
        {
            if (TryGetNonEmptyString(element, key, out string filePath))
            {
                return Path.GetFileName(filePath.TrimEnd('/', '\\'));
            }
        }

        foreach (string key in SymbolFieldOrder)
        {
            if (TryGetNonEmptyString(element, key, out string symbol))
            {
                return Shorten(symbol);
            }
        }

        return null;
    }

    private static bool TryGetNonEmptyString(System.Text.Json.JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.TryGetProperty(name, out System.Text.Json.JsonElement property)
            && property.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            string? text = property.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                value = text;
                return true;
            }
        }

        return false;
    }

    private static string Shorten(string value)
    {
        string collapsed = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return collapsed.Length > 48 ? collapsed[..45] + "…" : collapsed;
    }

    public static (string Title, IReadOnlyList<ApprovalDetail> Details, string? PrettyJson) Describe(
        string tool,
        string? target,
        string? inputJson)
    {
        List<ApprovalDetail> details = new();
        if (!string.IsNullOrWhiteSpace(target))
        {
            details.Add(new ApprovalDetail("File", target, false));
        }

        string? pretty = inputJson;
        if (!string.IsNullOrWhiteSpace(inputJson))
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(inputJson))
                {
                    pretty = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        AppendFieldDetails(document.RootElement, target, details);
                    }
                }
            }
            catch (JsonException)
            {
                // Leave pretty as the raw string; details keep whatever we resolved.
            }
        }

        return (TitleFor(tool), details, pretty);
    }

    private static void AppendFieldDetails(JsonElement root, string? target, List<ApprovalDetail> details)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "sessionId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PathFields.Contains(property.Name))
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    details.Add(new ApprovalDetail("File", ValueText(property.Value), false));
                }

                continue;
            }

            string value = ValueText(property.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            bool isCode = CodeFields.Contains(property.Name) || value.Contains('\n');
            details.Add(new ApprovalDetail(Humanize(property.Name), value, isCode));
        }
    }

    private static string ValueText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => element.ToString(),
        };
    }

    private static string TitleFor(string tool)
    {
        return tool switch
        {
            "new_file" => "Create a new file",
            "refresh_file" => "Load a file into the working copy for editing",
            "refresh_file_and_index" => "Load a file and refresh its index",
            "add_method" => "Add a method",
            "add_property" => "Add a property",
            "add_field" => "Add a field",
            "add_constructor" => "Add a constructor",
            "add_using" => "Add a using directive",
            "add_symbol" => "Add a symbol",
            "add_nested_type" => "Add a nested type",
            "submit_file" => "Replace the full file contents",
            "submit_symbol" => "Replace a symbol",
            "replace_text_in_file" => "Replace text in the file",
            "replace_span_in_file" => "Replace a span of text",
            "remove_symbol" => "Remove a symbol",
            "remove_using" => "Remove a using directive",
            "set_type_partial" => "Make a type partial",
            "stage_candidate_for_review" => "Stage this file for your review",
            "record_diff_decision" => "Record a review decision",
            "start_monitor_session" => "Start a governed edit session",
            _ => Humanize(tool),
        };
    }

    private static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        StringBuilder builder = new();
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (current == '_' || current == '-')
            {
                if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (char.IsUpper(current) && builder.Length > 0 && builder[builder.Length - 1] != ' ')
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        string spaced = builder.ToString().Trim();
        if (spaced.Length == 0)
        {
            return name;
        }

        return char.ToUpperInvariant(spaced[0]) + spaced.Substring(1);
    }
}
