using System.Runtime.CompilerServices;
using GameServer.Core.World;

// Death's Oath: "Trigger Level 20 Death Aura when Equipped" is a damage aura (DamageAuras.cs) around the wearer: chaos
// damage over time to the hostile monsters in its radius (30, grown by area), each marked with unique_deaths_oath_aura
// (its buff visual also draws the ring on the source). Walls block it like other area damage. "You take 450 Chaos
// Damage per second for 3 seconds on Kill" is the unique_deaths_oath_debuff on the killer (refreshes, does not stack).
public static partial class Game
{
    const string DeathAuraEffect = "DeathAura";
    const ushort DeathsOathAuraBuffRow = 218, DeathsOathDebuffRow = 219;
    const int DeathAuraBaseRadius = 30;

    sealed class DeathAuraState
    {
        public WornGrantedSkills.Grant Grant;
        public object? Area, Owner;
        public bool Live = true, Ring;
        public readonly Dictionary<uint, ushort> Marks = [];
        public int Radius = DeathAuraBaseRadius;
        public int Hit, Blocked;
        public double NextLog;
    }

    static readonly ConditionalWeakTable<Player, DeathAuraState> DeathAuras = new();

    // Starts, keeps or stops the aura from the worn grant (called when the stats change and on entering an area).
    static void ReconcileDeathAura(Player player)
    {
        WornGrantedSkills.Grant? grant = !player.Closed && !player.LeavingArea && !InTown(player) && CurrentGameRules
            ? AllWornGrantedSkillsOf(player).Where(w => w.EffectId == DeathAuraEffect && !w.Support
                && !IsInactive(player, w.Host) && LeagueFor(player).Rules.AllowsGemEffect(w.EffectId))
                .Select(w => (WornGrantedSkills.Grant?)w).FirstOrDefault() : null;
        DeathAuras.TryGetValue(player, out var state);
        if (state is { Live: true } && grant is { } same && state.Grant.Identity == same.Identity && state.Grant.Level == same.Level
            && ReferenceEquals(state.Area, player.Area) && ReferenceEquals(state.Owner, CombatLifetime))
            return;
        if (state is not null) StopDeathAura(player, state, grant is null ? "no worn grant" : "restarted");
        if (grant is not { } g) return;
        var s = new DeathAuraState { Grant = g, Area = player.Area, Owner = CombatLifetime };
        var own = DeathAuraOwnStats(player, g);
        s.Radius = DeathAuraRadius(player, g, own, CharacterPlayerStats(player));
        DeathAuras.AddOrUpdate(player, s);
        StartDamageAura(player, new DamageAura
        {
            Name = "Death Aura",
            Radius = () => s.Radius,
            BuffRow = DeathsOathAuraBuffRow,
            Skill = 0,
            Marks = s.Marks,
            Running = () => s.Live && !player.Closed && ReferenceEquals(player.Area, s.Area),
            Active = () =>
            {
                // The ring: the aura's buff on its source (AuraSource) draws the radius effect, like other auras. The
                // buff ends on death (CancelOnDeath), so it comes back with the next frame alive.
                bool alive = player.PlayerLife > 0;
                if (alive != s.Ring)
                {
                    s.Ring = alive;
                    if (alive) player.Area.Buffs.Set(player.Id, new(DeathsOathAuraBuffRow, player.Id, AuraSource: true, DisplayState: 0));
                    else player.Area.Buffs.Clear(player.Id, DeathsOathAuraBuffRow, player.Id, auraSource: true);
                    if (GameServer.Core.Diagnostics.ServerTrace.Skills) Console.WriteLine($"[gateway] Death Aura ring {(alive ? "shown" : "hidden")}");
                }
                return alive && !InGracePeriod(player);
            },
            Sees = (origin, target) =>
            {
                bool clear = MeleeArcSight(player, origin, target);
                if (clear) s.Hit++; else s.Blocked++;
                return clear;
            },
            Damage = seconds =>
            {
                using var scope = new PlayerStatScope(player, fresh: true);
                var stats = new Dictionary<uint, int>(CharacterPlayerStats(player));
                var ownStats = DeathAuraOwnStats(player, s.Grant);
                double perSecond = DeathAuraDps(stats, ownStats);
                s.Radius = DeathAuraRadius(player, s.Grant, ownStats, stats);
                double now = AreaTime(player);
                if (GameServer.Core.Diagnostics.ServerTrace.Skills && now >= s.NextLog && s.Hit + s.Blocked > 0)
                {
                    Console.WriteLine($"[gateway] Death Aura: {perSecond:0} chaos/s, radius {s.Radius}, monster-frames hit {s.Hit}, behind walls {s.Blocked}");
                    s.NextLog = now + 1; s.Hit = s.Blocked = 0;
                }
                return (new SkillDamageContext(SkillHitKind.Spell, stats).OverTime(new DamageOverTime(DamageType.Chaos, perSecond, seconds)), stats);
            },
        });
        if (GameServer.Core.Diagnostics.ServerTrace.Skills) Console.WriteLine($"[gateway] Death Aura on (level {g.Level}): base {own.GetValueOrDefault(Stat.BaseChaosDamageToDealPerMinute) / 60d:0} chaos/s, radius {s.Radius}");
    }

