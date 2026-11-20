namespace Ranks;

internal sealed class PlayerDatabaseQueue<TSave>(
    Func<TSave, Task> write,
    Func<TSave, TSave, TSave?> combine,
    Action<Exception> report,
    IReadOnlyDictionary<ulong, Task>? previous = null) where TSave : struct
{
    // Leave pool capacity for logins and commands during an autosave or mass disconnect.
    internal const int MaxConcurrentWrites = 4;
    private readonly SemaphoreSlim _writers = new(MaxConcurrentWrites);
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Lane> _lanes = new();
    private readonly Dictionary<ulong, Task> _previous = previous == null ? new() : new(previous);
    private bool _sealed;

    private sealed class Lane
    {
        public LinkedList<Work> Pending { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
    }

    private readonly record struct Work(TSave? Save, Func<Task>? Operation);

    public void Save(ulong steamId, TSave snapshot) => Enqueue(steamId, new Work(snapshot, null));

    public void Run(ulong steamId, Func<Task> operation) => Enqueue(steamId, new Work(null, operation));

    private void Enqueue(ulong steamId, Work work)
    {
        lock (_gate)
        {
            if (_sealed) throw new InvalidOperationException("The database queue has been sealed.");
            if (_lanes.TryGetValue(steamId, out var existing))
            {
                if (work.Save is { } newer && existing.Pending.Last is { Value.Save: { } older } tail &&
                    combine(older, newer) is { } combined)
                    tail.Value = new Work(combined, null);
                else
                    existing.Pending.AddLast(work);
                return;
            }

            var lane = new Lane();
            lane.Pending.AddLast(work);
            _lanes.Add(steamId, lane);
            _previous.Remove(steamId, out var predecessor);
            lane.Completion = Task.Run(() => Drain(steamId, lane, predecessor));
        }
    }

    private async Task Drain(ulong steamId, Lane lane, Task? predecessor)
    {
        if (predecessor != null) await predecessor.ConfigureAwait(false);
        while (true)
        {
            Work work;
            lock (_gate)
            {
                if (lane.Pending.First is not { } head)
                {
                    _lanes.Remove(steamId);
                    return;
                }

                work = head.Value;
                lane.Pending.RemoveFirst();
            }

            try
            {
                if (work.Operation is { } operation) await operation().ConfigureAwait(false);
                else if (work.Save is { } snapshot)
                {
                    await _writers.WaitAsync().ConfigureAwait(false);
                    try { await write(snapshot).ConfigureAwait(false); }
                    finally { _writers.Release(); }
                }
            }
            catch (Exception error)
            {
                report(error);
            }
        }
    }

    public Dictionary<ulong, Task> Seal()
    {
        lock (_gate)
        {
            _sealed = true;
            var unfinished = _previous.Where(entry => !entry.Value.IsCompleted)
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            foreach (var (steamId, lane) in _lanes)
                unfinished.Add(steamId, lane.Completion);
            return unfinished;
        }
    }
}
