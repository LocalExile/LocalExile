using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

static partial class Sirus
{
    // The beam variants carry these effects in their own stat sets, independently of a critical strike.
    internal static void OnHit(ClientState c, SpawnedMonster m, MonsterAttack attack, DamagePacket baseHit,
        DamagePacket taken, bool critical, bool blocked, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (!IsActor(m.Variety) || blocked || c.Immune || c.PlayerLife == 0 || InGracePeriod(c)) return;
        int index = attack.SkillId - 0x4000;
        var effects = Effects(m.Variety!);
        if (index < 0 || index >= effects.Length) return;
        int effect = effects[index];
        int Native(string name) => Stat(c, effect, m.Level, name);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        double previousAction = c.ElementalAilments.ActionFactor(now);
        if (Native("always_ignite") != 0 && !critical)
            c.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Ignite,
                (baseHit[DamageType.Fire].Min + baseHit[DamageType.Fire].Max) * .5 * .9, 4, now, Get, c.IncomingHitRandom);
        double cold = taken[DamageType.Cold].Min / Math.Max(1, c.PlayerMaxLife);
        if (Native("always_freeze") != 0)
            c.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Freeze, 100, Math.Min(3, 6 * cold), now, Get, c.IncomingHitRandom);
        if (Native("active_skill_chill_effect_+%_final") != 0)
            c.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Chill,
                50 * Math.Pow(cold, .4) * Math.Max(0, 1 + Native("active_skill_chill_effect_+%_final") / 100d),
                2, now, Get, c.IncomingHitRandom);
        if (Native("always_shock") != 0)
            c.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Shock,
                50 * Math.Pow(taken[DamageType.Lightning].Min / Math.Max(1, c.PlayerMaxLife), .4),
                2 * Math.Max(0, 1 + Native("active_skill_shock_duration_+%_final") / 100d), now, Get, c.IncomingHitRandom);
        PlayerAilmentsChangedLocked(c, previousAction, now);

        int stacks = Native("corrupted_blood_on_hit_num_stacks");
        if (stacks == 0 || BloodImmune(Get) || RandomRolls.Chance(Get("avoid_corrupted_blood_%_chance"), 100, c.IncomingHitRandom)) return;
        var e = Exposures.GetValue(c, _ => new());
        if (Get("cannot_gain_corrupted_blood_while_you_have_at_least_5_stacks") != 0 && e.BloodStacks >= 5 && e.BloodUntil > now) return;
        if (e.BloodUntil <= now) { e.BloodStacks = 0; e.BloodDps = 0; }
        e.BloodStacks = Math.Min(10, e.BloodStacks + stacks);
        e.BloodUntil = now + Native("corrupted_blood_on_hit_duration") / 1000d;
        e.BloodDps = Math.Max(e.BloodDps, (baseHit.MinTotal + baseHit.MaxTotal) * .5
            * Native("corrupted_blood_on_hit_%_average_damage_to_deal_per_minute_per_stack") / 6000d);
        BloodIcon(c, e, now);
    }

    static bool BloodImmune(Func<string, int> get) => get("cannot_be_inflicted_by_corrupted_blood") != 0
        || get("immune_to_corrupted_blood") != 0;

    static void BloodIcon(ClientState c, Exposure e, double now)
    {
        if (e.BloodBuff != 0)
            c.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, e.BloodBuff), null, WorldId: PlayerId));
        e.BloodBuff = e.BloodStacks == 0 ? (ushort)0 : c.NextBuff++;
        if (e.BloodBuff != 0)
            c.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, e.BloodBuff, 238,
                values: [0], u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, e.BloodUntil - now)), stacks: (ushort)e.BloodStacks),
                "[Sirus] corrupted blood", WorldId: PlayerId));
    }

    internal static DamagePacket Dot(ClientState c, int effect, bool minion)
    {
        var packet = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            double dps = Stat(c, effect, 84, "base_" + type.ToString().ToLowerInvariant() + "_damage_to_deal_per_minute") / 60d;
            if (dps > 0) packet = packet.Add(type, new(dps, dps));
        }
        double penalty = minion ? Math.Max(0, 1 + Stat(c, effect, 84,
            "monster_penalty_against_minions_damage_+%_final_vs_player_minions") / 100d) : 1;
        // Conversion affects hits only; the stat set already specifies each degeneration type.
        return packet.Scale(penalty);
    }

    internal sealed partial class Fight
    {
        DamagePacket Ground(ClientState c, (int X, int Y) at, bool minion, double now)
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

        async Task GroundDamage(ClientState p, double now, double seconds)
        {
            var w = p.Wire;
            bool died = false, shieldChanged = false;
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, PlayerId, [], null, atGate: () =>
            {
                lock (p.Monsters)
                {
                    if (stopped || !CurrentGameWork || p.Closed || p.LeavingArea || p.Paused) return null;
                    var e = Exposures.GetValue(p, _ => new());
                    var stats = CharacterPlayerStats(p);
                    int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
                    if (e.BloodStacks > 0 && (e.BloodUntil <= now || BloodImmune(Get) || p.PlayerLife == 0))
                    { e.BloodStacks = 0; e.BloodDps = 0; BloodIcon(p, e, now); }
                    var raw = p.PlayerLife == 0 || p.Immune || InGracePeriod(p) ? DamagePacket.Empty : Ground(p, p.Walk.Position(now), false, now);
                    if (p.PlayerLife > 0 && !p.Immune && !InGracePeriod(p) && e.BloodStacks > 0)
                        raw = raw.Add(DamageType.Physical, new(e.BloodDps * e.BloodStacks, e.BloodDps * e.BloodStacks));
                    var immune = PlayerImmunities(p);
                    double shieldBefore = p.EnergyShield.Current;
                    uint lifeBefore = p.PlayerLife;
                    int lifeDamage = 0;
                    foreach (var type in DamageTypes.ConversionOrder)
                    {
                        int slot = (int)type;
                        double resistance = type == DamageType.Physical ? PlayerDamageReduction(p, stats).Physical
                            : Math.Min(Resistance(p, type), MaxResistance(p, type));
                        double factor = p.ElementalAilments.DamageTakenFactor(now, Get("base_damage_taken_+%")
                            + Get(type.ToString().ToLowerInvariant() + "_damage_taken_+%"))
                            * Math.Max(0, 1 + Get(IncomingResourceDefences.TakenStat) / 100d)
                            * Math.Max(0, 1 - IncomingResourceDefences.LessTakenPct(p.Pools, Get) / 100d);
                        double earned = (immune.Contains(type) ? 0 : raw[type].Min * Math.Max(0, 1 - resistance / 100d) * seconds * factor) + e.DotCarry[slot];
                        int damage = (int)Math.Min(int.MaxValue, Math.Floor(earned)); e.DotCarry[slot] = earned - damage;
                        bool chaosBypasses = type == DamageType.Chaos && Get("chaos_damage_does_not_bypass_energy_shield") == 0
                            && Get("virtual_chaos_damage_does_not_bypass_energy_shield") == 0
                            && !(Get("chaos_damage_does_not_bypass_energy_shield_while_not_low_life") != 0
                                && (long)p.PlayerLife * 100 > (long)p.PlayerMaxLife * LowLifeThresholdPct(stats))
                            && !(Get("chaos_damage_does_not_bypass_energy_shield_while_not_low_mana") != 0
                                && (long)p.PlayerMana * 2 > p.Pools.Mana);
                        lifeDamage += type == DamageType.Chaos ? p.EnergyShield.Damage(damage, chaosBypasses, now)
                            : IncomingResourceDefences.DamageOverTime(p.EnergyShield, damage, Get(IncomingResourceDefences.BypassStat), now, ref p.NonChaosDotBypassCarry);
                    }
                    p.PlayerLife = (uint)Math.Max(0, (long)p.PlayerLife - lifeDamage);
                    shieldChanged = p.EnergyShield.Current != shieldBefore;
                    if (died = lifeBefore > 0 && p.PlayerLife == 0)
                    { p.InvalidateIncomingHits(); RecordHardcoreDeath(p); }
                    foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                    {
                        if (g.Monster.Dead || g.Monster.Invulnerable || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning") continue;
                        var hit = Ground(p, g.Monster.Walk.Position(now), true, now);
                        int MinionStat(string name) => Stats.Key(name) is uint key ? g.Player.GetValueOrDefault(key) : 0;
                        bool elementalImmune = g.Definition.IsGolem && (MinionStat("golem_immunity_to_elemental_damage") > 0
                            || g.Definition.Kind is "Flame" or "Ice" or "Lightning" && MinionStat("elemental_golem_immunity_to_elemental_damage") > 0);
                        double total = 0;
                        foreach (var type in DamageTypes.ConversionOrder)
                        {
                            if (elementalImmune && DamageTypes.IsElemental(type)) continue;
                            var res = MonsterHitResistance(g.Monster, type, default);
                            total += DamageModel.Mitigate(hit[type].Min, res.Value, res.Maximum);
                        }
                        double amount = total * seconds + carry.GetValueOrDefault((p, g.Id));
                        int damage = (int)Math.Min(int.MaxValue, Math.Floor(amount)); carry[(p, g.Id)] = amount - damage;
                        int shield = Math.Min((int)g.Shield, damage); g.Shield -= shield; damage -= shield;
                        g.Monster.Life = (uint)Math.Max(0, (long)g.Monster.Life - damage);
                        if (damage > 0 || shield > 0) p.Minions.OutputQueue.Add(new([], "Sirus ground damages minion", g.Id, g.Monster, PlayerMinions.OutputKind.Life));
                        if (g.Monster.Life == 0) GolemDieLocked(p, g, now);
                    }
                    return lifeBefore == p.PlayerLife ? null : InstanceProtocol.LifeUpdate(PlayerId, p.PlayerLife, p.PlayerReservedLife);
                }
            });
            if (shieldChanged) await SendPlayerEnergyShield(w.Remote, w.Stream, w.Ciphers, p);
            if (died) await PlayerDied(w.Remote, w.Stream, w.Ciphers, p, "[Sirus] ground damage");
        }
    }
}
