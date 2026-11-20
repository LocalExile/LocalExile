using GameServer.Modding;

/// <summary>One load's identity. Only the host can stop it; callers can own and release resources.</summary>
public sealed class ModuleLifetime : IModuleLifetime
{
    static long _nextGeneration;
    readonly Lock _gate = new();
    readonly ModuleExecution _execution;
    readonly HashSet<Ownership> _owned = [];
    readonly CancellationTokenSource _stopping = new();
    int _active = 1;

    public ModuleLifetime(string id) : this(id, new ModuleExecution()) { }

    internal ModuleLifetime(string id, ModuleExecution execution)
    {
        _execution = execution;
        Id = id;
        Generation = Interlocked.Increment(ref _nextGeneration);
        // Keep the token readable after the source is disposed at stop.
        Stopping = _stopping.Token;
    }

    public string Id { get; }
    public long Generation { get; }
    public bool IsActive => Volatile.Read(ref _active) != 0;
    public CancellationToken Stopping { get; }

    public IDisposable Own(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var ownership = new Ownership(this, resource);
        lock (_gate)
        {
            if (IsActive)
            {
                _owned.Add(ownership);
                return ownership;
            }
        }
        ownership.Dispose();
        return ownership;
    }

    public bool TryRunSynchronous(Action mutation) => _execution.TryRun(this, mutation);

    // Only Gateway-owned transitions run here, never callbacks into a mod.
    public bool TryCommit(Action commit)
    {
        lock (_gate)
        {
            if (!IsActive) return false;
            commit();
            return true;
        }
    }

    public void Stop(Action<Exception> failed)
    {
        if (_execution.DeferFromMutation(() => Stop(failed))) return;
        Ownership[] owned;
        using (_execution.Change())
        lock (_gate)
        {
            if (!IsActive) return;
            Volatile.Write(ref _active, 0);
            owned = [.. _owned];
            _owned.Clear();
        }

        // Never call module code under the ownership lock. A callback may release or own another resource.
        try { _stopping.Cancel(); }
        catch (Exception ex) { Report(ex); }
        finally { _stopping.Dispose(); }
        foreach (var ownership in owned)
        {
            try { ownership.Dispose(); }
            catch (Exception ex) { Report(ex); }
        }

        void Report(Exception ex)
        {
            // Reporting a failed cleanup must not prevent the rest from being released either.
            try { failed(ex); }
            catch { }
        }
    }

    void Remove(Ownership ownership)
    {
        lock (_gate) _owned.Remove(ownership);
    }

    sealed class Ownership(ModuleLifetime owner, IDisposable resource) : IDisposable
    {
        ModuleLifetime? _owner = owner;
        IDisposable? _resource = resource;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Remove(this);
            Interlocked.Exchange(ref _resource, null)?.Dispose();
        }
    }
}

/// <summary>Independent instances can commit concurrently. Only a change of the host's loaded rules takes the writer.</summary>
internal sealed class ModuleExecution
{
    readonly ReaderWriterLockSlim gate = new(LockRecursionPolicy.SupportsRecursion);

    public bool TryRun(ModuleLifetime owner, Action mutation)
    {
        // A caller can already hold an instance lock. It must never wait for rule cleanup under that lock.
        if (gate.IsWriteLockHeld || !gate.TryEnterReadLock(0)) return false;
        try
        {
            if (!owner.IsActive) return false;
            mutation();
            return true;
        }
        finally { gate.ExitReadLock(); }
    }

    public bool DeferFromMutation(Action operation)
    {
        if (!gate.IsReadLockHeld || gate.IsWriteLockHeld) return false;
        ThreadPool.UnsafeQueueUserWorkItem(static work => work(), operation, preferLocal: false);
        return true;
    }

    public ChangeScope Change()
    {
        gate.EnterWriteLock();
        return new(gate);
    }

    internal readonly struct ChangeScope(ReaderWriterLockSlim gate) : IDisposable
    {
        public void Dispose() => gate.ExitWriteLock();
    }
}
