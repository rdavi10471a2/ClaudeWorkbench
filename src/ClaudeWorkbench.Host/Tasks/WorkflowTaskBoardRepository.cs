using Microsoft.Data.Sqlite;

namespace ClaudeWorkbench.Host.Tasks;

public sealed class WorkflowTaskBoardRepository
{
    private readonly WorkflowTaskBoardDatabase database;
    private readonly string taskMemoryRoot;

    public WorkflowTaskBoardRepository(string databasePath, string taskMemoryRoot)
    {
        database = new WorkflowTaskBoardDatabase(databasePath);
        this.taskMemoryRoot = Path.GetFullPath(taskMemoryRoot);
    }

    public string DatabasePath => database.DatabasePath;

    public string TaskMemoryRoot => taskMemoryRoot;

    public void EnsureCreated()
    {
        Directory.CreateDirectory(taskMemoryRoot);
        database.EnsureCreated();
        SeedInitializationTaskIfEmpty();
    }

    public WorkflowTaskBoardSnapshot LoadSnapshot()
    {
        EnsureCreated();
        using (SqliteConnection connection = database.OpenConnection())
        {
            IReadOnlyList<WorkflowTaskRow> tasks = LoadTasks(connection);
            if (ReconcileAgentNoteFiles(connection, tasks))
            {
                tasks = LoadTasks(connection);
            }

            return new WorkflowTaskBoardSnapshot(
                LoadStates(connection),
                LoadEventTypes(connection),
                tasks,
                LoadFiles(connection),
                LoadEvents(connection));
        }
    }

