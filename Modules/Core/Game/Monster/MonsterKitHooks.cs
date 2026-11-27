using System.Collections.Immutable;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Module-agnostic extension hooks for monster kit construction, <c>EmptyAction*</c> combo bindings, clip-event
/// observation, and state-machine transitions (PR #1618 S6).
/// <para>
/// League and content modules register handlers via <c>context.Lifetime.Own(MonsterKitHooks.Add...(...))</c> so
/// registrations detach cleanly on module unload. Every hot-path query checks <c>IsEmpty</c> first for zero overhead
/// when no hooks are registered.
/// </para>
/// </summary>
public static class MonsterKitHooks
{
    /// <summary>Explicit clip-event → child-GE binding supplied by a module for an <c>EmptyAction*</c> parent
    /// (overrides or supplements S3 Tier-2 matching for ambiguous parents).</summary>
    public sealed record ComboEventBinding(
        string EventName,
        int ChildEffect,
        MonsterComboDispatch? DispatchOverride = null);

    /// <summary>Filter deciding whether <paramref name="effect"/> on <paramref name="variety"/> may enter a monster's
    /// top-level AI kit (<c>BossKits</c> or <c>MonsterKits</c>). Return <c>false</c> to suppress.</summary>
    public delegate bool EffectFilter(MonsterCatalog.Variety variety, int effect, string? effectId);

    /// <summary>Provider of explicit clip-event → child-GE bindings for a variety's <c>EmptyAction*</c> parent.</summary>
    public delegate IReadOnlyList<ComboEventBinding>? ComboBindingProvider(MonsterCatalog.Variety variety, int parentEffect);

    /// <summary>Observer notified when a monster clip event fires during an action (used by S3 combos and encounter
    /// modules needing synchronized VFX/state reactions).</summary>
    public delegate void ClipEventObserver(Player player, uint monsterId, SpawnedMonster monster, int parentEffect, string eventName, double now);

    /// <summary>Phase of a monster skill action for <see cref="ActionLifecycleObserver"/>.</summary>
    public enum ActionLifecyclePhase : byte { Started, Ended }

    /// <summary>Observer notified when a monster starts or completes a skill action (enables modules to drive
    /// skill-scoped <c>StateMachine</c> flags via <see cref="Game.SetMonsterStateAsync"/>).</summary>
    public delegate void ActionLifecycleObserver(Player player, uint monsterId, SpawnedMonster monster, int effect, ActionLifecyclePhase phase, double now);

    /// <summary>Provider of additional state-machine defaults/transitions for a variety (used by S5 and encounter
    /// modules).</summary>
    public delegate StateMachineData? StateTransitionProvider(MonsterCatalog.Variety variety, StateMachineData? current);

    static readonly Lock Gate = new();
    static ImmutableArray<EffectFilter> effectFilters = [];
    static ImmutableArray<ComboBindingProvider> comboProviders = [];
    static ImmutableArray<ClipEventObserver> clipObservers = [];
    static ImmutableArray<ActionLifecycleObserver> actionObservers = [];
    static ImmutableArray<StateTransitionProvider> stateProviders = [];
    static int effectFilterVersion;
    static int comboProviderVersion;

    public static bool HasEffectFilters => !effectFilters.IsEmpty;
    public static bool HasComboProviders => !comboProviders.IsEmpty;
    public static bool HasClipObservers => !clipObservers.IsEmpty;
    public static bool HasActionLifecycleObservers => !actionObservers.IsEmpty;
    public static bool HasStateProviders => !stateProviders.IsEmpty;

    /// <summary>Monotonic version incremented whenever effect filters change (for cache-key invalidation).</summary>
    public static int EffectFilterVersion => Volatile.Read(ref effectFilterVersion);

    /// <summary>Monotonic version incremented whenever combo binding providers change (for cache-key invalidation).</summary>
    public static int ComboProviderVersion => Volatile.Read(ref comboProviderVersion);

