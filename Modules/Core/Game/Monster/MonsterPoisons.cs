using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    /// <summary>BuffDefinitions row 10 <c>poison</c>.</summary>
    internal const ushort PoisonBuffRow = 10;

    static readonly string[] PoisonIncreases = ["damage_+%", "damage_over_time_+%", "chaos_damage_+%",
        "chaos_damage_over_time_+%", "base_poison_damage_+%", "base_ailment_damage_+%"];

    internal static void PoisonOnHitLocked(ClientState conn, SpawnedMonster target, DamagePacket mitigated,
        bool spell, bool critical, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill,
        double now, Random random)
    {
        if (target.Dead || target.Life == 0) return;
        double phys = mitigated[DamageType.Physical].Min;
        double chaos = mitigated[DamageType.Chaos].Min;
        if (phys + chaos <= 0) return;

        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int Skill(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        int Get(string name) => Player(name) + Skill(name);

        if (Get("cannot_poison_enemies") != 0 || Get("never_damaging_ailments") != 0) return;

        long chance = (long)Get("base_chance_to_poison_on_hit_%")
            + Get("chance_to_poison_on_hit_%")
            + Get("global_chance_to_poison_on_hit_%")
            + (spell ? Get("spells_chance_to_poison_on_hit_%") : Get("chance_to_poison_on_hit_with_attacks_%"));
        if (chaos > 0) chance += Get("chaos_damage_chance_to_poison_%");

        if (chance <= 0 || !RandomRolls.Chance((int)Math.Clamp(chance, 0, 100), 100, random)) return;
        if (target.Mods.Effects["cannot_be_poisoned"] != 0) return;
        int avoid = target.Mods.Effects["avoid_poison_%"];
        if (avoid > 0 && RandomRolls.Chance(avoid, 100, random)) return;

        double baseDps = (phys + chaos) * 0.30;
        double inc = (double)PoisonIncreases.Sum(Get) + (spell ? Get("spell_damage_+%") : Get("attack_damage_+%"));
        double dotMulti = 1 + ((double)Get("dot_multiplier_+") + Get("chaos_dot_multiplier_+")
            + (critical ? 50 + Get("critical_ailment_dot_multiplier_+") : 0)) / 100.0;
        double faster = MonsterPoisons.Faster(Get);
        double dps = baseDps * Math.Max(0, 1 + inc / 100.0) * Math.Max(0, dotMulti) * (faster > 0 ? faster : 1.0);

        double seconds = Ailments.PoisonDurationSeconds
            * Math.Max(0, 1 + ((double)Get("poison_duration_+%") + Get("base_all_ailment_duration_+%")) / 100.0)
            * MonsterPoisons.SelfDuration(name => target.Mods.Effects[name]) / (faster > 0 ? faster : 1.0);

        if (dps <= 0 || seconds <= 0) return;
        target.Poisons.Add(now, dps, seconds);
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
                if (m.Poisons.Idle) continue;
                int damage = 0;
                if (m.Dead || m.Life == 0) m.Poisons.Clear();
                else damage = m.Poisons.Advance(now, MonsterDotTaken(m, DamageType.Chaos, now));
                int stacks = m.Poisons.Stacks(now);
                if (!m.Poisons.Poisoned && m.Poisons.Buff is ushort gone)
                {
                    ended.Add((id, gone));
                    m.Poisons.Buff = null;
                }
                else if (m.Poisons.Poisoned)
                {
                    if (m.Poisons.Buff is ushort old) ended.Add((id, old));
                    ushort next = conn.NextBuff++;
                    m.Poisons.Buff = next;
                    updated.Add((id, next, stacks, m.Poisons.Until - now));
                }
                if (damage == 0) continue;
                (m.Life, bool stageStarts) = TakeOwnerDamageLocked(conn, m, damage);
                m.Aggro = m.Life > 0;
                if (m.Life == 0)
                {
                    m.Dead = true;
                    CollectVaalSoulAtDeathLocked(conn, m);
                    m.Poisons.Clear();
                    if (m.Poisons.Buff is ushort buff) { ended.Add((id, buff)); m.Poisons.Buff = null; }
                }
                hits.Add((id, m, m.Life, damage));
                if (stageStarts) CommitBossStage(remote, stream, ciphers, conn, id, m, now);
            }
        }
        foreach (var (id, buff) in ended)
            await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(id, buff), null);
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
            if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
        }
    }
}
