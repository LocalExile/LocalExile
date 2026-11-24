using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

static partial class Sirus
{
    internal static DamagePacket Dot(Player c, int effect, bool minion)
    {
        var packet = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            double dps = Stat(c, effect, 84, global::Stat.Of("base_" + type.ToString().ToLowerInvariant() + "_damage_to_deal_per_minute")) / 60d;
            if (dps > 0) packet = packet.Add(type, new(dps, dps));
        }
        double penalty = minion ? Math.Max(0, 1 + Stat(c, effect, 84,
            global::Stat.MonsterPenaltyAgainstMinionsDamagePlusPctFinalVsPlayerMinions) / 100d) : 1;
        // Conversion affects hits only; the stat set already specifies each degeneration type.
        return packet.Scale(penalty);
    }

    internal sealed partial class Fight
    {
        DamagePacket Ground(Player c, (int X, int Y) at, bool minion, double now)
        {
            var packet = DamagePacket.Empty;
            if (storms.Any(s => Distance(at, s.Monster.Walk.Position(now)) < 38)) packet = packet.Add(Dot(c, minion ? 10981 : 6833, minion));
            if (storms.Any(s => Distance(at, s.Monster.Walk.Position(now)) < 13)) packet = packet.Add(Dot(c, minion ? 10982 : 6834, minion));
            if (quads.Any(q => now >= q.From && Enumerable.Range(0, 4).Any(i =>
                KitavaActTen.LineDistance(at, q.At, (q.At.X + (int)(Math.Cos(q.Angle + (now - q.From) * .32 + i * Math.PI / 2) * 80),
                    q.At.Y + (int)(Math.Sin(q.Angle + (now - q.From) * .32 + i * Math.PI / 2) * 80))) < 3)))
                packet = packet.Add(Dot(c, 10973, minion));
            if (patches.Any(g => Distance(at, g.At) <= g.Radius)) packet = packet.Add(Dot(c, 10945, minion));
            return packet;
        }

        async Task GroundDamage(Player p, double now, double seconds)
        {
            bool died = false, shieldChanged = false, manaChanged = false;
            await SendWorldRecord(p, p.Id, [], null, atGate: () =>
            {
                if (stopped || !CurrentGameWork || p.Closed || p.LeavingArea || p.Paused) return null;
                var e = Exposures.GetValue(p, _ => new());
                var stats = CharacterPlayerStats(p);
                int Get(uint key) => stats.GetValueOrDefault(key);
                var raw = p.PlayerLife == 0 || p.Immune || Get(global::Stat.BaseCannotBeDamaged) > 0 || InGracePeriod(p) ? DamagePacket.Empty : Ground(p, p.Walk.Position(now), false, now);
                var immune = PlayerImmunities(p);
                double shieldBefore = p.EnergyShield.Current;
                uint lifeBefore = p.PlayerLife;
                uint manaBefore = p.PlayerMana;
                int lifeDamage = 0;
                foreach (var type in DamageTypes.ConversionOrder)
                {
                    int slot = (int)type;
                    double resistance = type == DamageType.Physical ? PlayerDamageReduction(p, stats).Physical
                        : Math.Min(Resistance(p, type), MaxResistance(p, type));
                    double factor = p.ElementalAilments.DamageTakenFactor(now, Get(global::Stat.BaseDamageTakenPlusPct)
                        + Get(global::Stat.Of(type.ToString().ToLowerInvariant() + "_damage_taken_+%")))
                        * IncomingResourceDefences.FinalTakenFactor(p.Pools, Get);
                    double earned = (immune.Contains(type) ? 0 : raw[type].Min * Math.Max(0, 1 - resistance / 100d) * seconds * factor) + e.DotCarry[slot];
                    int damage = (int)Math.Min(int.MaxValue, Math.Floor(earned)); e.DotCarry[slot] = earned - damage;
                    lifeDamage += type == DamageType.Chaos
                        ? DamagePlayerChaosOverTime(p, damage, now, stats)
                        : IncomingResourceDefences.DamageOverTime(p.EnergyShield, damage, Get(IncomingResourceDefences.BypassStat), now, ref p.NonChaosDotBypassCarry);
                }
                uint lifeAfter = VaalImmortalCallLife(p, (uint)Math.Max(0, (long)p.PlayerLife - lifeDamage), now);
                RecordRechargeLifeDamage(p, (long)p.PlayerLife - lifeAfter, now, stats);
                p.PlayerLife = lifeAfter;
                shieldChanged = p.EnergyShield.Current != shieldBefore;
                manaChanged = manaBefore != p.PlayerMana;
                if (died = lifeBefore > 0 && p.PlayerLife == 0)
                { p.InvalidateIncomingHits(); RecordHardcoreDeath(p); }
                foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                {
                    if (g.Monster.Dead || g.Monster.Invulnerable || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning") continue;
                    var hit = Ground(p, g.Monster.Walk.Position(now), true, now);
                    int MinionStat(uint key) => g.Player.GetValueOrDefault(key);
                    bool elementalImmune = g.Definition.IsGolem && (MinionStat(global::Stat.GolemImmunityToElementalDamage) > 0
                        || g.Definition.Kind is "Flame" or "Ice" or "Lightning" && MinionStat(global::Stat.ElementalGolemImmunityToElementalDamage) > 0);
                    double total = 0;
                    foreach (var type in DamageTypes.ConversionOrder)
                    {
                        if (elementalImmune && DamageTypes.IsElemental(type)) continue;
                        var res = MonsterHitResistance(g.Monster, type, default);
                        total += DamageModel.Mitigate(hit[type].Min, res.Value, res.Maximum);
                    }
                    double amount = total * seconds + carry.GetValueOrDefault((p, g.Id));
                    int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount)); carry[(p, g.Id)] = amount - damage;
                    int incoming = damage;
                    damage = g.ShieldPool.Damage(damage, bypass: false, now);
                    PublishEnergyShieldLocked(p, g);
                    g.Monster.Life = (uint)Math.Max(0, (long)g.Monster.Life - damage);
                    if (incoming > 0) p.Minions.OutputQueue.Add(new([], "Sirus ground damages minion", g.Id, g.Monster, PlayerMinions.OutputKind.Life,
                        DeathKind: DotDeathKind(g.Monster, g.Monster.Life)));
                    if (g.Monster.Life == 0) GolemDieLocked(p, g, now);
                }
                return lifeBefore == p.PlayerLife ? null : InstanceProtocol.LifeUpdate(p.Id, p.PlayerLife, p.PlayerReservedLife,
                    u2c: p.PlayerLife == 0 ? InstanceProtocol.DeathKindDegen : InstanceProtocol.DeathKindNormal);
            });
            if (shieldChanged) await SendPlayerEnergyShield(p);
            if (manaChanged) await SendPlayerMana(p, "[Sirus] ground mana");
            if (died) await PlayerDied(p, "[Sirus] ground damage", deathKind: InstanceProtocol.DeathKindDegen);
        }
    }
}