    public WorkflowTaskRow CreateTask(string name, string? shortName, string? description, string? notesMarkdown)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Task name is required.", nameof(name));
        }

        EnsureCreated();
        string taskId = CreateId();
        string normalizedShortName = NormalizeShortName(shortName, name, taskId);
        string slug = CreateSlug(normalizedShortName, taskId);
        DateTime now = DateTime.Now;
        int taskNumber;
        using (SqliteConnection connection = database.OpenConnection())
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                taskNumber = GetNextTaskNumber(connection, transaction);
                string stateCode = HasAnyActiveTask(connection, transaction) ? "Proposed" : "Active";
                string? notesPath = null;
                if (!string.IsNullOrWhiteSpace(notesMarkdown))
                {
                    notesPath = GetTaskNotesPath(taskId, taskNumber, slug, TaskNoteKind.User);
                    File.WriteAllText(notesPath, notesMarkdown);
                }

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        insert into workflow_tasks(id, task_number, name, description, short_name, slug, state_code, notes_markdown_path, created_at, updated_at, activated_at)
                        values ($id, $taskNumber, $name, $description, $shortName, $slug, $stateCode, $notesMarkdownPath, $createdAt, $updatedAt, $activatedAt);
                        """;
                    command.Parameters.AddWithValue("$id", taskId);
                    command.Parameters.AddWithValue("$taskNumber", taskNumber);
                    command.Parameters.AddWithValue("$name", name.Trim());
                    AddNullable(command, "$description", string.IsNullOrWhiteSpace(description) ? null : description.Trim());
                    command.Parameters.AddWithValue("$shortName", normalizedShortName);
                    command.Parameters.AddWithValue("$slug", slug);
                    command.Parameters.AddWithValue("$stateCode", stateCode);
                    AddNullable(command, "$notesMarkdownPath", notesPath);
                    command.Parameters.AddWithValue("$createdAt", now);
                    command.Parameters.AddWithValue("$updatedAt", now);
                    if (stateCode.Equals("Active", StringComparison.Ordinal))
                    {
                        command.Parameters.AddWithValue("$activatedAt", now);
                    }
                    else
                    {
                        command.Parameters.AddWithValue("$activatedAt", DBNull.Value);
                    }

                    command.ExecuteNonQuery();
                }

                InsertEvent(connection, taskId, "Created", "Task created.", null, now, transaction);
                transaction.Commit();
            }

            return LoadTask(connection, taskId);
        }
    }

    private void SeedInitializationTaskIfEmpty()
    {
        using (SqliteConnection connection = database.OpenConnection())
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                if (HasAnyTasks(connection, transaction))
                {
                    transaction.Commit();
                    return;
                }

                string taskId = CreateId();
                DateTime now = DateTime.Now;
                int taskNumber = GetNextTaskNumber(connection, transaction);
                string name = "Initialize Workflow From Task Model";
                string shortName = NormalizeShortName("InitializeWorkflow", name, taskId);
                string slug = CreateSlug(shortName, taskId);
                string notesPath = GetTaskNotesPath(taskId, taskNumber, slug, TaskNoteKind.User);
                File.WriteAllText(
                    notesPath,
                    """
                    # Initialize Workflow From Task Model

                    This task was created automatically when the workspace task board was initialized.

                    Use it to shape the task-driven workflow, current-task context loading, and task memory update loop.
                    """);

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        insert into workflow_tasks(id, task_number, name, description, short_name, slug, state_code, notes_markdown_path, created_at, updated_at, activated_at)
                        values ($id, $taskNumber, $name, $description, $shortName, $slug, 'Active', $notesMarkdownPath, $createdAt, $updatedAt, $activatedAt);
                        """;
                    command.Parameters.AddWithValue("$id", taskId);
                    command.Parameters.AddWithValue("$taskNumber", taskNumber);
                    command.Parameters.AddWithValue("$name", name);
                    command.Parameters.AddWithValue("$description", "Default placeholder task for initializing workflow behavior in this workspace.");
                    command.Parameters.AddWithValue("$shortName", shortName);
                    command.Parameters.AddWithValue("$slug", slug);
                    command.Parameters.AddWithValue("$notesMarkdownPath", notesPath);
                    command.Parameters.AddWithValue("$createdAt", now);
                    command.Parameters.AddWithValue("$updatedAt", now);
                    command.Parameters.AddWithValue("$activatedAt", now);
                    command.ExecuteNonQuery();
                }

                InsertEvent(
                    connection,
                    taskId,
                    "Created",
                    "Workspace task board initialized.",
                    null,
                    now,
                    transaction);
                transaction.Commit();
            }
        }
    }

    public int GetNextTaskNumberPreview()
    {
        EnsureCreated();
        using (SqliteConnection connection = database.OpenConnection())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "select current_value + 1 from workflow_task_sequences where name = 'task';";
                return Convert.ToInt32(command.ExecuteScalar() ?? 1);
            }
        }
    }

    public WorkflowTaskRow UpdateTaskDetails(string taskId, string name, string? shortName, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Task name is required.", nameof(name));
        }

        EnsureCreated();
        DateTime now = DateTime.Now;

        using (SqliteConnection connection = database.OpenConnection())
        {
            WorkflowTaskRow existing = LoadTask(connection, taskId);
            string normalizedShortName = NormalizeShortName(shortName, name, taskId);
            string slug = CreateSlug(normalizedShortName, taskId);
            string? notesPath = existing.NotesMarkdownPath;
            if (!string.IsNullOrWhiteSpace(notesPath) && !existing.Slug.Equals(slug, StringComparison.Ordinal))
            {
                string newNotesPath = GetTaskNotesPath(taskId, existing.TaskNumber, slug, TaskNoteKind.User);
                if (File.Exists(notesPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newNotesPath) ?? ".");
                    File.Move(notesPath, newNotesPath, true);
                }

                notesPath = newNotesPath;
            }

            string? agentNotesPath = existing.AgentNotesMarkdownPath;
            if (!string.IsNullOrWhiteSpace(agentNotesPath) && !existing.Slug.Equals(slug, StringComparison.Ordinal))
            {
                string newAgentNotesPath = GetTaskNotesPath(taskId, existing.TaskNumber, slug, TaskNoteKind.Agent);
                if (File.Exists(agentNotesPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newAgentNotesPath) ?? ".");
                    File.Move(agentNotesPath, newAgentNotesPath, true);
                }

                agentNotesPath = newAgentNotesPath;
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    update workflow_tasks
                    set name = $name,
                        description = $description,
                        short_name = $shortName,
                        slug = $slug,
                        notes_markdown_path = $notesMarkdownPath,
                        agent_notes_markdown_path = $agentNotesMarkdownPath,
                        updated_at = $updatedAt
                    where id = $id;
                    """;
                command.Parameters.AddWithValue("$id", taskId);
                command.Parameters.AddWithValue("$name", name.Trim());
                AddNullable(command, "$description", string.IsNullOrWhiteSpace(description) ? null : description.Trim());
                command.Parameters.AddWithValue("$shortName", normalizedShortName);
                command.Parameters.AddWithValue("$slug", slug);
                AddNullable(command, "$notesMarkdownPath", notesPath);
                AddNullable(command, "$agentNotesMarkdownPath", agentNotesPath);
                command.Parameters.AddWithValue("$updatedAt", now);
                int updated = command.ExecuteNonQuery();
                if (updated == 0)
                {
                    throw new InvalidOperationException("Task was not found: " + taskId);
                }
            }

            InsertEvent(connection, taskId, "DetailsUpdated", "Task details updated.", null, now);
            return LoadTask(connection, taskId);
        }
    }

    public WorkflowTaskRow MoveTask(string taskId, string stateCode)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("Task id is required.", nameof(taskId));
        }

        if (string.IsNullOrWhiteSpace(stateCode))
        {
            throw new ArgumentException("Task state is required.", nameof(stateCode));
        }

        EnsureCreated();
        using (SqliteConnection connection = database.OpenConnection())
        {
            EnsureStateExists(connection, stateCode);
            WorkflowTaskRow existing = LoadTask(connection, taskId);
            DateTime now = DateTime.Now;
            if (stateCode.Equals("Active", StringComparison.Ordinal) && HasOtherActiveTask(connection, taskId))
            {
                throw new InvalidOperationException("Only one task can be Active. Move the current Active task first.");
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    update workflow_tasks
                    set state_code = $stateCode,
                        is_archived = 0,
                        archived_at = null,
                        updated_at = $updatedAt,
                        activated_at = case when $stateCode = 'Active' then coalesce(activated_at, $updatedAt) else activated_at end,
                        completed_at = case when $stateCode in ('Done', 'Blocked') then coalesce(completed_at, $updatedAt) else null end
                    where id = $id;
                    """;
                command.Parameters.AddWithValue("$id", taskId);
                command.Parameters.AddWithValue("$stateCode", stateCode);
                command.Parameters.AddWithValue("$updatedAt", now);
                int updated = command.ExecuteNonQuery();
                if (updated == 0)
                {
                    throw new InvalidOperationException("Task was not found: " + taskId);
                }
            }

            InsertEvent(connection, taskId, "StateChanged", "Task moved to " + stateCode + ".", null, now);
            return LoadTask(connection, taskId);
        }
    }

    public WorkflowTaskRow ArchiveTask(string taskId)
    {
        EnsureCreated();
        using (SqliteConnection connection = database.OpenConnection())
        {
            WorkflowTaskRow existing = LoadTask(connection, taskId);
            if (existing.StateCode.Equals("Active", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Move this task out of Active before archiving it.");
            }

            DateTime now = DateTime.Now;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    update workflow_tasks
                    set is_archived = 1,
                        archived_at = $archivedAt,
                        updated_at = $updatedAt
                    where id = $id;
                    """;
                command.Parameters.AddWithValue("$id", taskId);
                command.Parameters.AddWithValue("$archivedAt", now);
                command.Parameters.AddWithValue("$updatedAt", now);
                int updated = command.ExecuteNonQuery();
                if (updated == 0)
                {
                    throw new InvalidOperationException("Task was not found: " + taskId);
                }
            }

            InsertEvent(connection, taskId, "Archived", "Task archived.", null, now);
            return LoadTask(connection, taskId);
        }
    }

    public WorkflowTaskRow RestoreTask(string taskId)
    {
        EnsureCreated();
        using (SqliteConnection connection = database.OpenConnection())
        {
            DateTime now = DateTime.Now;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    update workflow_tasks
                    set is_archived = 0,
                        archived_at = null,
                        updated_at = $updatedAt
                    where id = $id;
                    """;
                command.Parameters.AddWithValue("$id", taskId);
                command.Parameters.AddWithValue("$updatedAt", now);
                int updated = command.ExecuteNonQuery();
                if (updated == 0)
                {
                    throw new InvalidOperationException("Task was not found: " + taskId);
                }
            }

            InsertEvent(connection, taskId, "Restored", "Task restored.", null, now);
            return LoadTask(connection, taskId);
        }
    }

    public WorkflowTaskRow UpdateNotes(string taskId, string notesMarkdown)
    {
        EnsureCreated();
        DateTime now = DateTime.Now;

        using (SqliteConnection connection = database.OpenConnection())
        {
            WorkflowTaskRow existing = LoadTask(connection, taskId);
            string notesPath = GetTaskNotesPath(taskId, existing.TaskNumber, existing.Slug, TaskNoteKind.User);
            File.WriteAllText(notesPath, notesMarkdown ?? string.Empty);

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    update workflow_tasks
                    set notes_markdown_path = $notesMarkdownPath,
                        updated_at = $updatedAt
                    where id = $id;
                    """;
                command.Parameters.AddWithValue("$id", taskId);
                command.Parameters.AddWithValue("$notesMarkdownPath", notesPath);
                command.Parameters.AddWithValue("$updatedAt", now);
                int updated = command.ExecuteNonQuery();
                if (updated == 0)
                {
                    throw new InvalidOperationException("Task was not found: " + taskId);
                }
            }

            InsertEvent(connection, taskId, "NotesUpdated", "Task notes updated.", null, now);
            return LoadTask(connection, taskId);
        }
    }

    public ArchivedDiscussionRow SaveArchivedDiscussion(
        string name,
        string markdownPath,
        string? threadId,
        string turnMode,
        string trigger)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Archived discussion name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(markdownPath))
        {
            throw new ArgumentException("Archived discussion markdown path is required.", nameof(markdownPath));
        }

        EnsureCreated();
        string id = CreateId();
        DateTime now = DateTime.Now;
        using (SqliteConnection connection = database.OpenConnection())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    insert into workflow_archived_discussions(id, name, markdown_path, thread_id, turn_mode, trigger, created_at)
                    values ($id, $name, $markdownPath, $threadId, $turnMode, $trigger, $createdAt);
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$name", name.Trim());
                command.Parameters.AddWithValue("$markdownPath", Path.GetFullPath(markdownPath));
                AddNullable(command, "$threadId", threadId);
                command.Parameters.AddWithValue("$turnMode", string.IsNullOrWhiteSpace(turnMode) ? "Discuss" : turnMode);
                command.Parameters.AddWithValue("$trigger", string.IsNullOrWhiteSpace(trigger) ? "Manual" : trigger);
                command.Parameters.AddWithValue("$createdAt", now);
                command.ExecuteNonQuery();
            }

            return new ArchivedDiscussionRow(
                id,
                name.Trim(),
                Path.GetFullPath(markdownPath),
                string.IsNullOrWhiteSpace(threadId) ? null : threadId.Trim(),
                string.IsNullOrWhiteSpace(turnMode) ? "Discuss" : turnMode,
                string.IsNullOrWhiteSpace(trigger) ? "Manual" : trigger,
                now);
        }
    }

    public IReadOnlyList<ArchivedDiscussionRow> ListArchivedDiscussions()
    {
        EnsureCreated();
        using (SqliteConnection connection = database.OpenConnection())
        {
            List<ArchivedDiscussionRow> rows = [];
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    select id, name, markdown_path, thread_id, turn_mode, trigger, created_at
                    from workflow_archived_discussions
                    order by created_at desc, id desc;
                    """;
                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(new ArchivedDiscussionRow(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5),
                            reader.GetDateTime(6)));
                    }
                }
            }

            return rows;
        }
    }

    public WorkflowTaskFileRow AddFile(string taskId, string relativePath, string? intent, string? fileRole)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Task file path is required.", nameof(relativePath));
        }

        EnsureCreated();
        string id = CreateId();
        DateTime now = DateTime.Now;
        using (SqliteConnection connection = database.OpenConnection())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    insert into workflow_task_files(id, task_id, relative_path, intent, file_role)
                    values ($id, $taskId, $relativePath, $intent, $fileRole)
                    on conflict(task_id, relative_path) do update set
                        intent = excluded.intent,
                        file_role = excluded.file_role;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$taskId", taskId);
                command.Parameters.AddWithValue("$relativePath", NormalizeRelativePath(relativePath));
                AddNullable(command, "$intent", intent);
                AddNullable(command, "$fileRole", fileRole);
                command.ExecuteNonQuery();
            }

            TouchTask(connection, taskId, now);
            InsertEvent(connection, taskId, "FileAdded", "Task file added: " + NormalizeRelativePath(relativePath), null, now);
            return LoadFiles(connection).First(file =>
                file.TaskId.Equals(taskId, StringComparison.Ordinal)
                && file.RelativePath.Equals(NormalizeRelativePath(relativePath), StringComparison.OrdinalIgnoreCase));
        }
    }

    public WorkflowTaskEventRow AddComment(string taskId, string message)
    {
        EnsureCreated();
        DateTime now = DateTime.Now;
        using (SqliteConnection connection = database.OpenConnection())
        {
            WorkflowTaskEventRow row = InsertEvent(connection, taskId, "Comment", message, null, now);
            TouchTask(connection, taskId, now);
            return row;
        }
    }

    public string ReadNotes(string? notesMarkdownPath)
    {
        if (string.IsNullOrWhiteSpace(notesMarkdownPath) || !File.Exists(notesMarkdownPath))
        {
            return string.Empty;
        }

        return File.ReadAllText(notesMarkdownPath);
    }

    public WorkflowTaskRow UpdateAgentNotes(string taskId, string notesMarkdown)
    {
        EnsureCreated();
        DateTime now = DateTime.Now;

        using (SqliteConnection connection = database.OpenConnection())
        {
            WorkflowTaskRow existing = LoadTask(connection, taskId);
            string notesPath = GetTaskNotesPath(taskId, existing.TaskNumber, existing.Slug, TaskNoteKind.Agent);
            File.WriteAllText(notesPath, notesMarkdown ?? string.Empty);

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    update workflow_tasks
                    set agent_notes_markdown_path = $agentNotesMarkdownPath,
                        updated_at = $updatedAt
                    where id = $id;
                    """;
                command.Parameters.AddWithValue("$id", taskId);
                command.Parameters.AddWithValue("$agentNotesMarkdownPath", notesPath);
                command.Parameters.AddWithValue("$updatedAt", now);
                int updated = command.ExecuteNonQuery();
                if (updated == 0)
                {
                    throw new InvalidOperationException("Task was not found: " + taskId);
                }
            }

            InsertEvent(connection, taskId, "AgentNotesUpdated", "Agent notes updated.", null, now);
            return LoadTask(connection, taskId);
        }
    }

    private string GetTaskNotesPath(string taskId, int taskNumber, string slug, TaskNoteKind kind)
    {
        string folderName = "task-" + FormatTaskNumber(taskNumber);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            folderName += "-" + slug;
        }

        string taskFolder = Path.Combine(taskMemoryRoot, folderName);
        Directory.CreateDirectory(taskFolder);
        string fileName = taskId + "-" + (kind == TaskNoteKind.User ? "user" : "agent") + ".md";
        return Path.Combine(taskFolder, fileName);
    }

    private bool ReconcileAgentNoteFiles(SqliteConnection connection, IReadOnlyList<WorkflowTaskRow> tasks)
    {
        bool changed = false;
        foreach (WorkflowTaskRow task in tasks)
        {
            string? notesPath = FindExistingTaskNotesPath(task, TaskNoteKind.Agent);
            if (string.IsNullOrWhiteSpace(notesPath))
            {
                continue;
            }

            if (string.Equals(task.AgentNotesMarkdownPath, notesPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTime now = DateTime.Now;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    update workflow_tasks
                    set agent_notes_markdown_path = $agentNotesMarkdownPath,
                        updated_at = $updatedAt
                    where id = $id;
                    """;
                command.Parameters.AddWithValue("$id", task.Id);
                command.Parameters.AddWithValue("$agentNotesMarkdownPath", notesPath);
                command.Parameters.AddWithValue("$updatedAt", now);
                int updated = command.ExecuteNonQuery();
                if (updated == 0)
                {
                    throw new InvalidOperationException("Task was not found: " + task.Id);
                }
            }

            InsertEvent(connection, task.Id, "AgentNotesUpdated", "Agent notes linked from task-memory file.", null, now);
            changed = true;
        }

        return changed;
    }

    private string? FindExistingTaskNotesPath(WorkflowTaskRow task, TaskNoteKind kind)
    {
        string expectedPath = GetTaskNotesPathWithoutCreatingDirectory(task.Id, task.TaskNumber, task.Slug, kind);
        if (File.Exists(expectedPath))
        {
            return expectedPath;
        }

        if (!Directory.Exists(taskMemoryRoot))
        {
            return null;
        }

        string fileName = task.Id + "-" + (kind == TaskNoteKind.User ? "user" : "agent") + ".md";
        return Directory
            .EnumerateFiles(taskMemoryRoot, fileName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private string GetTaskNotesPathWithoutCreatingDirectory(string taskId, int taskNumber, string slug, TaskNoteKind kind)
    {
        string folderName = "task-" + FormatTaskNumber(taskNumber);
        if (!string.IsNullOrWhiteSpace(slug))
        {
            folderName += "-" + slug;
        }

        string fileName = taskId + "-" + (kind == TaskNoteKind.User ? "user" : "agent") + ".md";
        return Path.Combine(taskMemoryRoot, folderName, fileName);
    }

    private static IReadOnlyList<WorkflowTaskStateRow> LoadStates(SqliteConnection connection)
    {
        List<WorkflowTaskStateRow> rows = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "select code, name, sort_order, is_terminal from workflow_task_states order by sort_order;";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add(new WorkflowTaskStateRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3) != 0));
                }
            }
        }

        return rows;
    }

    private static IReadOnlyList<WorkflowTaskEventTypeRow> LoadEventTypes(SqliteConnection connection)
    {
        List<WorkflowTaskEventTypeRow> rows = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "select code, name, sort_order from workflow_task_event_types order by sort_order;";
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add(new WorkflowTaskEventTypeRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt32(2)));
                }
            }
        }

        return rows;
    }

    private static IReadOnlyList<WorkflowTaskRow> LoadTasks(SqliteConnection connection)
    {
        List<WorkflowTaskRow> rows = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                select t.id, t.task_number, t.name, t.description, t.state_code, s.name, t.notes_markdown_path,
                       t.agent_notes_markdown_path, t.short_name, t.slug, t.is_archived,
                       t.created_at, t.updated_at, t.activated_at, t.completed_at, t.archived_at
                from workflow_tasks t
                join workflow_task_states s on s.code = t.state_code
                order by t.is_archived, s.sort_order, t.updated_at desc, t.created_at desc;
                """;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add(ReadTask(reader));
                }
            }
        }

        return rows;
    }

    private static WorkflowTaskRow LoadTask(SqliteConnection connection, string taskId)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                select t.id, t.task_number, t.name, t.description, t.state_code, s.name, t.notes_markdown_path,
                       t.agent_notes_markdown_path, t.short_name, t.slug, t.is_archived,
                       t.created_at, t.updated_at, t.activated_at, t.completed_at, t.archived_at
                from workflow_tasks t
                join workflow_task_states s on s.code = t.state_code
                where t.id = $id;
                """;
            command.Parameters.AddWithValue("$id", taskId);
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    return ReadTask(reader);
                }
            }
        }

        throw new InvalidOperationException("Task was not found: " + taskId);
    }

    private static WorkflowTaskRow ReadTask(SqliteDataReader reader)
    {
        return new WorkflowTaskRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            NormalizeShortName(reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(2), reader.GetString(0)),
            NormalizeSlug(reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(2), reader.GetString(0)),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(10) != 0,
            reader.GetDateTime(11),
            reader.GetDateTime(12),
            reader.IsDBNull(13) ? null : reader.GetDateTime(13),
            reader.IsDBNull(14) ? null : reader.GetDateTime(14),
            reader.IsDBNull(15) ? null : reader.GetDateTime(15));
    }

    private static IReadOnlyList<WorkflowTaskFileRow> LoadFiles(SqliteConnection connection)
    {
        List<WorkflowTaskFileRow> rows = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                select id, task_id, relative_path, intent, file_role
                from workflow_task_files
                order by relative_path;
                """;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add(new WorkflowTaskFileRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
            }
        }

        return rows;
    }

    private static IReadOnlyList<WorkflowTaskEventRow> LoadEvents(SqliteConnection connection)
    {
        List<WorkflowTaskEventRow> rows = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                select e.id, e.task_id, e.event_type_code, et.name, e.message, e.payload_json, e.created_at
                from workflow_task_events e
                join workflow_task_event_types et on et.code = e.event_type_code
                order by e.created_at desc;
                """;
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add(new WorkflowTaskEventRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetDateTime(6)));
                }
            }
        }

        return rows;
    }

    private static WorkflowTaskEventRow InsertEvent(
        SqliteConnection connection,
        string taskId,
        string eventTypeCode,
        string? message,
        string? payloadJson,
        DateTime createdAt,
        SqliteTransaction? transaction = null)
    {
        string id = CreateId();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into workflow_task_events(id, task_id, event_type_code, message, payload_json, created_at)
                values ($id, $taskId, $eventTypeCode, $message, $payloadJson, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$taskId", taskId);
            command.Parameters.AddWithValue("$eventTypeCode", eventTypeCode);
            AddNullable(command, "$message", message);
            AddNullable(command, "$payloadJson", payloadJson);
            command.Parameters.AddWithValue("$createdAt", createdAt);
            command.ExecuteNonQuery();
        }

        return new WorkflowTaskEventRow(id, taskId, eventTypeCode, eventTypeCode, message, payloadJson, createdAt);
    }

    private static void TouchTask(SqliteConnection connection, string taskId, DateTime now)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "update workflow_tasks set updated_at = $updatedAt where id = $id;";
            command.Parameters.AddWithValue("$id", taskId);
            command.Parameters.AddWithValue("$updatedAt", now);
            int updated = command.ExecuteNonQuery();
            if (updated == 0)
            {
                throw new InvalidOperationException("Task was not found: " + taskId);
            }
        }
    }

    private static void EnsureStateExists(SqliteConnection connection, string stateCode)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "select count(*) from workflow_task_states where code = $stateCode;";
            command.Parameters.AddWithValue("$stateCode", stateCode);
            long count = Convert.ToInt64(command.ExecuteScalar() ?? 0);
            if (count == 0)
            {
                throw new InvalidOperationException("Task state was not found: " + stateCode);
            }
        }
    }

    private static bool HasOtherActiveTask(
        SqliteConnection connection,
        string excludedTaskId)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                select count(*)
                from workflow_tasks
                where state_code = 'Active'
                  and id <> $id;
                """;
            command.Parameters.AddWithValue("$id", excludedTaskId);
            return Convert.ToInt64(command.ExecuteScalar() ?? 0) > 0;
        }
    }

    private static int GetNextTaskNumber(SqliteConnection connection, SqliteTransaction transaction)
    {
        int current;
        using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "select current_value from workflow_task_sequences where name = 'task';";
            current = Convert.ToInt32(select.ExecuteScalar() ?? 0);
        }

        int next = current + 1;
        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "update workflow_task_sequences set current_value = $value where name = 'task';";
            update.Parameters.AddWithValue("$value", next);
            update.ExecuteNonQuery();
        }

        return next;
    }

    private static bool HasAnyActiveTask(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "select count(*) from workflow_tasks where state_code = 'Active';";
            return Convert.ToInt64(command.ExecuteScalar() ?? 0) > 0;
        }
    }

    private static bool HasAnyTasks(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "select count(*) from workflow_tasks;";
            return Convert.ToInt64(command.ExecuteScalar() ?? 0) > 0;
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(path) || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Task file paths must be relative to the task folder.", nameof(path));
        }

        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => part.Equals("..", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Task file paths cannot be empty or traverse parent folders.", nameof(path));
        }

        return string.Join('/', parts);
    }

    private static string CreateId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string NormalizeShortName(string? shortName, string name, string taskId)
    {
        string source = string.IsNullOrWhiteSpace(shortName) ? name : shortName;
        string normalized = ToPascalIdentifier(source);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return LimitShortName(normalized);
        }

        return "Task" + GetTaskIdSuffix(taskId);
    }

    private static string NormalizeSlug(string? slug, string? shortName, string name, string taskId)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return slug.Trim();
        }

        return CreateSlug(NormalizeShortName(shortName, name, taskId), taskId);
    }

    private static string CreateSlug(string shortName, string taskId)
    {
        string slug = new string(shortName.Where(char.IsLetterOrDigit).ToArray());
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return slug;
        }

        return "task-" + GetTaskIdSuffix(taskId);
    }

    private static string ToPascalIdentifier(string value)
    {
        string[] parts = value
            .Split([' ', '-', '_', '.', '/', '\\', ':', ';', ',', '(', ')', '[', ']', '{', '}'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> normalizedParts = [];
        foreach (string part in parts)
        {
            string cleaned = new string(part.Where(char.IsLetterOrDigit).ToArray());
            if (cleaned.Length == 0)
            {
                continue;
            }

            normalizedParts.Add(char.ToUpperInvariant(cleaned[0]) + cleaned[1..]);
        }

        return string.Concat(normalizedParts);
    }

    private static string LimitShortName(string value)
    {
        if (value.Length <= 20)
        {
            return value;
        }

        return value[..20];
    }

    private static string GetTaskIdSuffix(string taskId)
    {
        if (taskId.Length <= 8)
        {
            return taskId;
        }

        return taskId[..8];
    }

    private static string FormatTaskNumber(int taskNumber)
    {
        return taskNumber.ToString("0000");
    }

    private enum TaskNoteKind
    {
        User,
        Agent
    }

    private static void AddNullable(SqliteCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value);
    }
}