    public static IDisposable AddEffectFilter(EffectFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (Gate)
        {
            effectFilters = effectFilters.Add(filter);
            Interlocked.Increment(ref effectFilterVersion);
        }
        return new Token(() =>
        {
            lock (Gate)
            {
                effectFilters = effectFilters.Remove(filter);
                Interlocked.Increment(ref effectFilterVersion);
            }
        });
    }

    public static IDisposable AddComboBindings(ComboBindingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            comboProviders = comboProviders.Add(provider);
            Interlocked.Increment(ref comboProviderVersion);
        }
        return new Token(() =>
        {
            lock (Gate)
            {
                comboProviders = comboProviders.Remove(provider);
                Interlocked.Increment(ref comboProviderVersion);
            }
        });
    }

    public static IDisposable AddClipEventObserver(ClipEventObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (Gate) clipObservers = clipObservers.Add(observer);
        return new Token(() => { lock (Gate) clipObservers = clipObservers.Remove(observer); });
    }

    public static IDisposable AddActionLifecycleObserver(ActionLifecycleObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (Gate) actionObservers = actionObservers.Add(observer);
        return new Token(() => { lock (Gate) actionObservers = actionObservers.Remove(observer); });
    }

    public static IDisposable AddStateTransitions(StateTransitionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate) stateProviders = stateProviders.Add(provider);
        return new Token(() => { lock (Gate) stateProviders = stateProviders.Remove(provider); });
    }

    /// <summary>Returns false if any registered effect filter rejects <paramref name="effect"/> for
    /// <paramref name="variety"/>. Zero allocations; immediate true when no filters are registered.</summary>
    public static bool AllowEffect(MonsterCatalog.Variety variety, int effect, string? effectId)
    {
        var list = effectFilters;
        for (int i = 0; i < list.Length; i++)
            if (!list[i](variety, effect, effectId)) return false;
        return true;
    }

    /// <summary>Collects any module-supplied clip-event → child-GE bindings for <paramref name="parentEffect"/>.</summary>
    public static IReadOnlyList<ComboEventBinding> ExtraComboBindings(MonsterCatalog.Variety variety, int parentEffect)
    {
        var list = comboProviders;
        if (list.IsEmpty) return [];
        List<ComboEventBinding>? combined = null;
        for (int i = 0; i < list.Length; i++)
            if (list[i](variety, parentEffect) is { Count: > 0 } entries)
                (combined ??= []).AddRange(entries);
        return combined is not null ? combined : [];
    }

    /// <summary>Notifies registered clip-event observers (called on the area's frame/action driver).</summary>
    public static void NotifyClipEvent(Player player, uint monsterId, SpawnedMonster monster, int parentEffect, string eventName, double now)
    {
        var list = clipObservers;
        for (int i = 0; i < list.Length; i++)
            list[i](player, monsterId, monster, parentEffect, eventName, now);
    }

    /// <summary>Notifies registered action-lifecycle observers when a monster skill starts or ends.</summary>
    public static void NotifyActionLifecycle(Player player, uint monsterId, SpawnedMonster monster, int effect, ActionLifecyclePhase phase, double now)
    {
        var list = actionObservers;
        for (int i = 0; i < list.Length; i++)
            list[i](player, monsterId, monster, effect, phase, now);
    }

    /// <summary>Applies registered state-transition providers in registration order.</summary>
    public static StateMachineData? ApplyStateTransitions(MonsterCatalog.Variety variety, StateMachineData? current)
    {
        var list = stateProviders;
        for (int i = 0; i < list.Length; i++)
            if (list[i](variety, current) is { } next) current = next;
        return current;
    }

    /// <summary>Clears all registered hooks when the Game module unloads.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            effectFilters = [];
            comboProviders = [];
            clipObservers = [];
            actionObservers = [];
            stateProviders = [];
            Interlocked.Increment(ref effectFilterVersion);
            Interlocked.Increment(ref comboProviderVersion);
        }
    }

    sealed class Token(Action remove) : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) remove();
        }
    }
}
