namespace HiveStatsOverlay;

internal sealed class ObservedTaskCollection
{
    private readonly object gate = new();
    private readonly List<Task> pending = new();

    public void Track(Task task)
    {
        if (task.IsCompleted)
        {
            ObserveFault(task);
            return;
        }

        lock (gate)
            pending.Add(task);

        task.ContinueWith(
            static (completed, state) => ((ObservedTaskCollection)state!).Untrack(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void Untrack(Task task)
    {
        lock (gate)
            pending.Remove(task);
        ObserveFault(task);
    }

    public bool TryWaitAll(TimeSpan timeout)
    {
        Task[] snapshot;
        lock (gate)
            snapshot = pending.ToArray();

        if (snapshot.Length == 0)
            return true;

        try
        {
            return Task.WhenAll(snapshot).Wait(timeout);
        }
        catch (AggregateException aggregate) when (aggregate.InnerExceptions.All(static ex => ex is OperationCanceledException))
        {
            return true;
        }
    }

    private static void ObserveFault(Task task)
    {
        if (!task.IsFaulted || task.Exception is not { } exception)
            return;

        Exception root = exception.InnerException ?? exception;
        Console.WriteLine($"[HiveStatsOverlay] Background task faulted: {root.Message}");
    }
}
