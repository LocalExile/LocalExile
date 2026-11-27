using System.Runtime.CompilerServices;
using GameServer.Core.World;

public static partial class Game
{
    // The four Aegis skills that are not Physical Aegis (Invictus Solaris, Vix Lunaris, Perepiteia, Magna Eclipsis): the
    // same shield (PhysicalAegisState: a pool that takes damage from hits and is restored after a pause without that
    // damage), each for its own damage types. Their states live here, per player object, so an area change starts
    // them full; Physical Aegis keeps its own state and its carry-over.
    static readonly (string Effect, ushort Buff, ushort Depleted, ushort Persist, DamageType[] Types)[] TypedAegisKinds =
    [
        ("FireAegis", 956, 961, 966, [DamageType.Fire]),
        ("ColdAegis", 957, 962, 967, [DamageType.Cold]),
        ("ElementalAegis", 958, 963, 968, [DamageType.Fire, DamageType.Cold, DamageType.Lightning]),
        ("LightningAegis", 959, 964, 969, [DamageType.Lightning]),
    ];

    static readonly ConditionalWeakTable<Player, PhysicalAegisState[]> TypedAegisStates = new();

    /// <summary>The player's typed Aegis states, each reconciled with what is worn. Null when the player wears none and
    /// never has: the check is one comparison per call until the worn items or the stat sources change.</summary>
    static PhysicalAegisState[]? TypedAegisesOf(Player player)
    {
        var owner = ResourceEffectsOf(player);
        if (!ResourceEffectsCurrent(player, owner)) return null;
        var items = ItemsOf(player);
        var states = TypedAegisStates.GetValue(player, p =>
        {
            var made = new PhysicalAegisState[TypedAegisKinds.Length];
            for (int i = 0; i < made.Length; i++) made[i] = new PhysicalAegisState { Changed = () => PresentStats(p) };
            return made;
        });
        var first = states[0];
        if (ReferenceEquals(first.CheckedOwner, owner) && ReferenceEquals(first.CheckedStatSources, player.StatSourcesCache)
            && first.CheckedInventoryMutations == items.Mutations)
            return first.CheckedHasGrant ? states : null;
        first.CheckedOwner = owner;
        first.CheckedStatSources = player.StatSourcesCache;
        first.CheckedInventoryMutations = items.Mutations;
        var grants = AllWornGrantedSkillsOf(player, items.EquipmentSnapshot());
        bool any = false;
        for (int i = 0; i < states.Length; i++)
        {
            string effect = TypedAegisKinds[i].Effect;
            var grant = grants.FirstOrDefault(g => g.EffectId == effect && !g.Support && !g.ManuallyCast
                && !IsInactive(player, g.Host) && LeagueFor(player).Rules.AllowsGemEffect(g.EffectId));
            var state = states[i];
            if (grant.Host == 0 || grant.EffectId != effect || PhysicalAegisGeometry(player, grant) is not { } geometry)
            {
                if (state.Exists) state.Retire();
                continue;
            }
            any = true;
            if (!ReferenceEquals(state.Owner, owner) || state.Source != grant || state.Maximum != geometry.Maximum
                || state.RechargeDelay != geometry.Seconds || state.Variation != geometry.Variation)
            {
                state.Retire();
                state.Install(owner, grant, geometry.Maximum, geometry.Seconds, geometry.Variation);
            }
        }
        first.CheckedHasGrant = any;
        return any ? states : null;
    }

    /// <summary>What is left of a hit's elemental damage after the typed Aegises took their types' share. The share of
    /// each type is the hit's own (<paramref name="types"/>: only its proportions count).</summary>
    static int TypedAegisDamage(Player player, int elemental, DamagePacket types, double now)
    {
        if (elemental <= 0 || TypedAegisesOf(player) is not { } states) return Math.Max(0, elemental);
        double whole = 0;
        Span<double> share = stackalloc double[3];
        DamageType[] order = [DamageType.Fire, DamageType.Cold, DamageType.Lightning];
        for (int t = 0; t < 3; t++) { share[t] = Math.Max(0, types[order[t]].Max); whole += share[t]; }
        if (whole <= 0) return elemental;
        int left = elemental;
        for (int i = 0; i < states.Length; i++)
        {
            var state = states[i];
            if (!state.Exists) continue;
            double part = 0;
            foreach (var type in TypedAegisKinds[i].Types) part += share[Array.IndexOf(order, type)];
            int amount = (int)Math.Min(left, Math.Round(elemental * part / whole));
            if (amount <= 0) continue;
            int after = state.Absorb(amount, now);
            int absorbed = amount - after;
            left -= absorbed;
            // What this shield took is no longer there for the next one (Elemental Aegis after a single-type one).
            if (absorbed > 0)
                foreach (var type in TypedAegisKinds[i].Types)
                {
                    int t = Array.IndexOf(order, type);
                    share[t] = Math.Max(0, share[t] - share[t] * absorbed / amount);
                }
        }
        return left;
    }

    // The buffs: the persistent one while the shield exists, and its stage (the pool as stacks, or the depleted one with
    // the countdown to its restore), as Physical Aegis shows its own.
    static void PresentTypedAegises(Player player)
    {
        TypedAegisesOf(player);
        if (!TypedAegisStates.TryGetValue(player, out var known)) return;
        double now = ResourceEffectsTime(player);
        var buffs = player.Area.Buffs;
        for (int i = 0; i < known.Length; i++)
        {
            var (_, active, depleted, persist, _) = TypedAegisKinds[i];
            var state = known[i];
            if (state.Exists) state.Advance(now);
            if (state.Exists && double.IsFinite(state.RestoresAt)) PresentIn(player, state.RestoresAt - now);
            ushort wanted = !state.Exists ? (ushort)0 : state.Depleted ? depleted : active;
            if (active != wanted) buffs.Clear(player.Id, active, player.Id);
            if (depleted != wanted) buffs.Clear(player.Id, depleted, player.Id);
            if (!state.Exists) { buffs.Clear(player.Id, persist, player.Id); continue; }
            buffs.Keep(player.Id, new(persist, player.Id));
            buffs.Keep(player.Id, state.Depleted
                ? new(wanted, player.Id, U68: Buff.Seconds(state.RestoresAt - now))
                : new(wanted, player.Id, Values: [(uint)Math.Max(0, state.Variation)], Stacks: (ushort)Math.Clamp(state.Current, 0, ushort.MaxValue)));
        }
    }
}
