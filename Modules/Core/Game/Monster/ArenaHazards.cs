using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Campaign-owned ground contacts: typed degeneration, ordinary hit defences and shared native actors.</summary>
internal static class ArenaHazards
{
    internal static bool Untargetable(Player c, SpawnedMonster m) =>
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
    internal static int Stat(Player c, int effect, byte level, uint name)
    {
        var cat = SkillsFor(c);
        return name is var key ? cat.EffectStats(effect, cat.MaxLevelAt(effect, level))
            .Where(s => s.Item1 == key).Sum(s => s.Item2) : 0;
    }
    internal static DamagePacket Dot(Player c, int effect, byte level, bool minion)
    {
        var result = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            double value = Stat(c, effect, level, global::Stat.Of($"base_{type.ToString().ToLowerInvariant()}_damage_to_deal_per_minute")) / 60d;
            if (value > 0) result = result.Add(type, new(value, value));
        }
        return result.Scale(minion ? Math.Max(0, 1 + Stat(c, effect, level,
            global::Stat.MonsterPenaltyAgainstMinionsDamagePlusPctFinalVsPlayerMinions) / 100d) : 1);
    }
    internal static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    internal static (int X, int Y) Toward((int X, int Y) from, (int X, int Y) to, double distance)
    {
        double d = Distance(from, to);
        if (d <= distance) return to;
        return ((int)Math.Round(from.X + (to.X - from.X) * distance / d), (int)Math.Round(from.Y + (to.Y - from.Y) * distance / d));
    }
    internal static async Task Hit(Player p, uint id, SpawnedMonster source, MonsterAttack attack,
        Func<(int X, int Y), bool> inside, Func<bool> current, double minionFactor = 1)
    {
        MonsterHitOutcome? outcome = null;
        await SendWorldRecord(p, id, [], null, atGate: () =>
        {
            if (!CurrentGameWork || !current() || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p)) return null;
            double now = Math.Max(AreaTime(p), ProjectileWorldTime(p));
            var minion = attack with { Damage = attack.Damage.Scale(minionFactor) };
            foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                if (!g.Monster.Dead && inside(g.Monster.Walk.Position(now))) GolemTakeHitLocked(p, g, id, source, minion, now, area: true);
            if (p.PlayerLife == 0 || !inside(p.Walk.Position(now))) return null;
            outcome = CommitMonsterHitLocked(p, id, source, attack, now, p.Walk.Position(now),
                source.Walk.Position(now), false, p.NextActionSequence());
            return outcome?.HitPacket;
        });
        if (outcome is not null) await PresentMonsterHit(p, id, outcome);
    }
    internal static async Task Ground(Player p, Exposure e, double now,
        Func<(int X, int Y), bool, DamagePacket> rawAt, Func<bool> current)
    {
        double seconds = double.IsNaN(e.Last) ? 0 : Math.Clamp(now - e.Last, 0, .5); e.Last = now;
        if (seconds == 0) return;
        bool shieldChanged = false, manaChanged = false, died = false;
        await SendWorldRecord(p, p.Id, [], null, atGate: () =>
        {
            if (!CurrentGameWork || !current() || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p)) return null;
            var stats = CharacterPlayerStats(p);
            int Get(uint key) => stats.GetValueOrDefault(key);
            var raw = p.PlayerLife == 0 || p.Immune || Get(global::Stat.BaseCannotBeDamaged) != 0 ? DamagePacket.Empty : rawAt(p.Walk.Position(now), false);
            var immune = PlayerImmunities(p); uint before = p.PlayerLife; double shield = p.EnergyShield.Current;
            uint manaBefore = p.PlayerMana;
            int loss = 0;
            foreach (var type in DamageTypes.ConversionOrder)
            {
                int slot = (int)type;
                double resistance = type == DamageType.Physical ? PlayerDamageReduction(p, stats).Physical
                    : Math.Min(Resistance(p, type) + (type == DamageType.Chaos ? Get(global::Stat.AdditionalChaosResistanceAgainstDamageOverTimePct) : 0), MaxResistance(p, type));
                int conditional = type == DamageType.Physical && p.Walk.Moving(now) ? Get(global::Stat.PhysicalDamageOverTimeTakenPlusPctWhileMoving)
                    : type == DamageType.Chaos ? Get(global::Stat.ChaosDamageTakenOverTimePlusPct)
                        + (p.InCausticCloud ? Get(global::Stat.ChaosDamageTakenOverTimePlusPctWhileInCausticCloud) : 0) : 0;
                double factor = p.ElementalAilments.DamageTakenFactor(now, Get(global::Stat.BaseDamageTakenPlusPct)
                    + Get(global::Stat.DegenEffectPlusPct) + conditional
                    + Get(global::Stat.Of(type.ToString().ToLowerInvariant() + "_damage_taken_+%")))
                    * IncomingResourceDefences.FinalTakenFactor(p.Pools, Get);
                double amount = (immune.Contains(type) ? 0 : raw[type].Min * Math.Max(0, 1 - resistance / 100d) * seconds * factor) + e.Carry[slot];
                int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount)); e.Carry[slot] = amount - damage;
                loss += type == DamageType.Chaos
                    ? DamagePlayerChaosOverTime(p, damage, now, stats)
                    : IncomingResourceDefences.DamageOverTime(p.EnergyShield, damage, Get(IncomingResourceDefences.BypassStat), now, ref p.NonChaosDotBypassCarry);
            }
            uint lifeAfter = VaalImmortalCallLife(p, (uint)Math.Max(0, (long)p.PlayerLife - loss), now);
            RecordRechargeLifeDamage(p, (long)p.PlayerLife - lifeAfter, now, stats);
            p.PlayerLife = lifeAfter;
            shieldChanged = shield != p.EnergyShield.Current;
            manaChanged = manaBefore != p.PlayerMana;
            if (died = before > 0 && p.PlayerLife == 0) { p.InvalidateIncomingHits(); RecordHardcoreDeath(p); }
            foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
            {
                if (g.Monster.Dead || g.Monster.Invulnerable || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning") continue;
                var packet = rawAt(g.Monster.Walk.Position(now), true);
                int MinionStat(uint key) => g.Player.GetValueOrDefault(key);
                bool elementalImmune = g.Definition.IsGolem && (MinionStat(global::Stat.GolemImmunityToElementalDamage) != 0
                    || g.Definition.Kind is "Flame" or "Ice" or "Lightning" && MinionStat(global::Stat.ElementalGolemImmunityToElementalDamage) != 0);
                double amount = e.MinionCarry.GetValueOrDefault(g.Id);
                double soulLink = MinionLinkDamageTaken(p, g, now);
                foreach (var type in DamageTypes.ConversionOrder)
                {
                    if (elementalImmune && DamageTypes.IsElemental(type)) continue;
                    var res = MonsterHitResistance(g.Monster, type, default);
                    amount += DamageModel.Mitigate(packet[type].Min, res.Value, res.Maximum) * seconds * soulLink;
                }
                int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount)); e.MinionCarry[g.Id] = amount - damage;
                int beforeShield = damage;
                damage = g.ShieldPool.Damage(damage, false, now);
                int absorbed = beforeShield - damage;
                g.Monster.Life = (uint)Math.Max(0, (long)g.Monster.Life - damage);
                if (absorbed > 0 || damage > 0) p.Minions.OutputQueue.Add(new([], "campaign ground damages minion", g.Id, g.Monster, PlayerMinions.OutputKind.Life,
                    DeathKind: DotDeathKind(g.Monster, g.Monster.Life)));
                if (g.Monster.Life == 0) GolemDieLocked(p, g, now);
            }
            return before == p.PlayerLife ? null : InstanceProtocol.LifeUpdate(p.Id, p.PlayerLife, p.PlayerReservedLife,
                u2c: p.PlayerLife == 0 ? InstanceProtocol.DeathKindDegen : InstanceProtocol.DeathKindNormal);
        });
        if (shieldChanged) await SendPlayerEnergyShield(p);
        if (manaChanged) await SendPlayerMana(p, "campaign arena ground mana");
        if (died) await PlayerDied(p, "campaign arena ground", deathKind: InstanceProtocol.DeathKindDegen);
    }
}
