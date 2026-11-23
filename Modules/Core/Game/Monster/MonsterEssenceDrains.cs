using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    // The "more" support stats that scale Essence Drain's debuff: a projectile spell's chaos damage over time.
    static readonly uint[] EssenceDrainSupportMore =
    [
        Stat.SupportControlledDestructionSpellDamagePlusPctFinal, Stat.SupportVoidManipulationChaosDamagePlusPctFinal,
        Stat.SupportRapidDecayDamageOverTimePlusPctFinal, Stat.SupportEfficacySpellDamagePlusPctFinal,
        Stat.SupportEfficacyDamageOverTimePlusPctFinal, Stat.SupportPhysChaosProjectileChaosDamageOverTimePlusPctFinal,
        Stat.CrueltyDamageOverTimePlusPctFinal,
    ];

    /// <summary>Essence Drain (GrantedEffects <c>EssenceDrain</c>) and its transfigured versions: the debuff one projectile
    /// hit puts on the monster; null for any other skill. Its damage is base_chaos_damage_to_deal_per_minute (the gem level's
    /// resolved value) with the increases a spell's chaos damage over time takes (its implicit
    /// spell_damage_modifiers_apply_to_skill_dot admits spell damage; it is not area damage, and projectile damage does not
    /// apply to its damage over time), essence_drain_damage_+%, the supports' more multipliers and the damage over time
    /// multipliers. Duration: base_skill_effect_duration with skill effect duration and siphon_duration_+%. Leech:
    /// siphon_life_leech_from_damage_permyriad. Of Desperation's life loss is not modelled.</summary>
    internal static MonsterEssenceDrain.Cast? EssenceDrainCast(Player player, ushort skillId, int effect)
    {
        string? id = SkillsFor(player).EffectId(effect);
        if (id is not ("EssenceDrain" or "EssenceDrainAltX" or "EssenceDrainAltY")) return null;
        var own = SupportedSkill(player, skillId).Stats;
        var playerStats = CharacterPlayerStats(player);
        var merged = MergeSkillStats(playerStats, own);
        int Get(uint k) => merged.GetValueOrDefault(k);
        int Own(uint k) => own.GetValueOrDefault(k);
        int Player(uint k) => playerStats.GetValueOrDefault(k);

        double perSecond = Get(Stat.BaseChaosDamageToDealPerMinute) / 60.0;
        if (!(perSecond > 0) || DamageStats.Prohibited(DamageType.Chaos, Get)) return null;
        double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.SpellDamagePlusPct) + Get(Stat.ChaosDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.DamageOverTimePlusPctWithSpells)
            + Get(Stat.ChaosDamageOverTimePlusPct) + Get(Stat.EssenceDrainDamagePlusPct);
        double more = EssenceDrainSupportMore.Aggregate(1.0, (m, name) => m * Math.Max(0, 1 + Own(name) / 100.0));
        double multiplier = 1 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.ChaosDotMultiplierPlus)
            + Get(Stat.DamageOverTimeMultiplierPlusWithSpells)) / 100.0;
        double dps = perSecond * Math.Max(0, 1 + increased / 100.0) * more * Math.Max(0, multiplier);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own)
            * Math.Max(0, 1 + Get(Stat.SiphonDurationPlusPct) / 100.0);
        return new(dps, seconds, Math.Max(0, Get(Stat.SiphonLifeLeechFromDamagePermyriad)), skillId, player);
    }

    /// <summary>A projectile hit that the monster survived: puts the shot's Essence Drain debuff on it.</summary>
    internal static void EssenceDrainOnHitLocked(uint id, SpawnedMonster m, MonsterEssenceDrain.Cast cast, double now)
    {
        m.EssenceDrain.Apply(now, cast);
        Console.WriteLine($"[gateway] essence drain: hit 0x{id:x} ({m.Label}), {cast.DamagePerSecond:0.#}/s for " +
            $"{cast.Seconds:0.##} s, leech {cast.LeechPermyriad / 100.0:0.##}%");
    }

    // The debuff's leech goes to its caster only, if alive and still in this instance: on this connection directly, on
    // another player's own frame (KillShares) so its pools are changed under its own lock.
    static void CreditSiphonLeechLocked(Player runner, Player? caster, SpawnedMonster m, double life)
    {
        if (caster is null || caster.Closed || caster.LeavingArea || caster.PlayerLife == 0) return;
        if (ReferenceEquals(caster, runner)) { SiphonLeechLocked(runner, m, life); return; }
        if (caster.Instance is null || !ReferenceEquals(caster.Instance, runner.Instance)) return;
        caster.KillShares.Enqueue(_ =>
        {
            using (ProjectileMutation(caster)) SiphonLeechLocked(caster, m, life);
            return Task.CompletedTask;
        });
    }

    // The debuff's life leech: a leech instance like a hit's (same caps and rate), with no gain on hit.
    static void SiphonLeechLocked(Player player, SpawnedMonster m, double life)
    {
        if (!(life > 0) || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return;
        if (m.Effects[Stat.MapMonstersCannotBeLeechedFrom] != 0 || m.Mods.Effects[Stat.CannotHaveLifeLeechedFrom] != 0) return;
        var session = LeechOf(player);
        double now = ProjectileWorldTime(player);
        var instant = session.Hit(now, (life, 0, 0), player.PlayerLife >= player.PlayerMaxLife,
            player.PlayerMana >= UnreservedMana(player), player.EnergyShield.Current >= player.EnergyShield.Maximum);
        uint before = player.PlayerLife;
        player.PlayerLife = PetrifiedBlood.RecoverLife(player, before, instant.Life, ref session.LifeInstantCarry, minimum: 1);
        player.EnergyShield.Gain(instant.EnergyShield);
        session.InstantLifeChanged |= before != player.PlayerLife;
        TrimLeechLocked(player);
    }

    internal static async Task TickEssenceDrains(Player player)
    {
        if (!MonsterEssenceDrain.AnyApplied) return;
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage, Player? Owner)>();
        double leeched = 0;
        using (ProjectileMutation(player))
        {
            if (player.Closed || player.LeavingArea) return;
            double now = ProjectileWorldTime(player);
            foreach (var (id, m) in player.Area.Monsters)
            {
                if (m.EssenceDrain.Idle) continue;
                if (m.Dead || m.Life == 0)
                {
                    m.EssenceDrain.Clear();
                    continue;
                }
                // The kill belongs to the instance whose damage crossed the monster's remaining life.
                double before = m.Life, sum = 0;
                MonsterEssenceDrain.Cast? winner = null;
                var (damage, leech) = m.EssenceDrain.Advance(now, MonsterDotTaken(m, DamageType.Chaos, now),
                    (cast, dealt, life) =>
                    {
                        if (winner is null && (sum += dealt) >= before) winner = cast;
                        CreditSiphonLeechLocked(player, cast.Caster, m, life);
                    });
                if (damage <= 0) continue;
                leeched += leech;
                // Its damage, Rampage, Vaal soul and kill go to that instance's owner, never to this connection for running
                // the world; with the owner gone, the death without any player's on-kill effects.
                var owner = DotOwner(player, winner?.Caster);
                (m.Life, bool stageStarts) = owner is null ? TakeDamageLocked(m, damage) : TakeOwnerDamageLocked(owner, m, damage);
                m.Aggro = m.Life > 0;
                if (m.Life == 0)
                {
                    m.Dead = true;
                    m.LastHitBy = owner;
                    if (owner is not null) CollectVaalSoulAtDeathLocked(owner, m);
                    m.EssenceDrain.Clear();
                }
                hits.Add((id, m, m.Life, damage, owner));
                if (stageStarts) CommitBossStage(player, id, m, now);
            }
        }
        foreach (var (id, m, life, damage, owner) in hits)
        {
            if (life == 0)
                SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
            if (life == 0 && DotDeathKind(m, life) is var deathKind && deathKind != InstanceProtocol.DeathKindNormal)
                await SendWorldRecord(player, id, InstanceProtocol.LifeUpdate(id, life, u2c: deathKind,
                        orderKey: player.Connection.LockstepClient ? uint.MaxValue : 0u),
                    $"0x01b1 monster 0x{id:x} ({m.Label}) died of essence drain");
            else
                Note(life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) died of essence drain" : null);
            if (life == 0)
            {
                Console.WriteLine($"[gateway] essence drain killed 0x{id:x} ({m.Label})");
                if (owner is not null) await DotKilled(player, id, m);
                else await MonsterKilled(player, id, m, byMinion: true);
            }
        }
        // One log line a second at most: the damage and leech the debuffs dealt since the last line.
        if (hits.Count == 0) return;
        essenceDrainLogDamage += hits.Sum(h => (long)h.Damage);
        essenceDrainLogLeech += leeched;
        double clock = AreaTime(player);
        if (clock - essenceDrainLogAt < 1) return;
        Console.WriteLine($"[gateway] essence drain ticks: {essenceDrainLogDamage} damage, {essenceDrainLogLeech:0.##} life " +
            $"leeched over {Math.Min(clock - essenceDrainLogAt, 99):0.#} s (player life {player.PlayerLife}/{player.PlayerMaxLife})");
        essenceDrainLogAt = clock;
        essenceDrainLogDamage = 0;
        essenceDrainLogLeech = 0;
    }

    static double essenceDrainLogAt, essenceDrainLogLeech;
    static long essenceDrainLogDamage;
}
