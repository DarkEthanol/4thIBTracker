using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FourthIBTracker.Services;

namespace FourthIBTracker.ViewModels;

public partial class TodoItemViewModel : ObservableObject
{
    private readonly TodoTask _task;
    private readonly TodoService _store;
    private readonly Action<string?> _refresh;

    [ObservableProperty]
    private bool isDone;

    public string Id => _task.Id;
    public string Title => _task.Title;
    public DateTime DueDate => _task.DueDate;
    public DateTime? CompletedAt => _task.CompletedAt;
    public bool IsOverdue => !IsDone && DueDate.Date < DateTime.Today;
    public bool IsToday => !IsDone && DueDate.Date == DateTime.Today;
    public bool IsRecurring => _task.Recurrence != TodoRecurrence.None;

    public string RecurrenceLabel => _task.Recurrence switch
    {
        TodoRecurrence.Weekly => $"Every {DueDate:dddd}",
        TodoRecurrence.BiWeekly => $"Every 2 weeks on {DueDate:dddd}",
        TodoRecurrence.Monthly => $"Monthly on day {_task.RecurrenceDay}",
        TodoRecurrence.LastDayOfMonth => "Last day of every month",
        _ => "",
    };

    public string DueLabel => IsDone
        ? $"completed {CompletedAt:ddd dd MMM, HH:mm} · due {DueDate:dd MMM}"
        : IsToday
            ? "due TODAY"
            : IsOverdue
                ? $"OVERDUE · was due {DueDate:ddd dd MMM}"
                : $"due {DueDate:ddd dd MMM}";

    public TodoItemViewModel(TodoTask task, TodoService store, Action<string?> refresh)
    {
        _task = task;
        _store = store;
        _refresh = refresh;
        isDone = task.IsCompleted;
    }

    partial void OnIsDoneChanged(bool value)
    {
        var completedDueDate = _task.DueDate;
        var wasRecurring = IsRecurring;
        var nextTask = _store.SetCompleted(_task, value);
        var message = value && wasRecurring && nextTask is not null
            ? $"Completed the {completedDueDate:dd MMM} occurrence. Next due {nextTask.DueDate:ddd dd MMM}."
            : null;
        _refresh(message);
    }
}

public sealed record TodoRecurrenceOption(TodoRecurrence Value, string Label)
{
    // The app's custom ComboBox template displays SelectionBoxItem directly,
    // so provide the friendly label for the selected value as well as the list.
    public override string ToString() => Label;
}

public partial class TodoViewModel : ObservableObject
{
    private readonly TodoService _store = new();
    private readonly DispatcherTimer _dayChangeTimer;
    private DateTime _displayedDate = DateTime.Today;

    public ObservableCollection<TodoItemViewModel> OpenItems { get; } = new();
    public ObservableCollection<TodoItemViewModel> CompletedItems { get; } = new();
    public IReadOnlyList<TodoRecurrenceOption> RecurrenceOptions { get; } =
    [
        new(TodoRecurrence.None, "Does not repeat"),
        new(TodoRecurrence.Weekly, "Weekly · same weekday"),
        new(TodoRecurrence.BiWeekly, "Every 2 weeks · same weekday"),
        new(TodoRecurrence.Monthly, "Monthly · same date"),
        new(TodoRecurrence.LastDayOfMonth, "Monthly · last day"),
    ];

    [ObservableProperty]
    private string newTaskTitle = "";

    [ObservableProperty]
    private DateTime? newTaskDueDate = DateTime.Today;

    [ObservableProperty]
    private TodoRecurrence newTaskRecurrence;

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverdue))]
    private int overdueCount;

    public bool HasOverdue => OverdueCount > 0;

    public TodoViewModel()
    {
        _store.Load();
        Refresh();

        // Keep the sidebar warning accurate if the app remains open overnight.
        _dayChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _dayChangeTimer.Tick += (_, _) =>
        {
            if (_displayedDate != DateTime.Today)
                Refresh();
        };
        _dayChangeTimer.Start();
    }

    [RelayCommand]
    private void AddTask()
    {
        var title = NewTaskTitle.Trim();
        if (title.Length == 0)
        {
            StatusMessage = "Enter a task name.";
            return;
        }

        if (NewTaskDueDate is not DateTime dueDate)
        {
            StatusMessage = "Choose a due date.";
            return;
        }

        _store.Add(title, dueDate, NewTaskRecurrence);
        NewTaskTitle = "";
        NewTaskDueDate = DateTime.Today;
        NewTaskRecurrence = TodoRecurrence.None;
        StatusMessage = "";
        Refresh();
    }

    [RelayCommand]
    private void DeleteTask(TodoItemViewModel? item)
    {
        if (item is null) return;

        var result = MessageBox.Show(
            $"Delete ‘{item.Title}’?\n\nThis cannot be undone.",
            "Delete task",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _store.Delete(item.Id);
        StatusMessage = "";
        Refresh();
    }

    private void RefreshAfterTaskChange(string? message)
    {
        Refresh();
        StatusMessage = message ?? "";
    }

    [RelayCommand]
    public void Refresh()
    {
        _displayedDate = DateTime.Today;
        OpenItems.Clear();
        CompletedItems.Clear();

        var items = _store.Tasks
            .Select(task => new TodoItemViewModel(task, _store, RefreshAfterTaskChange))
            .ToList();

        foreach (var item in items
                     .Where(item => !item.IsDone)
                     .OrderBy(item => item.DueDate)
                     .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
            OpenItems.Add(item);

        foreach (var item in items
                     .Where(item => item.IsDone)
                     .OrderByDescending(item => item.CompletedAt)
                     .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
            CompletedItems.Add(item);

        OverdueCount = OpenItems.Count(item => item.IsOverdue);
    }
}
