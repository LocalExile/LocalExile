using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    /// <summary>BuffDefinitions row 10 <c>poison</c>.</summary>
    internal const ushort PoisonBuffRow = 10;

    /// <summary>A skill's poison damage and duration, as <see cref="SupportedIgnite"/>: its panel poison rows.</summary>
    internal static MonsterPoisons.Settings SupportedPoison(ClientState conn, ushort skillId, bool spell,
        IReadOnlyDictionary<uint, int> player, SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0,
        SupportedHitInputs? capturedInputs = null)
    {
        Func<string, int> get;
        if (capturedInputs is null)
        {
            var merged = MergeSkillStats(player, SupportedSkill(conn, skillId).Stats);
            get = name => Stats.Key(name) is uint key ? merged.GetValueOrDefault(key) : 0;
        }
        else
            get = capturedInputs.Get;
        string hand = spell ? "spell" : "main_hand";
        var rows = capturedInputs is null
            ? SupportedAilmentRows(conn, skillId, hand, weapon, additionalIncreased)
            : SupportedAilmentRowsFrom(capturedInputs, hand, additionalIncreased);
        return MonsterPoisons.Settings.From(rows, hand, get,
            spell || weapon is not null ? null : MainHandLocalSources(conn));
    }

    /// <summary>The default melee attack's poison, from the rows <see cref="DefaultAttackIgnite"/> reads.</summary>
    internal static MonsterPoisons.Settings DefaultAttackPoison(ClientState conn, IReadOnlyDictionary<uint, int> player,
        SkillCatalog.Unarmed? weapon = null, long additionalIncreased = 0, DamageConversion? conversion = null)
    {
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        var sourceWeapon = weapon ?? WeaponFor(conn);
        return MonsterPoisons.Settings.From(AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true),
            SupportedAttackDamage.Raw(sourceWeapon, Get), conversion ?? DamageStats.Conversion(player, Stats, attack: true),
            Get, additionalIncreased),
            "main_hand", Get, sourceWeapon.LocalSources);
    }

    // A landed hit's poison: the chance the panel shows (virtual_total_{hand}_chance_to_poison_on_hit_%: the caster's and
    // the skill's stats, and for an attack its weapon's own, Settings.WeaponChance), then the monster's avoidance. Its damage
    // per second (an independent roll in the non-critical or critical range) and duration come from the hit's panel poison
    // rows (<paramref name="settings"/>, captured with the hit as its ignite's are), so a poison deals what the panel shows:
    // the hit's own increases and the monster's resistance, already in <paramref name="mitigated"/>, must not reach it.
    internal static void PoisonOnHitLocked(ClientState conn, SpawnedMonster target, DamagePacket mitigated,
        bool spell, bool critical, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill,
        MonsterPoisons.Settings settings, double now, Random random) =>
        TryPoisonOnHitLocked(conn, target, mitigated, spell, critical, player, skill, settings, now, random,
            out _, out _);

    internal static bool TryPoisonOnHitLocked(ClientState conn, SpawnedMonster target, DamagePacket mitigated,
        bool spell, bool critical, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill,
        MonsterPoisons.Settings settings, double now, Random random, out double perSecond, out double seconds)
    {
        perSecond = seconds = 0;
        if (target.Dead || target.Life == 0 || settings.Seconds <= 0) return false;
        double phys = mitigated[DamageType.Physical].Min;
        double chaos = mitigated[DamageType.Chaos].Min;
        if (phys + chaos <= 0) return false;

        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int Skill(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        int Get(string name) => Player(name) + Skill(name);

        if (MonsterPoisons.Prohibited(Get)) return false;

        long chance = (long)Get("base_chance_to_poison_on_hit_%")
            + (spell
                ? Get("spells_chance_to_poison_on_hit_%")
                : Get("chance_to_poison_on_hit_with_attacks_%") + settings.WeaponChance + settings.ProjectileChance);
        if (chaos > 0) chance += Get("chaos_damage_chance_to_poison_%");

        if (chance <= 0 || !RandomRolls.Chance((int)Math.Clamp(chance, 0, 100), 100, random)) return false;
        if (target.Mods.Effects["cannot_be_poisoned"] != 0) return false;
        int avoid = target.Mods.Effects["avoid_poison_%"];
        if (avoid > 0 && RandomRolls.Chance(avoid, 100, random)) return false;

        var (min, max) = critical ? (settings.CriticalMin, settings.CriticalMax) : (settings.Min, settings.Max);
        // Lethal Dose: a hit that finds the enemy unpoisoned applies additional poisons on top of its own.
        int stacks = 1 + (target.Poisons.Stacks(now) == 0
            ? Math.Max(0, Get("additional_poisons_+_to_apply_vs_non_poisoned_enemies")) : 0);
        perSecond = min + (Math.Max(min, max) - min) * random.NextDouble();
        seconds = settings.Seconds * MonsterPoisons.SelfDuration(name => target.Mods.Effects[name]);
        int accepted = 0;
        for (int i = 0; i < stacks; i++)
            if (target.Poisons.Add(now, perSecond, seconds)) accepted++;
        if (accepted == 0) return false;
        ReservationPoisonLocked(conn, now);
        return true;
    }

    internal static async Task TickPoisons(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage)>();
        var ended = new List<(uint Id, ushort Buff)>();
        var updated = new List<(uint Id, ushort Buff, int Stacks, double Remaining)>();
        using (ProjectileMutation(conn))
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = ProjectileWorldTime(conn);
            foreach (var (id, m) in conn.Monsters)
            {
                if (OwnedGolemActor(conn, id, m)) continue;
                if (m.Poisons.Idle) continue;
                int damage = 0;
                if (m.Dead || m.Life == 0) m.Poisons.Clear();
                else damage = m.Poisons.Advance(now, MonsterDotTaken(m, DamageType.Chaos, now));
                if (damage > 0)
                {
                    (m.Life, bool stageStarts) = TakeOwnerDamageLocked(conn, m, damage);
                    m.Aggro = m.Life > 0;
                    if (m.Life == 0)
                    {
                        m.Dead = true;
                        CollectVaalSoulAtDeathLocked(conn, m);
                        m.Poisons.Clear();
                    }
                    hits.Add((id, m, m.Life, damage));
                    if (stageStarts) CommitBossStage(remote, stream, ciphers, conn, id, m, now);
                }
                if (m.Dead || m.Life == 0 || !m.Poisons.Poisoned)
                {
                    if (m.Poisons.Buff is ushort gone)
                    {
                        ended.Add((id, gone));
                        m.Poisons.Buff = null;
                    }
                }
                else
                {
                    if (m.Poisons.Buff is ushort old) ended.Add((id, old));
                    int stacks = m.Poisons.Stacks(now);
                    ushort next = conn.NextBuff++;
                    m.Poisons.Buff = next;
                    updated.Add((id, next, stacks, m.Poisons.Until - now));
                }
            }
        }
        foreach (var (id, buff) in ended)
        {
            await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(id, buff), null);
            // The buff may be another player's (who applied the ailment): theirs goes too (instances are the instance's).
            ToOthers(conn, id, InstanceEntities.RemoveBuff(id, buff));
        }
        foreach (var (id, buff, stacks, remaining) in updated)
            await Send(remote, stream, ciphers,
                InstanceEntities.AddBuff(id, buff, PoisonBuffRow, PlayerId, stacks: (ushort)Math.Clamp(stacks, 1, ushort.MaxValue),
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, remaining))), null);
        foreach (var (id, m, life, damage) in hits)
        {
            if (life == 0)
                await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]), null);
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life),
                life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) died of poison" : null);
            if (life == 0) await DotKilled(remote, stream, ciphers, conn, id, m);
        }
    }
}
