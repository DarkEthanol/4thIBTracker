using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FourthIBTracker.Services;

public enum TodoRecurrence
{
    None,
    Weekly,
    BiWeekly,
    Monthly,
    LastDayOfMonth,
}

public sealed class TodoTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime DueDate { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TodoRecurrence Recurrence { get; set; }
    public int RecurrenceDay { get; set; }
    public string? NextTaskId { get; set; }
}

internal sealed class TodoStore
{
    public int Version { get; set; } = 3;
    public List<TodoTask> Tasks { get; set; } = new();
}

/// <summary>
/// Stores user-created tasks in %APPDATA%\4thIBTracker\todos.json.
/// The former recurring-task history is deliberately left untouched in
/// todo-history.json, but it is not used to seed the new task list.
/// </summary>
public class TodoService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "4thIBTracker", "todos.json");

    private TodoStore _store = new();

    public IReadOnlyList<TodoTask> Tasks => _store.Tasks;

    public void Load()
    {
        if (!File.Exists(StorePath))
        {
            _store = new TodoStore();
            return;
        }

        try
        {
            _store = JsonSerializer.Deserialize<TodoStore>(
                File.ReadAllText(StorePath), JsonOptions) ?? new TodoStore();
            _store.Tasks ??= new List<TodoTask>();
            _store.Version = 3;

            // Ignore incomplete records rather than letting one malformed task
            // prevent the rest of the user's list from loading.
            _store.Tasks.RemoveAll(task =>
                string.IsNullOrWhiteSpace(task.Id) ||
                string.IsNullOrWhiteSpace(task.Title));

            foreach (var task in _store.Tasks)
            {
                task.Title = task.Title.Trim();
                task.DueDate = task.DueDate.Date;
                if (task.RecurrenceDay is < 1 or > 31)
                    task.RecurrenceDay = task.DueDate.Day;
            }
        }
        catch (JsonException)
        {
            _store = new TodoStore();
        }
        catch (IOException)
        {
            _store = new TodoStore();
        }
    }

    public TodoTask Add(string title, DateTime dueDate, TodoRecurrence recurrence)
    {
        dueDate = dueDate.Date;
        if (recurrence == TodoRecurrence.LastDayOfMonth)
            dueDate = LastDayOfMonth(dueDate.Year, dueDate.Month);

        var task = new TodoTask
        {
            Title = title.Trim(),
            DueDate = dueDate,
            Recurrence = recurrence,
            RecurrenceDay = dueDate.Day,
        };
        _store.Tasks.Add(task);
        Save();
        return task;
    }

    public TodoTask? SetCompleted(TodoTask task, bool completed)
    {
        if (completed && task.Recurrence != TodoRecurrence.None)
        {
            task.IsCompleted = true;
            task.CompletedAt = DateTime.Now;

            // Keep the finished occurrence as history and create a separate
            // next occurrence. The link prevents an uncheck/recheck of an old
            // occurrence from creating duplicate future tasks.
            var nextTask = string.IsNullOrWhiteSpace(task.NextTaskId)
                ? null
                : _store.Tasks.FirstOrDefault(candidate => candidate.Id == task.NextTaskId);
            if (nextTask is null)
            {
                nextTask = new TodoTask
                {
                    Title = task.Title,
                    DueDate = GetNextDueDate(task),
                    Recurrence = task.Recurrence,
                    RecurrenceDay = task.RecurrenceDay,
                };
                _store.Tasks.Add(nextTask);
                task.NextTaskId = nextTask.Id;
            }

            Save();
            return nextTask;
        }

        task.IsCompleted = completed;
        task.CompletedAt = completed ? DateTime.Now : null;
        Save();
        return null;
    }

    public static DateTime GetNextDueDate(TodoTask task)
    {
        var current = task.DueDate.Date;
        return task.Recurrence switch
        {
            TodoRecurrence.Weekly => current.AddDays(7),
            TodoRecurrence.BiWeekly => current.AddDays(14),
            TodoRecurrence.Monthly => SameDayNextMonth(current, task.RecurrenceDay),
            TodoRecurrence.LastDayOfMonth => LastDayNextMonth(current),
            _ => current,
        };
    }

    private static DateTime SameDayNextMonth(DateTime current, int preferredDay)
    {
        var nextMonth = new DateTime(current.Year, current.Month, 1).AddMonths(1);
        var day = Math.Min(Math.Clamp(preferredDay, 1, 31),
            DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
        return new DateTime(nextMonth.Year, nextMonth.Month, day);
    }

    private static DateTime LastDayNextMonth(DateTime current)
    {
        var nextMonth = new DateTime(current.Year, current.Month, 1).AddMonths(1);
        return LastDayOfMonth(nextMonth.Year, nextMonth.Month);
    }

    private static DateTime LastDayOfMonth(int year, int month) =>
        new(year, month, DateTime.DaysInMonth(year, month));

    public void Delete(string taskId)
    {
        _store.Tasks.RemoveAll(task => task.Id == taskId);
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(_store, JsonOptions));
    }
}
