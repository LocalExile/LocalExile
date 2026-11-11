using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

static partial class KitavaActTen
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/act10/Kitava/";
    internal static readonly int[] Effects = [6587, 6592, 6596, 8484, 6589, 8508, 8510, 8512, 8486, 8488, 8490, 8492, 8494,
        8496, 8498, 8500, 8502, 8504, 8506, 6625, 6629, 6633, 8678, 8682, 8686, 8691, 8693, 8680, 8684, 8688];
    static readonly ConditionalWeakTable<SkillCatalog, ConcurrentDictionary<(int, byte), (int Level, Dictionary<uint, int> Stats, double Multiplier, int Crit)>> Native = new();
    static readonly ConditionalWeakTable<ClientState, Exposure> Exposures = new();
    sealed class Exposure { public double Dps; }
    static (int Level, Dictionary<uint, int> Stats, double Multiplier, int Crit) Data(ClientState c, int effect, byte level) =>
        Native.GetValue(Game.SkillsFor(c), _ => new()).GetOrAdd((effect, level), p => GolemData.Skill(Game.SkillsFor(c), p.Item1, p.Item2));
    static int Stat(ClientState c, int effect, byte level, string name) => Stats.Key(name) is uint key ? Data(c, effect, level).Stats.GetValueOrDefault(key) : 0;
    static ushort Skill(int effect) => (ushort)(0x4000 + Array.IndexOf(Effects, effect));
    internal static MonsterAttack Attack(ClientState c, MonsterCatalog.Variety v, byte level, int effect)
    {
        var row = Data(c, effect, level); bool spell = effect is not (6592 or 6596);
        var damage = DamagePacket.Empty;
        if (spell)
            foreach (var type in DamageTypes.ConversionOrder)
            {
                string slug = type.ToString().ToLowerInvariant();
                int min = Stat(c, effect, level, $"spell_minimum_base_{slug}_damage"), max = Stat(c, effect, level, $"spell_maximum_base_{slug}_damage");
                if (max > 0) damage = damage.Add(type, new(min, max));
            }
        else
        {
            var (min, max) = Monsters.BaseDamage(v, level) ?? (1, 1);
            damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(row.Multiplier * Math.Max(0, 1 + Stat(c, effect, level, "active_skill_damage_+%_final") / 100d));
        }
        double fire = Math.Clamp(Stat(c, effect, level, "skill_physical_damage_%_to_convert_to_fire"), 0, 100) / 100d;
        if (fire > 0)
        {
            var physical = damage[DamageType.Physical];
            var original = damage;
            damage = DamagePacket.Of(DamageType.Physical, physical.Min * (1 - fire), physical.Max * (1 - fire)).Add(DamageType.Fire, physical.Scale(fire));
            foreach (var type in DamageTypes.ConversionOrder.Where(t => t != DamageType.Physical)) damage = damage.Add(type, original[type]);
        }
        double seconds = Game.SkillsFor(c).CastTimeMs(effect) / 1000d;
        seconds /= Math.Max(.01, 1 + Stat(c, effect, level, spell ? "active_skill_cast_speed_+%_final" : "active_skill_attack_speed_+%_final") / 100d);
        return new(Skill(effect), Math.Max(.1, seconds), 170, damage) { Spell = spell, Scripted = true };
    }
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState c, MonsterCatalog.Variety v, byte level)
    {
        if (v.Id == Body)
            return [.. Effects.Select(e => Game.SkillsFor(c).FindByRow(Skill(e), e, Data(c, e, level).Level,
                WithBehaviourVariation(c, e, Attack(c, v, level, e).InPlaceSkillStats()))).OfType<GrantedSkill>()];
        if (v.Id is Sin or Innocence)
        {
            int[] effects = v.Id == Sin ? [2790, 2791] : [2788, 2789];
            return [.. effects.Select((e, i) => Game.SkillsFor(c).FindByRow((ushort)(0x4000 + i), e, Data(c, e, level).Level,
                WithBehaviourVariation(c, e, new MonsterAttack((ushort)(0x4000 + i), 1.5, 170, DamagePacket.Empty) { Spell = true }.InPlaceSkillStats()))).OfType<GrantedSkill>()];
        }
        return null;
    }
    internal static double LineDistance((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
    {
        double x = b.X - a.X, y = b.Y - a.Y, length = x * x + y * y;
        double t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * x + (p.Y - a.Y) * y) / length, 0, 1);
        return Math.Sqrt(Math.Pow(p.X - a.X - t * x, 2) + Math.Pow(p.Y - a.Y - t * y, 2));
    }
    readonly record struct Contact(double Fraction, double X, double Y);
    // Sparse samples of the native AMD contact tracks, in model units; their time is relative to the full clip.
    static IReadOnlyDictionary<int, Contact[]> Tracks => NativeContactTracks;
    static readonly IReadOnlyDictionary<int, Contact[]> NativeContactTracks = new Dictionary<int, Contact[]>
    {
        [6592] = [new(0.2619, -488.08, -27.18), new(0.2951, -493.54, -116.17), new(0.3619, -478.52, -257.23), new(0.4333, -427.06, -392.88), new(0.5047, -329.05, -520.59), new(0.5761, -182.64, -630.27), new(0.6476, 12.46, -683.04), new(0.719, 222.09, -645.08)],
        [6596] = [new(0.2619, 426.95, -55.22), new(0.3, 416.15, -146.12), new(0.3619, 377.55, -243.62), new(0.4333, 311.07, -358.39), new(0.5047, 199.12, -496.75), new(0.5761, 55.48, -593.61), new(0.6476, -92.03, -628.5), new(0.719, -229.29, -608.36)],
        [8484] = [new(0.4332, -313.75, -266.55), new(0.46, -241.66, -307.91), new(0.4932, -137.09, -342.26), new(0.52, -46.36, -355.92), new(0.5466, 46.4, -355.92), new(0.5732, 137.13, -342.26), new(0.6066, 241.66, -307.91), new(0.6332, 314.64, -267.42)],
        [8486] = [new(0.4332, -577.8, -69.68), new(0.46, -558.45, -122.08), new(0.4932, -514.55, -183.64), new(0.52, -466.5, -227.97), new(0.5466, -408.76, -265.42), new(0.5732, -343.85, -294.35), new(0.6066, -257.58, -317.2), new(0.6332, -187.16, -323.86)],
        [8488] = [new(0.4332, 108.75, -327.44), new(0.46, 190.64, -325.71), new(0.4932, 290.97, -299.3), new(0.52, 366.4, -261.35), new(0.5466, 433.46, -209.53), new(0.5732, 489.22, -146.11), new(0.6066, 540.05, -55.71), new(0.6332, 563.65, 22.79)],
        [8490] = [new(0.4332, -416.87, -557.77), new(0.46, -321.09, -607.77), new(0.4932, -182.15, -649.3), new(0.52, -61.59, -665.82), new(0.5466, 61.66, -665.82), new(0.5732, 182.21, -649.3), new(0.6066, 321.09, -607.77), new(0.6332, 418.06, -558.82)],
        [8492] = [new(0.4332, -782.12, -47.75), new(0.46, -756.39, -159.19), new(0.4932, -692.21, -292.9), new(0.52, -619.62, -391.34), new(0.5466, -530.77, -476.76), new(0.5732, -429.55, -545.43), new(0.6066, -293.46, -604.26), new(0.6332, -181.16, -627.36)],
        [8494] = [new(0.4332, 205.92, -636.47), new(0.46, 317.39, -610.87), new(0.4932, 451.18, -546.83), new(0.52, 549.7, -474.35), new(0.5466, 635.22, -385.6), new(0.5732, 704.0, -284.46), new(0.6066, 762.98, -148.44), new(0.6332, 786.2, -36.16)],
        [8496] = [new(0.4332, -624.11, -786.16), new(0.46, -480.71, -870.48), new(0.4932, -272.7, -940.51), new(0.52, -92.21, -968.37), new(0.5466, 92.31, -968.37), new(0.5732, 272.79, -940.51), new(0.6066, 480.71, -870.48), new(0.6332, 625.88, -787.93)],
        [8498] = [new(0.4332, -1025.5, -84.82), new(0.46, -994.88, -208.78), new(0.4932, -925.38, -369.42), new(0.52, -849.28, -496.72), new(0.5466, -757.8, -616.42), new(0.5732, -654.95, -723.29), new(0.6066, -518.25, -832.48), new(0.6332, -406.65, -896.37)],
        [8500] = [new(0.4332, 340.49, -869.57), new(0.46, 472.74, -814.97), new(0.4932, 633.14, -709.37), new(0.52, 752.51, -601.14), new(0.5466, 857.42, -476.11), new(0.5732, 943.28, -339.76), new(0.6066, 1019.37, -163.53), new(0.6332, 1052.25, -23.5)],
        [8502] = [new(0.3094, -313.28, -419.17), new(0.3523, -136.89, -487.95), new(0.3904, 46.33, -500.37), new(0.4333, 241.3, -456.75), new(0.638, 392.56, -743.05), new(0.6809, 75.38, -814.02), new(0.719, -222.69, -793.82), new(0.7619, -509.66, -681.92)],
        [8504] = [new(0.3094, -522.48, -29.45), new(0.3523, -461.78, -208.79), new(0.3904, -353.52, -357.12), new(0.4333, -194.78, -478.44), new(0.638, -316.88, -778.34), new(0.6809, -575.12, -580.99), new(0.719, -751.25, -339.67), new(0.7619, -849.99, -47.91)],
        [8506] = [new(0.3094, 77.15, -508.64), new(0.3523, 235.13, -410.46), new(0.3904, 360.22, -276.16), new(0.4333, 455.77, -102.13), new(0.638, 741.47, -142.38), new(0.6809, 586.02, -425.49), new(0.719, 382.53, -643.98), new(0.7619, 125.51, -803.71)],
    };
    sealed record Hit(double At, int Generation, int Group, int Effect, (int X, int Y) Aim, double Radius,
        (int X, int Y)? End = null, bool Cross = false, bool Orb = false);
    sealed record Patch((int X, int Y) At, double From, double Until, double Radius, int Effect, DamageType Type);
    sealed record Visual(uint Id, (int X, int Y) At, string Model, uint Clip, double Until, float Rotation = 0);
    sealed class Orb((int X, int Y) at, int effect, double until, double next)
    { public readonly (int X, int Y) At = at; public readonly int Effect = effect; public readonly double Until = until; public double Next = next; }
    internal sealed partial class Fight
    {
        readonly List<Hit> hits = [];
        readonly List<Patch> patches = [];
        readonly List<Visual> visuals = [];
        readonly List<Orb> orbs = [];
        readonly HashSet<(int, ClientState, uint)> struck = [];
        readonly Dictionary<(ClientState, uint), double> carry = [];
        int rotation, group;
        double lastGround;
        readonly Dictionary<int, double> cooldowns = [];
        void CancelCombat() { hits.Clear(); struck.Clear(); body.ActionGeneration++; body.SwingGeneration = null; }
        (int X, int Y) ModelCell(double x, double y)
        {
            double cos = Math.Cos(anchor.Rotation), sin = Math.Sin(anchor.Rotation);
            return (anchor.X + (int)Math.Round((x * cos - y * sin) / WorldUnits.PerCell),
                anchor.Y + (int)Math.Round((x * sin + y * cos) / WorldUnits.PerCell));
        }
        async Task Combat(ClientState c, double now)
        {
            if (now < ready || InGracePeriod(c)) return;
            int[] cycle = Round switch
            {
                0 => [6587, 8484, 8508, 6592, 8488, 6625, 6596, 8490, 8510, 8496, 6629, 8486, 8492, 8494, 8498, 8500, 8512],
                1 => [6589, 8678, 8502, 6587, 6625, 8508, 6592, 8682, 8504, 6596, 8686, 8506, 6629, 8484],
                _ => [8680, 8691, 6633, 6589, 8502, 8684, 6592, 8693, 8688, 8506, 6596, 8508, 6587],
            };
            int effect = cycle[rotation++ % cycle.Length];
            if (cooldowns.GetValueOrDefault(effect) > now) { ready = now + .2; return; }
            var attack = Attack(c, body.Variety!, body.Level, effect); var aim = c.Walk.Position(now);
            cooldowns[effect] = now + Game.SkillsFor(c).CooldownMs(effect, Data(c, effect, body.Level).Level) / 1000d;
            int generation;
            lock (c.Monsters) { generation = ++body.ActionGeneration; body.SwingGeneration = null; body.BusyUntil = ready = now + attack.Seconds + .2; }
            int cast = ++group;
            await All(id, [InstanceProtocol.ActionStarted(id, anchor.X, anchor.Y, aim.X, aim.Y, attack.SkillId, c.NextActionSequence(), 0x0408), InstanceProtocol.ActionEnded(id)], true);
            Console.WriteLine($"[Kitava A10] interval {Round + 1}, {Game.SkillsFor(c).EffectId(effect)}");
            if (Tracks.TryGetValue(effect, out var contacts))
            {
                for (int i = 0; i < contacts.Length; i++)
                {
                    var contact = contacts[i]; var at = ModelCell(contact.X, contact.Y);
                    var end = i + 1 < contacts.Length ? ModelCell(contacts[i + 1].X, contacts[i + 1].Y) : at;
                    hits.Add(new(now + attack.Seconds * contact.Fraction, generation, cast, effect, at, effect is 6592 or 6596 ? 12 : 7, end));
                }
            }
            else if (effect is 6587 or 6589)
            {
                double delay = effect == 6589 ? attack.Seconds : attack.Seconds * .32;
                hits.Add(new(now + delay, generation, cast, effect, aim, effect == 6589 ? 65 : 42, Cross: true));
                if (effect == 6589) await Fx(c, aim, Art + "x_nuke/marker.ao", now + delay, clip: null);
            }
            else if (effect is 6625 or 6629 or 6633)
            {
                double duration = Math.Max(1, Stat(c, effect, body.Level, "base_skill_effect_duration") / 1000d);
                int[] sides = effect == 6633 ? [-1, 1] : [effect == 6625 ? -1 : 1];
                foreach (int side in sides)
                {
                    var at = ModelCell(side * 310, -520);
                    patches.Add(new(at, now + attack.Seconds * .32, now + attack.Seconds * .32 + duration, 24, effect, DamageType.Physical));
                    await Fx(c, at, Art + "devouring_darkness/devourer_portal/devourer_portal_rig.ao", now + duration + attack.Seconds, clip: null);
                }
            }
            else if (effect is 8678 or 8682 or 8686 or 8680 or 8684 or 8688)
            {
                double duration = Math.Max(1, Stat(c, effect, body.Level, "base_skill_effect_duration") / 1000d);
                double start = now + attack.Seconds * .6;
                var at = c.Layout.Near((aim.X + (effect is 8682 or 8684 ? -20 : effect is 8686 or 8688 ? 20 : 0), aim.Y + 12));
                orbs.Add(new(at, effect, start + duration, start + .8));
                await Fx(c, at, Art + "beam_ball/beam_ball.ao", start + duration, clip: null);
            }
            else
            {
                bool ultimate = effect is 8691 or 8693;
                int side = effect is 8510 or 8691 ? -1 : effect is 8512 or 8693 ? 1 : 0;
                if (Round < 2)
                {
                    // Default stance omits these two ACT attachments; stance3 supplies them itself.
                    uint clip = effect == 8508 ? 0u : effect == 8510 ? 2u : 1u;
                    await Fx(c, (anchor.X, anchor.Y), Art + "death_breath/rig.ao", now + 5, clip, anchor.Rotation);
                    await Fx(c, (anchor.X, anchor.Y), Art + "DB_burning_grd/DB_burning_grd.ao", now + 6.433, clip, anchor.Rotation);
                }
                // Three normal breaths leave neighbouring lanes safe; the ultimate covers all but the far-side strip.
                double from = ultimate ? .333 : .4, until = ultimate ? .9 : .87;
                for (int tick = 0; tick < (ultimate ? 7 : 4); tick++)
                {
                    double at = now + attack.Seconds * (from + (until - from) * tick / (ultimate ? 6 : 3));
                    var a = ModelCell((side - (ultimate ? 1 : 0)) * 320, -300);
                    var b = ModelCell((side + (ultimate ? 1 : 0)) * 320, -930);
                    hits.Add(new(at, generation, ++group, effect, a, ultimate ? 35 : 21, b));
                }
            }
        }
        async Task CombatMaintenance(ClientState c, double now)
        {
            foreach (var fx in visuals.Where(v => v.Until <= now).ToArray()) { visuals.Remove(fx); await AllRemoval(fx.Id); }
            patches.RemoveAll(p => p.Until <= now); orbs.RemoveAll(o => o.Until <= now);
            if (stage is 2 or 3 or 4)
                foreach (var orb in orbs.Where(o => o.Next <= now).ToArray())
                {
                    var target = Present.FirstOrDefault(p => p.PlayerLife > 0 && Awake(p));
                    if (target is null) continue;
                    var to = target.Walk.Position(now); double angle = Math.Atan2(to.Y - orb.At.Y, to.X - orb.At.X);
                    var end = (orb.At.X + (int)(Math.Cos(angle) * 120), orb.At.Y + (int)(Math.Sin(angle) * 120));
                    hits.Add(new(now + .65, body.ActionGeneration, ++group, orb.Effect, orb.At, 4, end, Orb: true));
                    await Fx(c, orb.At, Art + "beam_ball/beam_ball_beam.ao", now + .8, clip: null, rotation: (float)(angle + Math.PI / 2));
                    orb.Next = now + 1.5;
                }
            foreach (var hit in hits.Where(h => h.At <= now).ToArray())
            {
                hits.Remove(hit);
                if (stage is not (2 or 3 or 4) || !hit.Orb && hit.Generation != body.ActionGeneration) continue;
                if (hit.Effect is 6587 or 6589) await Fx(c, hit.Aim, Art + (hit.Effect == 6589 ? "x_nuke/explosion.ao" : "X_skill/explosionx.ao"), now + 2, clip: null);
                foreach (var p in Present.ToArray()) await Damage(p, hit);
            }
            if (stage == 2 && now >= nextAlly && sinId != 0 && innocenceId != 0 && Round < 2)
            {
                foreach (uint ally in new[] { sinId, innocenceId }) await Basic(c, ally, 0x4001, now, 1.5, id);
                nextAlly = now + 4;
            }
            if (now < lastGround + .25) return;
            double seconds = Math.Clamp(now - lastGround, 0, 1); lastGround = now;
            foreach (var p in Present.ToArray())
                lock (p.Monsters)
                {
                    bool Active((int X, int Y) at, Patch g) => stage is 2 or 3 or 4 && now >= g.From && Distance(at, g.At) <= g.Radius;
                    double Dps(Patch g) => g.Type == DamageType.Physical ? Stat(c, g.Effect, body.Level, "base_physical_damage_to_deal_per_minute") / 60d
                        : Math.Max(0, Stat(c, g.Effect, body.Level, "base_fire_damage_to_deal_per_minute") / 60d);
                    var e = Exposures.GetValue(p, _ => new());
                    double dps = p.PlayerLife == 0 || p.Paused || p.Immune || InGracePeriod(p) ? 0 : patches.Where(g => Active(p.Walk.Position(now), g) && !PlayerImmunities(p).Contains(g.Type))
                        .GroupBy(g => g.Type).Sum(g => g.Max(h => Dps(h) * (h.Type == DamageType.Fire ? Math.Max(0, 1 - Math.Min(Resistance(p, h.Type), MaxResistance(p, h.Type)) / 100d) : 1)));
                    p.TrapDegenPerSecond = Math.Max(0, p.TrapDegenPerSecond - e.Dps + dps); e.Dps = dps;
                    foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                    {
                        if (!GolemCurrent(p, g) || g.Monster.Dead || g.Monster.Invulnerable || g.Definition.Kind is "Agony" or "SkitterCold" or "SkitterLightning") continue;
                        double rate = patches.Where(h => Active(g.Monster.Walk.Position(now), h)).GroupBy(h => h.Type).Sum(h => h.Max(patch =>
                        {
                            var res = MonsterHitResistance(g.Monster, patch.Type, default);
                            return Dps(patch) * (patch.Type == DamageType.Fire ? Math.Max(0, 1 - Math.Min(res.Value, res.Maximum) / 100d) : 1)
                                * Math.Max(0, 1 + Stat(c, patch.Effect, body.Level, "monster_penalty_against_minions_damage_+%_final_vs_player_minions") / 100d);
                        }));
                        double raw = rate * seconds + carry.GetValueOrDefault((p, g.Id));
                        int damage = (int)Math.Min(int.MaxValue, Math.Floor(raw)); carry[(p, g.Id)] = raw - damage;
                        int shield = Math.Min((int)g.Shield, damage); g.Shield -= shield; damage -= shield;
                        g.Monster.Life = (uint)Math.Max(0, (long)g.Monster.Life - damage);
                        if (damage > 0 || shield > 0) p.Minions.OutputQueue.Add(new([], "Kitava ground damages minion", g.Id, g.Monster, PlayerMinions.OutputKind.Life));
                        if (g.Monster.Life == 0) GolemDieLocked(p, g, now);
                    }
                }
        }
        async Task Damage(ClientState p, Hit hit)
        {
            var w = p.Wire; MonsterHitOutcome? outcome = null; var attack = Attack(p, body.Variety!, body.Level, hit.Effect);
            bool Inside((int X, int Y) at) => hit.Cross ? Distance(at, hit.Aim) <= hit.Radius
                && Math.Min(Math.Abs(at.X - hit.Aim.X - at.Y + hit.Aim.Y), Math.Abs(at.X - hit.Aim.X + at.Y - hit.Aim.Y)) <= (hit.Effect == 6589 ? 11 : 6)
                : hit.End is { } end ? LineDistance(at, hit.Aim, end) <= hit.Radius : Distance(at, hit.Aim) <= hit.Radius;
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, p, id, [], null, atGate: () =>
            {
                lock (p.Monsters)
                {
                    double now = Math.Max(p.Clock.Elapsed.TotalSeconds, ProjectileWorldTime(p));
                    if (stopped || !CurrentGameWork || stage is not (2 or 3 or 4) || p.Closed || p.LeavingArea || p.Paused || InGracePeriod(p)) return null;
                    var ma = attack with { Damage = attack.Damage.Scale(Math.Max(0, 1 + Stat(p, hit.Effect, body.Level, "monster_penalty_against_minions_damage_+%_final_vs_player_minions") / 100d)) };
                    foreach (var g in p.Golems.All.Values.Concat(p.Reservations.Fighters.Values).Concat(p.Summons.Fighters.Values).ToArray())
                        if (!g.Monster.Dead && Inside(g.Monster.Walk.Position(now)) && struck.Add((hit.Group, p, g.Id))) GolemTakeHitLocked(p, g, id, body, ma, now, area: true);
                    if (p.PlayerLife == 0 || !Inside(p.Walk.Position(now)) || !struck.Add((hit.Group, p, PlayerId))) return null;
                    outcome = CommitMonsterHitLocked(w.Remote, p, id, body, attack, now, p.Walk.Position(now), hit.Aim, false, p.NextActionSequence());
                    return outcome?.HitPacket;
                }
            });
            if (outcome is not null) await PresentMonsterHit(w.Remote, w.Stream, w.Ciphers, p, id, outcome);
        }
        async Task Fx(ClientState c, (int X, int Y) at, string model, double until, uint? clip = 0, float rotation = 0)
        {
            var fx = new Visual(c.Instance.World.NewMonsterId(), at, model, clip ?? uint.MaxValue, until, rotation); visuals.Add(fx);
            foreach (var p in Present.ToArray()) await CreateFx(p, fx);
        }
        static async Task CreateFx(ClientState p, Visual fx)
        {
            var obj = new EffectObject(fx.At.X, fx.At.Y, fx.Model) { Rotation = fx.Rotation };
            await SendWorldCreate(p, fx.Id, () => InstanceEntities.CreateObject(fx.Id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), obj.Blob()));
            await SendWorldRecord(p, fx.Id, () => InstanceProtocol.EnterWakeZone(fx.Id));
            if (fx.Clip != uint.MaxValue) await SendWorldRecord(p, fx.Id, () => InstanceProtocol.AnimationUpdate(fx.Id, 0, fx.Clip, 1));
        }
    }
}
