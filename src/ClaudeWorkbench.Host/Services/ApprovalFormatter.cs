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
            "launch_staged_diff" => "Open the staged diff for review",
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
