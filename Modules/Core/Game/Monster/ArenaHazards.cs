using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Campaign-owned ground contacts: typed degeneration, ordinary hit defences and shared native actors.</summary>
internal static class ArenaHazards
{
    internal static bool Untargetable(ClientState c, SpawnedMonster m) =>
        c.Layout.Id == Yugul.Area && m.Variety?.Id is "Metadata/Monsters/Frog/FrogGod/SilverOrb" or "Metadata/Monsters/Frog/FrogGod/SilverPoolTurret"
        || c.Layout.Id == SolarisLunaris.Area && SolarisLunaris.IsCelestial(m.Variety);
    internal sealed class Exposure
    {
        internal double Last = double.NaN;
        internal readonly double[] Carry = new double[5];
        internal readonly Dictionary<uint, double> MinionCarry = [];
        internal ushort Fear;
        internal int FearValue;
    }
    internal static int Stat(ClientState c, int effect, byte level, string name)
    {
        var cat = SkillsFor(c);
        return Stats.Key(name) is uint key ? cat.EffectStats(effect, cat.MaxLevelAt(effect, level))
            .Where(s => s.Item1 == key).Sum(s => s.Item2) : 0;
    }
    internal static DamagePacket Dot(ClientState c, int effect, byte level, bool minion)
    {
        var result = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            double value = Stat(c, effect, level, $"base_{type.ToString().ToLowerInvariant()}_damage_to_deal_per_minute") / 60d;
            if (value > 0) result = result.Add(type, new(value, value));
        }
        return result.Scale(minion ? Math.Max(0, 1 + Stat(c, effect, level,
            "monster_penalty_against_minions_damage_+%_final_vs_player_minions") / 100d) : 1);
    }
    internal static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    internal static (int X, int Y) Toward((int X, int Y) from, (int X, int Y) to, double distance)
    {
        double d = Distance(from, to);
        if (d <= distance) return to;
        return ((int)Math.Round(from.X + (to.X - from.X) * distance / d), (int)Math.Round(from.Y + (to.Y - from.Y) * distance / d));
    }
    internal static async Task Hit(ClientState p, uint id, SpawnedMonster source, MonsterAttack attack,
        Func<(int X, int Y), bool> inside, Func<bool> current, double minionFactor = 1)
    {
        var w = p.Wire; MonsterHitOutcome? outcome = null;
        await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, id, [], null, atGate: () =>
        {
            lock (p.Monsters)
            {
                if (!CurrentGameWork || !current() || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p)) return null;
                double now = Math.Max(p.Clock.Elapsed.TotalSeconds, ProjectileWorldTime(p));
                var minion = attack with { Damage = attack.Damage.Scale(minionFactor) };
                foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                    if (!g.Monster.Dead && inside(g.Monster.Walk.Position(now))) GolemTakeHitLocked(p, g, id, source, minion, now, area: true);
                if (p.PlayerLife == 0 || !inside(p.Walk.Position(now))) return null;
                outcome = CommitMonsterHitLocked(w.Remote, p, id, source, attack, now, p.Walk.Position(now),
                    source.Walk.Position(now), false, p.NextActionSequence());
                return outcome?.HitPacket;
            }
        });
        if (outcome is not null) await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, id, outcome);
    }
    internal static async Task Ground(ClientState p, Exposure e, double now,
        Func<(int X, int Y), bool, DamagePacket> rawAt, Func<bool> current)
    {
        double seconds = double.IsNaN(e.Last) ? 0 : Math.Clamp(now - e.Last, 0, .5); e.Last = now;
        if (seconds == 0) return;
        var w = p.Wire; bool shieldChanged = false, died = false;
        await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, PlayerId, [], null, atGate: () =>
        {
            lock (p.Monsters)
            {
                if (!CurrentGameWork || !current() || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p)) return null;
                var stats = CharacterPlayerStats(p);
                int Get(string n) => Stats.Key(n) is uint key ? stats.GetValueOrDefault(key) : 0;
                var raw = p.PlayerLife == 0 || p.Immune || Get("base_cannot_be_damaged") != 0 ? DamagePacket.Empty : rawAt(p.Walk.Position(now), false);
                var immune = PlayerImmunities(p); uint before = p.PlayerLife; double shield = p.EnergyShield.Current;
                int loss = 0;
                foreach (var type in DamageTypes.ConversionOrder)
                {
                    int slot = (int)type;
                    double resistance = type == DamageType.Physical ? PlayerDamageReduction(p, stats).Physical
                        : Math.Min(Resistance(p, type), MaxResistance(p, type));
                    double factor = p.ElementalAilments.DamageTakenFactor(now, Get("base_damage_taken_+%")
                        + Get(type.ToString().ToLowerInvariant() + "_damage_taken_+%"))
                        * Math.Max(0, 1 + Get(IncomingResourceDefences.TakenStat) / 100d)
                        * Math.Max(0, 1 - IncomingResourceDefences.LessTakenPct(p.Pools, Get) / 100d);
                    double amount = (immune.Contains(type) ? 0 : raw[type].Min * Math.Max(0, 1 - resistance / 100d) * seconds * factor) + e.Carry[slot];
                    int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount)); e.Carry[slot] = amount - damage;
                    bool chaosBypasses = type == DamageType.Chaos && Get("chaos_damage_does_not_bypass_energy_shield") == 0
                        && Get("virtual_chaos_damage_does_not_bypass_energy_shield") == 0
                        && !(Get("chaos_damage_does_not_bypass_energy_shield_while_not_low_life") != 0
                            && (long)p.PlayerLife * 100 > (long)p.PlayerMaxLife * LowLifeThresholdPct(stats))
                        && !(Get("chaos_damage_does_not_bypass_energy_shield_while_not_low_mana") != 0
                            && (long)p.PlayerMana * 2 > p.Pools.Mana);
                    loss += type == DamageType.Chaos ? p.EnergyShield.Damage(damage, chaosBypasses, now)
                        : IncomingResourceDefences.DamageOverTime(p.EnergyShield, damage, Get(IncomingResourceDefences.BypassStat), now, ref p.NonChaosDotBypassCarry);
                }
                p.PlayerLife = (uint)Math.Max(0, (long)p.PlayerLife - loss);
                shieldChanged = shield != p.EnergyShield.Current;
                if (died = before > 0 && p.PlayerLife == 0) { p.InvalidateIncomingHits(); RecordHardcoreDeath(p); }
                foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                {
                    if (g.Monster.Dead || g.Monster.Invulnerable || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning") continue;
                    var packet = rawAt(g.Monster.Walk.Position(now), true);
                    int MinionStat(string n) => Stats.Key(n) is uint key ? g.Player.GetValueOrDefault(key) : 0;
                    bool elementalImmune = g.Definition.IsGolem && (MinionStat("golem_immunity_to_elemental_damage") != 0
                        || g.Definition.Kind is "Flame" or "Ice" or "Lightning" && MinionStat("elemental_golem_immunity_to_elemental_damage") != 0);
                    double amount = e.MinionCarry.GetValueOrDefault(g.Id);
                    foreach (var type in DamageTypes.ConversionOrder)
                    {
                        if (elementalImmune && DamageTypes.IsElemental(type)) continue;
                        var res = MonsterHitResistance(g.Monster, type, default);
                        amount += DamageModel.Mitigate(packet[type].Min, res.Value, res.Maximum) * seconds;
                    }
                    int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount)); e.MinionCarry[g.Id] = amount - damage;
                    int absorbed = Math.Min((int)g.Shield, damage); g.Shield -= absorbed; damage -= absorbed;
                    g.Monster.Life = (uint)Math.Max(0, (long)g.Monster.Life - damage);
                    if (absorbed > 0 || damage > 0) p.Minions.OutputQueue.Add(new([], "campaign ground damages minion", g.Id, g.Monster, PlayerMinions.OutputKind.Life));
                    if (g.Monster.Life == 0) GolemDieLocked(p, g, now);
                }
                return before == p.PlayerLife ? null : InstanceProtocol.LifeUpdate(PlayerId, p.PlayerLife, p.PlayerReservedLife);
            }
        });
        if (shieldChanged) await SendPlayerEnergyShield(w.Remote, w.Stream, w.Ciphers, p);
        if (died) await PlayerDied(w.Remote, w.Stream, w.Ciphers, p, "campaign arena ground");
    }
}