    static void StopDeathAura(Player player, DeathAuraState state, string why)
    {
        state.Live = false;
        if (DeathAuras.TryGetValue(player, out var current) && ReferenceEquals(current, state)) DeathAuras.Remove(player);
        if (ReferenceEquals(player.Area, state.Area))
        {
            foreach (uint id in state.Marks.Keys) player.Area.Buffs.Clear(id, DeathsOathAuraBuffRow, player.Id);
            if (state.Ring) player.Area.Buffs.Clear(player.Id, DeathsOathAuraBuffRow, player.Id, auraSource: true);
        }
        state.Ring = false;
        state.Marks.Clear();
        if (GameServer.Core.Diagnostics.ServerTrace.Skills) Console.WriteLine($"[gateway] Death Aura off ({why})");
    }

    // Radius 30 grown by area, aura area (base_aura_area_of_effect_+%, Pools.AuraAreaPct) included as for Pyroclast's aura.
    static int DeathAuraRadius(Player player, WornGrantedSkills.Grant grant, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> stats)
    {
        var scaling = SkillAreaStats(player, own, grant.Effect, stats);
        return SkillArea.Radius(DeathAuraBaseRadius, key => scaling.GetValueOrDefault(key)
            + (key == SkillArea.Increase ? player.Pools.AuraAreaPct : 0), spell: true);
    }

    static Dictionary<uint, int> DeathAuraOwnStats(Player player, WornGrantedSkills.Grant grant) =>
        SkillsFor(player).EffectStats(grant.Effect, grant.Level)
            .GroupBy(p => p.Item1).ToDictionary(p => p.Key, p => p.Sum(v => v.Item2));

    // Base chaos per second from the skill (base_chaos_damage_to_deal_per_minute), the player's increases that apply to
    // a chaos area spell's damage over time, then the damage over time multipliers.
    static double DeathAuraDps(IReadOnlyDictionary<uint, int> stats, IReadOnlyDictionary<uint, int> own)
    {
        double Get(uint key) => (double)stats.GetValueOrDefault(key) + own.GetValueOrDefault(key);
        double basis = own.GetValueOrDefault(Stat.BaseChaosDamageToDealPerMinute) / 60d;
        double increased = Get(Stat.DamagePlusPct) + Get(Stat.SpellDamagePlusPct) + Get(Stat.ChaosDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.ChaosDamageOverTimePlusPct) + Get(Stat.AreaDamagePlusPct);
        double dps = basis * Math.Max(0, 100 + increased) / 100;
        return dps * Math.Max(0, 100 + Get(Stat.DotMultiplierPlus) + Get(Stat.ChaosDotMultiplierPlus)) / 100;
    }

    // GameEvents.MonsterKilled: the killer wearing Death's Oath takes its chaos degeneration for its duration.
    public static async Task DeathsOathKilled(MonsterKill kill)
    {
        var player = kill.Player;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        var stats = CharacterPlayerStats(player);
        int perMinute = stats.GetValueOrDefault(Stat.DeathsOathDebuffOnKillBaseChaosDamageToDealPerMinute);
        int ms = stats.GetValueOrDefault(Stat.DeathsOathDebuffOnKillDurationMs);
        if (perMinute <= 0 || ms <= 0) return;
        await AddPlayerDebuff(player, DeathsOathDebuffRow, player.Id,
            new Dictionary<uint, int> { [Stat.BaseChaosDamageTakenPerMinute] = perMinute }, ms / 1000d);
    }
}
