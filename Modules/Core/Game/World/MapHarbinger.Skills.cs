using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static HarbingerCatalog;
using static Program;

/// <summary>
/// Harbinger, King helper and Empowered Obelisk real skill execution, delayed area impacts, burning-ground hazards,
/// ally auras (including <c>HarbingerTotemScarab3</c>'s damage-reduction and action-speed auras) and player curse auras.
/// </summary>
public static partial class MapHarbinger
{
    const double SkillEngageRange = 75;
    const double AllyAuraDefaultRadius = 65;
    const double CurseAuraDefaultRadius = 60;
    const double WarpMinOffset = 6, WarpMaxOffset = 18;
    const double ConvocationPullDistance = 20;
    const double MinionEmpowerDuration = 6.0;

    static readonly IReadOnlyDictionary<string, int> WeakMinionBuffStats =
        new Dictionary<string, int>(StringComparer.Ordinal) { ["damage_+%"] = 30, ["base_actor_scale_+%"] = 15 };
    static readonly IReadOnlyDictionary<string, int> StrongMinionBuffStats =
        new Dictionary<string, int>(StringComparer.Ordinal) { ["damage_+%"] = 60, ["base_actor_scale_+%"] = 25 };

    sealed class ActiveTimedBuff(uint sourceId, string auraKey, ushort buffRow, double radius, double remaining,
        IReadOnlyDictionary<string, int> stats, HashSet<uint>? targets = null)
    {
        public uint SourceId { get; } = sourceId;
        public string AuraKey { get; } = auraKey;
        public ushort BuffRow { get; } = buffRow;
        public double Radius { get; } = radius;
        public double Remaining { get; set; } = remaining;
        public IReadOnlyDictionary<string, int> Stats { get; } = stats;
        public HashSet<uint>? Targets { get; } = targets;
    }

    sealed class AppliedAllyAura(uint sourceId, ushort buffRow)
    {
        public uint SourceId { get; } = sourceId;
        public ushort BuffRow { get; } = buffRow;
        public readonly Dictionary<ClientState, ushort> ViewerBuffs = [];
    }

    sealed class GroundHazard
    {
        public required (int X, int Y) At { get; init; }
        public required double Radius { get; init; }
        public required double Remaining { get; set; }
        public double Dps { get; init; }
        public DamageType Type { get; init; } = DamageType.Fire;
        public uint SourceId { get; init; }
        public MonsterAttack? DelayedHit { get; init; }
        public double HitDelay { get; set; }
        public bool HitFired { get; set; }
        public string? ImpactAo { get; init; }
    }

    sealed class PlayerCurseState
    {
        public readonly Dictionary<uint, int> Stats = [];
        public readonly Dictionary<string, ushort> Buffs = new(StringComparer.Ordinal);
    }

    static readonly RuleTable<ClientState, ArenaHazards.Exposure> GroundExposure = new();

    // O(1) zero-alloc lookup when no player stands inside a Harbinger curse aura.
    static readonly RuleTable<ClientState, PlayerCurseState> ActivePlayerCurses = new();

    /// <summary>GameEvents.PlayerStatSources: active Harbinger curse aura stats on this player (zero-alloc early-out when inactive).</summary>
    public static Dictionary<uint, int> PlayerStats(ClientState conn, Dictionary<uint, int> stats)
    {
        if (!ActivePlayerCurses.TryGetValue(conn, out var curse) || curse.Stats.Count == 0) return stats;
        foreach (var (key, value) in curse.Stats)
            stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    static IReadOnlyList<GrantedSkill>? BuildOwnedSkills(ClientState conn, MonsterCatalog.Variety variety, byte level)
    {
        var kit = HarbingerCatalog.KitFor(variety.Id);
        if (kit.Skills.Count == 0) return null;
        var catalog = SkillsFor(conn);
        var granted = new List<GrantedSkill>(kit.Skills.Count);
        foreach (var skill in kit.Skills)
        {
            int effLevel = catalog.MaxLevelAt(skill.EffectRow, level);
            var attack = BuildSkillAttack(conn, skill, level);
            var stats = attack.Projectile is not null
                ? MonsterActionSkillStats(conn, skill.EffectRow, effLevel, attack)
                : WithBehaviourVariation(conn, skill.EffectRow, attack.InPlaceSkillStats());
            if (catalog.FindByRow(skill.SkillId, skill.EffectRow, effLevel, stats) is { } gs)
                granted.Add(gs);
        }
        return granted.Count > 0 ? granted : null;
    }

    static MonsterAttack BuildSkillAttack(ClientState conn, ActorSkill skill, byte level)
    {
        var catalog = SkillsFor(conn);
        var native = GolemData.Skill(catalog, skill.EffectRow, level);
        int Raw(string name) => Stats.Key(name) is uint k ? native.Stats.GetValueOrDefault(k) : 0;

        var damage = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            int min = Raw($"spell_minimum_base_{slug}_damage");
            int max = Raw($"spell_maximum_base_{slug}_damage");
            if (max > 0) damage = damage.Add(type, new(min, Math.Max(min, max)));
        }
        damage = damage.Scale(native.Multiplier);

        // HarbingerUpheaval converts 100% physical spell damage to lightning (skill_physical_damage_%_to_convert_to_lightning).
        if (skill.Role == SkillRole.Upheaval && Raw("skill_physical_damage_%_to_convert_to_lightning") is int pct and > 0
            && damage[DamageType.Physical].Max > 0)
        {
            double frac = Math.Clamp(pct / 100.0, 0, 1);
            var phys = damage[DamageType.Physical];
            damage = damage
                .Add(DamageType.Lightning, phys.Scale(frac))
                .Add(DamageType.Physical, new(-phys.Min * frac, -phys.Max * frac));
        }

        // AttachedVaalChargedBlast2 scales with stage count (charged_blast_spell_damage_+%_final_per_stack = 40; 3 stages).
        if (skill.Role == SkillRole.ChargedBlast && Raw("charged_blast_spell_damage_+%_final_per_stack") is int perStack and > 0)
            damage = damage.Scale(1.0 + 3 * perStack / 100.0);

        SkillCatalog.MonsterProjectileInfo? projectile = skill.Role switch
        {
            SkillRole.FireMortar => BuildFireMortarProjectile(Raw),
            SkillRole.BallLightning => BuildBallLightningProjectile(Raw),
            _ => null,
        };

        return new(skill.SkillId, skill.CastSeconds, SkillEngageRange, damage)
        {
            Spell = true,
            Scripted = true,
            Ranged = projectile is not null,
            Projectile = projectile,
        };
    }

    static SkillCatalog.MonsterProjectileInfo BuildFireMortarProjectile(Func<string, int> raw)
    {
        int count = 1 + Math.Max(0, raw("number_of_additional_projectiles"));
        return new(FireMortarProjectile, FireMortarSpeed, Lobbed: true, CollisionRadius: 2)
        {
            Count = count,
            SpreadRadians = count > 1 ? 0.45 : 0,
            AreaRadius = 10,
        };
    }

    static SkillCatalog.MonsterProjectileInfo BuildBallLightningProjectile(Func<string, int> raw)
    {
        int count = 1 + Math.Max(0, raw("number_of_additional_projectiles"));
        int speedPct = raw("base_projectile_speed_+%");
        int speed = Math.Max(60, (int)Math.Round(BallLightningSpeed * (1.0 + speedPct / 100.0)));
        return new(BallLightningProjectile, speed, Lobbed: false, CollisionRadius: 3)
        {
            Count = count,
            SpreadRadians = count > 1 ? Math.Tau : 0,
        };
    }

    static void SeedEncounterCooldowns(ClientState conn, Area area, Encounter h, double cdScale)
    {
        SeedActorCooldowns(area, h.Monster, h.Variety, 1.0 / cdScale);
        foreach (uint helperId in h.Helpers)
        {
            string? vid = null;
            lock (conn.Monsters)
                if (conn.Monsters.TryGetValue(helperId, out var hm)) vid = hm.Variety?.Id;
            if (vid is not null) SeedActorCooldowns(area, helperId, vid, 0.5 / cdScale);
        }
        foreach (uint obeliskId in h.Obelisks)
        {
            string? vid = null;
            lock (conn.Monsters)
                if (conn.Monsters.TryGetValue(obeliskId, out var om)) vid = om.Variety?.Id;
            if (vid is not null) SeedActorCooldowns(area, obeliskId, vid, 1.0 / cdScale);
        }
    }

    static void SeedActorCooldowns(Area area, uint actorId, string varietyId, double initialDelay)
    {
        if (actorId == 0) return;
        var kit = HarbingerCatalog.KitFor(varietyId);
        if (kit.Skills.Count == 0) return;
        if (!area.ActorCooldowns.TryGetValue(actorId, out var cds))
            area.ActorCooldowns[actorId] = cds = [];
        double stagger = 0;
        foreach (var s in kit.Skills)
        {
            if (s.Role is SkillRole.Summon or SkillRole.AllyDamageAura or SkillRole.PlayerCurseAura) continue;
            cds.TryAdd(s.EffectRow, initialDelay + stagger);
            stagger += 0.35;
        }
    }

    static (int X, int Y) ActorPosition(ClientState conn, uint actorId, (int X, int Y) fallback)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
            return conn.Monsters.TryGetValue(actorId, out var m) && !m.Dead ? m.Walk.Position(now) : fallback;
    }

    static async Task DriveEncounterSkills(ClientState conn, Area area, Encounter h, byte level,
        double seconds, double cdScale, (int X, int Y)[] players)
    {
        if (h.State != HarbingerState.Active) return;

        // 1. Harbinger avatar's own skills.
        await DriveActorKit(conn, area, h, h.Monster, h.Variety, level, seconds, cdScale, players, canWarp: true);

        // 2. King Harbinger's helper avatars (AvatarAtlasMinion1..6).
        foreach (uint helperId in h.Helpers)
        {
            string? vid = null;
            lock (conn.Monsters)
                if (conn.Monsters.TryGetValue(helperId, out var hm) && !hm.Dead) vid = hm.Variety?.Id;
            if (vid is not null)
                await DriveActorKit(conn, area, h, helperId, vid, level, seconds, cdScale, players, canWarp: false);
        }

        // 3. Empowered Obelisk totems (HarbingerTotemScarab2 = Ball Lightning nova, HarbingerTotemScarab4 = Warp Flame).
        foreach (uint obeliskId in h.Obelisks)
        {
            string? vid = null;
            lock (conn.Monsters)
                if (conn.Monsters.TryGetValue(obeliskId, out var om) && !om.Dead && om.Life > 0) vid = om.Variety?.Id;
            if (vid is not null)
                await DriveActorKit(conn, area, h, obeliskId, vid, level, seconds, cdScale, players, canWarp: false);
        }
    }

    static async Task DriveActorKit(ClientState conn, Area area, Encounter h, uint actorId, string varietyId,
        byte level, double seconds, double cdScale, (int X, int Y)[] players, bool canWarp)
    {
        var kit = HarbingerCatalog.KitFor(varietyId);
        if (kit.Skills.Count == 0) return;

        if (!area.ActorCooldowns.TryGetValue(actorId, out var cds))
            area.ActorCooldowns[actorId] = cds = [];

        var pos = ActorPosition(conn, actorId, (h.X, h.Y));
        var nearest = players.MinBy(p => Distance(p, pos));
        if (Distance(pos, nearest) > SkillEngageRange) return;

        // Decrement active cooldowns first; execute at most one cast per actor per tick.
        ActorSkill? ready = null;
        foreach (var skill in kit.Skills)
        {
            if (skill.Role is SkillRole.Summon or SkillRole.AllyDamageAura or SkillRole.PlayerCurseAura) continue;
            double rem = Math.Max(0, cds.GetValueOrDefault(skill.EffectRow, 0) - seconds);
            cds[skill.EffectRow] = rem;
            if (rem > 0 || ready is not null) continue;
            if (CanCastSkill(conn, h, skill, pos, nearest)) ready = skill;
        }
        if (ready is null || area.Busy(actorId)) return;

        area.ActorBusyUntil[actorId] = area.Time + ready.CastSeconds;
        cds[ready.EffectRow] = Math.Max(0.5, ready.CooldownSeconds) / cdScale;
        await ExecuteActorSkill(conn, area, h, actorId, ready, level, pos, nearest, canWarp);
    }

    static bool CanCastSkill(ClientState conn, Encounter h, ActorSkill skill, (int X, int Y) pos, (int X, int Y) nearest) =>
        skill.Role switch
        {
            SkillRole.MinionBuffWeak or SkillRole.MinionBuffStrong or SkillRole.AllyActionSpeedAura => h.Minions.Count > 0,
            SkillRole.Convocation => HasStrayedMinion(conn, h, pos, nearest),
            _ => true,
        };

    static bool HasStrayedMinion(ClientState conn, Encounter h, (int X, int Y) pos, (int X, int Y) nearest)
    {
        if (h.Minions.Count == 0) return false;
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
        {
            foreach (uint mid in h.Minions)
                if (conn.Monsters.TryGetValue(mid, out var mm) && !mm.Dead && mm.Life > 0)
                {
                    var mp = mm.Walk.Position(now);
                    if (Distance(mp, pos) > ConvocationPullDistance && Distance(mp, nearest) > ConvocationPullDistance)
                        return true;
                }
        }
        return false;
    }

    static async Task ExecuteActorSkill(ClientState conn, Area area, Encounter h, uint actorId, ActorSkill skill,
        byte level, (int X, int Y) pos, (int X, int Y) target, bool canWarp)
    {
        var catalog = SkillsFor(conn);
        var native = GolemData.Skill(catalog, skill.EffectRow, level);
        int Raw(string name) => Stats.Key(name) is uint k ? native.Stats.GetValueOrDefault(k) : 0;

        switch (skill.Role)
        {
            case SkillRole.WarpFlame:
            {
                (int X, int Y) dest = target;
                if (canWarp)
                {
                    var rng = Random.Shared;
                    double angle = rng.NextDouble() * Math.Tau;
                    double offset = WarpMinOffset + rng.NextDouble() * (WarpMaxOffset - WarpMinOffset);
                    dest = conn.Layout.Near(((int)Math.Round(h.X + offset * Math.Cos(angle)),
                        (int)Math.Round(h.Y + offset * Math.Sin(angle))));
                    if (dest == pos)
                        dest = conn.Layout.Near((h.X + (pos.X <= h.X ? 10 : -10), h.Y));
                }

                await SendActorSkillAction(conn, actorId, skill.SkillId, dest);
                double duration = Raw("base_skill_effect_duration") is int ms and > 0 ? ms / 1000.0 : 5.0;
                double dps = Raw("base_fire_damage_to_deal_per_minute") * native.Multiplier / 60.0;
                (int X, int Y) groundAt = canWarp ? pos : target;
                if (dps > 0)
                {
                    area.Hazards.Add(new()
                    {
                        At = groundAt,
                        Radius = 12,
                        Remaining = duration,
                        Dps = dps,
                        Type = DamageType.Fire,
                        SourceId = actorId,
                    });
                    area.HasHazards = true;
                }
                await SpawnEffect(conn, area, groundAt, BurningGroundAo, Math.Min(duration, 5.0));

                if (canWarp && dest != pos)
                {
                    await SpawnEffect(conn, area, pos, WarpMarkerAo, 0.8);
                    await TeleportActor(conn, actorId, dest);
                    await SpawnEffect(conn, area, dest, WarpMarkerAo, 0.8);
                }
                break;
            }

            case SkillRole.Convocation:
            {
                await SendActorSkillAction(conn, actorId, skill.SkillId, pos);
                int maxPull = Raw("convocation_max_number_of_minions_to_teleport") is int cap and > 0 ? cap : 10;
                var pull = new List<uint>(maxPull);
                double now = conn.Clock.Elapsed.TotalSeconds;
                lock (conn.Monsters)
                {
                    foreach (uint mid in h.Minions)
                    {
                        if (pull.Count >= maxPull) break;
                        if (conn.Monsters.TryGetValue(mid, out var mm) && !mm.Dead && mm.Life > 0
                            && Distance(mm.Walk.Position(now), pos) > ConvocationPullDistance)
                            pull.Add(mid);
                    }
                }
                await SpawnEffect(conn, area, pos, WarpMarkerAo, 0.8);
                for (int i = 0; i < pull.Count; i++)
                {
                    double a = Math.Tau * i / Math.Max(1, pull.Count);
                    var dest = conn.Layout.Near(((int)Math.Round(pos.X + 4 * Math.Cos(a)), (int)Math.Round(pos.Y + 4 * Math.Sin(a))));
                    await TeleportActor(conn, pull[i], dest);
                }
                break;
            }

            case SkillRole.MinionBuffWeak or SkillRole.MinionBuffStrong:
            {
                uint[] targets;
                lock (conn.Monsters)
                    targets = [.. h.Minions.Where(id => conn.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0).Take(6)];
                if (targets.Length == 0) return;

                var aim = ActorPosition(conn, targets[0], pos);
                await SendActorSkillAction(conn, actorId, skill.SkillId, aim, targets[0]);
                var buffStats = skill.Role == SkillRole.MinionBuffStrong ? StrongMinionBuffStats : WeakMinionBuffStats;
                string auraKey = $"harbinger:empower:{actorId}";
                area.ActiveTimedAuras[(actorId, auraKey)] = new(actorId, auraKey, MinionEmpowerBuffRow,
                    AllyAuraDefaultRadius * 2, MinionEmpowerDuration, buffStats, [.. targets]);
                break;
            }

            case SkillRole.AllyActionSpeedAura:
            {
                await SendActorSkillAction(conn, actorId, skill.SkillId, pos);
                double duration = Raw("base_skill_effect_duration") is int ms and > 0 ? ms / 1000.0 : 8.0;
                int speedStat = Raw("action_speed_-%");
                if (speedStat == 0) speedStat = -30;
                var stats = new Dictionary<string, int>(StringComparer.Ordinal) { ["action_speed_-%"] = speedStat };
                string auraKey = $"harbinger:speed:{actorId}";
                area.ActiveTimedAuras[(actorId, auraKey)] = new(actorId, auraKey, ActionSpeedAuraAtlasBuffRow,
                    AllyAuraDefaultRadius, duration, stats);
                break;
            }

            case SkillRole.FireMortar:
            {
                var attack = BuildSkillAttack(conn, skill, level);
                var (action, seq, monster) = await SendActorSkillAction(conn, actorId, skill.SkillId, target, PlayerId);
                if (monster is not null && attack.Projectile is { } proj)
                {
                    var w = conn.Wire;
                    LaunchMonsterProjectile(w.Remote, w.Stream, w.Ciphers, conn, actorId, monster, attack, proj, action,
                        orderedAim: target, sequence: seq);
                }
                // AvatarFireMortar also grants harbinger_mortar_buff (fire res + phys added as fire) to nearby allies.
                int fireRes = Raw("base_fire_damage_resistance_%");
                int addFire = Raw("physical_damage_%_to_add_as_fire");
                if (fireRes > 0 || addFire > 0)
                {
                    double dur = Raw("base_skill_effect_duration") is int ms and > 0 ? ms / 1000.0 : 5.0;
                    var buffStats = new Dictionary<string, int>(StringComparer.Ordinal);
                    if (fireRes > 0) buffStats["base_fire_damage_resistance_%"] = fireRes;
                    if (addFire > 0) buffStats["physical_damage_%_to_add_as_fire"] = addFire;
                    string auraKey = $"harbinger:mortar:{actorId}";
                    area.ActiveTimedAuras[(actorId, auraKey)] = new(actorId, auraKey, MortarBuffRow,
                        AllyAuraDefaultRadius, dur, buffStats);
                }
                break;
            }

            case SkillRole.BallLightning:
            {
                var attack = BuildSkillAttack(conn, skill, level);
                var (action, seq, monster) = await SendActorSkillAction(conn, actorId, skill.SkillId, target, PlayerId);
                if (monster is not null && attack.Projectile is { } proj)
                {
                    var w = conn.Wire;
                    LaunchMonsterProjectile(w.Remote, w.Stream, w.Ciphers, conn, actorId, monster, attack, proj, action,
                        orderedAim: target, sequence: seq);
                }
                break;
            }

            case SkillRole.BeamCannon:
            {
                var attack = BuildSkillAttack(conn, skill, level);
                await SendActorSkillAction(conn, actorId, skill.SkillId, target, PlayerId);
                await SpawnEffect(conn, area, target, BeamMarkerAo, 0.9);
                await SpawnEffect(conn, area, target, BeamCannonAo, 1.1);
                area.Hazards.Add(new()
                {
                    At = target,
                    Radius = 14,
                    Remaining = 1.0,
                    SourceId = actorId,
                    DelayedHit = attack,
                    HitDelay = 0.75,
                    ImpactAo = BeamNovaAo,
                });
                area.HasHazards = true;
                int coldRes = Raw("base_cold_damage_resistance_%");
                int addCold = Raw("physical_damage_%_to_add_as_cold");
                if (coldRes > 0 || addCold > 0)
                {
                    double dur = Raw("base_skill_effect_duration") is int ms and > 0 ? ms / 1000.0 : 5.0;
                    var buffStats = new Dictionary<string, int>(StringComparer.Ordinal);
                    if (coldRes > 0) buffStats["base_cold_damage_resistance_%"] = coldRes;
                    if (addCold > 0) buffStats["physical_damage_%_to_add_as_cold"] = addCold;
                    string auraKey = $"harbinger:cold:{actorId}";
                    area.ActiveTimedAuras[(actorId, auraKey)] = new(actorId, auraKey, ColdBeaconBuffRow,
                        AllyAuraDefaultRadius, dur, buffStats);
                }
                break;
            }

            case SkillRole.SkyBeam:
            {
                var attack = BuildSkillAttack(conn, skill, level);
                await SendActorSkillAction(conn, actorId, skill.SkillId, target, PlayerId);
                await SpawnEffect(conn, area, target, SkyBeamAo, 1.2);
                area.Hazards.Add(new()
                {
                    At = target,
                    Radius = 14,
                    Remaining = 1.1,
                    SourceId = actorId,
                    DelayedHit = attack,
                    HitDelay = 0.9,
                    ImpactAo = SkyBeamImpactAo,
                });
                area.HasHazards = true;
                break;
            }

            case SkillRole.ChargedBlast:
            {
                var attack = BuildSkillAttack(conn, skill, level);
                await SendActorSkillAction(conn, actorId, skill.SkillId, target, PlayerId);
                await SpawnEffect(conn, area, target, FlameblastMarkerAo, 1.2);
                area.Hazards.Add(new()
                {
                    At = target,
                    Radius = 16,
                    Remaining = 1.2,
                    SourceId = actorId,
                    DelayedHit = attack,
                    HitDelay = 1.0,
                    ImpactAo = FlameblastExplodeAo,
                });
                area.HasHazards = true;
                break;
            }

            case SkillRole.Upheaval:
            {
                var attack = BuildSkillAttack(conn, skill, level);
                await SendActorSkillAction(conn, actorId, skill.SkillId, target, PlayerId);
                await SpawnEffect(conn, area, target, UpheavalAo, 1.0);
                area.Hazards.Add(new()
                {
                    At = target,
                    Radius = 13,
                    Remaining = 0.6,
                    SourceId = actorId,
                    DelayedHit = attack,
                    HitDelay = 0.4,
                });
                area.HasHazards = true;
                break;
            }

            case SkillRole.RighteousFire:
            {
                await SendActorSkillAction(conn, actorId, skill.SkillId, pos);
                double duration = Raw("base_skill_effect_duration") is int ms and > 0 ? ms / 1000.0 : 4.0;
                double dps = Raw("base_fire_damage_to_deal_per_minute") * native.Multiplier / 60.0;
                var center = h.Minions.Count > 0 ? ActorPosition(conn, h.Minions[0], pos) : pos;
                await SpawnEffect(conn, area, center, RighteousFireAo, Math.Min(duration, 4.0));
                if (dps > 0)
                {
                    area.Hazards.Add(new()
                    {
                        At = center,
                        Radius = 16,
                        Remaining = duration,
                        Dps = dps,
                        Type = DamageType.Fire,
                        SourceId = actorId,
                    });
                    area.HasHazards = true;
                }
                break;
            }
        }
    }

    // As SendMonsterAction sends a scripted action: built at the send gate, ended before it starts (an actor whose last
    // action never ended plays no new one), and mirrored to the other players with their own target. One attempt, no
    // retry loop: this runs inside the player's tick, and a cast that misses its gate is cast again on its cooldown.
    static async Task<(int Action, ushort Sequence, SpawnedMonster? Monster)> SendActorSkillAction(
        ClientState conn, uint actorId, ushort skillId, (int X, int Y) target, uint targetObjectId = 0)
    {
        SpawnedMonster? monster;
        int action;
        lock (conn.Monsters)
        {
            if (!conn.Monsters.TryGetValue(actorId, out monster) || monster.Dead)
                return (0, 0, null);
            action = ++monster.ActionGeneration;
        }
        ushort seq = 0;
        bool sent = false;
        var w = conn.Wire;
        await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, actorId, [],
            $"0x01ab harbinger actor 0x{actorId:x} ({monster.Variety?.Id}) uses skill 0x{skillId:x} at ({target.X}, {target.Y})",
            atGate: () =>
            {
                using (ProjectileMutation(conn, phase: "send"))
                {
                    if (!MonsterActionCurrent(conn, actorId, monster, action) || conn.Paused) return null;
                    var from = monster.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                    seq = conn.NextActionSequence();
                    byte[] bytes = [.. InstanceProtocol.ActionEnded(actorId),
                        .. InstanceProtocol.ActionStarted(actorId, from.X, from.Y, target.X, target.Y, skillId, seq,
                            conn.MonsterOrderFlags, targetObjectId: targetObjectId == 0 ? null : targetObjectId)];
                    BroadcastMonsterMessage(conn, actorId, bytes);
                    sent = true;
                    return bytes;
                }
            });
        return sent ? (action, seq, monster) : (0, 0, null);
    }

    static async Task TeleportActor(ClientState conn, uint actorId, (int X, int Y) dest)
    {
        lock (conn.Monsters)
        {
            if (!conn.Monsters.TryGetValue(actorId, out var m) || m.Dead) return;
            m.Walk.Teleport(dest.X, dest.Y, conn.Clock.Elapsed.TotalSeconds);
        }
        byte[] packet = InstanceProtocol.Teleport(actorId, dest.X, dest.Y);
        foreach (var viewer in Members(conn))
        {
            bool visible;
            lock (viewer.Monsters) visible = viewer.MonsterViews.ContainsKey(actorId);
            if (!visible) continue;
            var vw = viewer.Wire;
            await SendWorldRecord(vw.Remote, vw.Stream, vw.Ciphers, viewer, actorId, [.. packet], null);
        }
    }

    static async Task TickHazards(ClientState conn, Area area, double dt)
    {
        if (area.Hazards.Count == 0) { area.HasHazards = false; return; }
        var members = Members(conn).Where(c => c.PlayerSent && c.PlayerLife > 0).ToArray();

        // Burning ground: the shared ground damage (the player's and their minions' defences, the degeneration death).
        GroundHazard[] burning = [.. area.Hazards.Where(h => h.Dps > 0 && h.Remaining > 0)];
        if (burning.Length > 0)
            foreach (var p in members)
            {
                var exposure = GroundExposure.GetValue(p, _ => new());
                double now = p.Clock.Elapsed.TotalSeconds;
                // Ground damages the time since its last call: a player who was last burnt long ago starts anew.
                if (now - exposure.Last > 1) exposure.Last = double.NaN;
                await ArenaHazards.Ground(p, exposure, now, (at, _) =>
                {
                    var raw = DamagePacket.Empty;
                    foreach (var h in burning)
                        if (Distance(at, h.At) <= h.Radius) raw = raw.Add(h.Type, new(h.Dps, h.Dps));
                    return raw;
                }, () => !conn.Closed && !conn.LeavingArea);
            }

        for (int i = area.Hazards.Count - 1; i >= 0; i--)
        {
            var hazard = area.Hazards[i];
            if (hazard.DelayedHit is { } hit && !hazard.HitFired)
            {
                hazard.HitDelay -= dt;
                if (hazard.HitDelay <= 0)
                {
                    hazard.HitFired = true;
                    if (hazard.ImpactAo is { } ao)
                        await SpawnEffect(conn, area, hazard.At, ao, 0.8);
                    SpawnedMonster? source = null;
                    lock (conn.Monsters) conn.Monsters.TryGetValue(hazard.SourceId, out source);
                    if (source is not null)
                    {
                        foreach (var player in members)
                            await ArenaHazards.Hit(player, hazard.SourceId, source, hit,
                                at => Distance(at, hazard.At) <= hazard.Radius, () => !conn.Closed && !conn.LeavingArea);
                    }
                }
            }

            hazard.Remaining -= dt;
            if (hazard.Remaining <= 0)
                area.Hazards.RemoveAt(i);
        }

        area.HasHazards = area.Hazards.Count > 0;
    }

    sealed record AllyAuraSource(uint SourceId, string Key, ushort BuffRow, (int X, int Y) Pos, double Radius,
        IReadOnlyDictionary<string, int> Stats, HashSet<uint>? Targets);

    static async Task SyncAllyAuras(ClientState conn, Area area, double dt)
    {
        // Expire timed ally buffs (minion empower, action speed aura, mortar/cold beacon).
        List<(uint SourceId, string AuraKey)>? expired = null;
        foreach (var (key, timed) in area.ActiveTimedAuras)
        {
            timed.Remaining -= dt;
            if (timed.Remaining <= 0) (expired ??= []).Add(key);
        }
        if (expired is not null)
            foreach (var key in expired) area.ActiveTimedAuras.Remove(key);

        // Gather all active ally aura sources from living Harbingers, helpers, obelisks, and active timed buffs.
        var sources = new List<AllyAuraSource>();
        byte level = AreaLevel(conn);
        double now = conn.Clock.Elapsed.TotalSeconds;
        var livingMinions = new List<(uint Id, (int X, int Y) Pos)>();

        lock (conn.Monsters)
        {
            foreach (var h in area.Encounters!)
            {
                if (h.State != HarbingerState.Active) continue;
                foreach (uint mid in h.Minions)
                    if (conn.Monsters.TryGetValue(mid, out var mm) && !mm.Dead && mm.Life > 0)
                        livingMinions.Add((mid, mm.Walk.Position(now)));

                // Harbinger + King helpers: passive elemental ally damage auras (AvatarAnger/Hatred/Wrath).
                AddPassiveAllyAurasLocked(conn, level, now, h.Monster, sources);
                foreach (uint helperId in h.Helpers)
                    AddPassiveAllyAurasLocked(conn, level, now, helperId, sources);

                // Empowered Obelisk auras (HarbingerTotemScarab3: DamageReduction -75%, ActionSpeed -20%).
                foreach (uint obeliskId in h.Obelisks)
                {
                    if (!conn.Monsters.TryGetValue(obeliskId, out var om) || om.Dead || om.Life == 0 || om.Variety is null) continue;
                    var kit = HarbingerCatalog.KitFor(om.Variety.Id);
                    if (kit.ObeliskAuras.Count == 0) continue;
                    var opos = om.Walk.Position(now);
                    foreach (var aura in kit.ObeliskAuras)
                        sources.Add(new(obeliskId, $"harbinger:obelisk:{obeliskId}:{aura.ModId}", aura.BuffRow,
                            opos, aura.Radius, aura.Stats, null));
                }
            }

            foreach (var (_, timed) in area.ActiveTimedAuras)
            {
                if (!conn.Monsters.TryGetValue(timed.SourceId, out var sm) || sm.Dead) continue;
                sources.Add(new(timed.SourceId, timed.AuraKey, timed.BuffRow, sm.Walk.Position(now),
                    timed.Radius, timed.Stats, timed.Targets));
            }
        }

        // Compute desired (MonsterId, AuraKey) applications.
        var desired = new Dictionary<(uint MonsterId, string AuraKey), AllyAuraSource>();
        foreach (var (mid, mpos) in livingMinions)
        {
            foreach (var src in sources)
            {
                if (src.Targets is not null && !src.Targets.Contains(mid)) continue;
                if (Distance(mpos, src.Pos) <= src.Radius)
                    desired[(mid, src.Key)] = src;
            }
        }

        // Remove expired/out-of-range/dead-source auras.
        var toRemove = new List<(uint MonsterId, string AuraKey)>();
        foreach (var key in area.AppliedAllyAuras.Keys)
            if (!desired.ContainsKey(key))
                toRemove.Add(key);
        foreach (var key in toRemove)
            await RemoveAppliedAllyAura(conn, area, key);

        // Apply new auras & refresh viewer buff visuals.
        var viewers = Members(conn).ToArray();
        foreach (var (key, src) in desired)
        {
            bool added;
            lock (conn.Monsters)
            {
                if (!conn.Monsters.TryGetValue(key.MonsterId, out var m) || m.Dead || m.Life == 0) continue;
                m.TemporaryStats[key.AuraKey] = src.Stats;
            }
            if (!area.AppliedAllyAuras.TryGetValue(key, out var applied))
            {
                applied = new(src.SourceId, src.BuffRow);
                area.AppliedAllyAuras[key] = applied;
                added = true;
            }
            else added = false;

            if (added || applied.ViewerBuffs.Count < viewers.Length)
            {
                foreach (var viewer in viewers)
                {
                    if (applied.ViewerBuffs.ContainsKey(viewer)) continue;
                    bool visible;
                    lock (viewer.Monsters) visible = viewer.MonsterViews.ContainsKey(key.MonsterId);
                    if (!visible) continue;
                    ushort buffId = viewer.NextBuff++;
                    applied.ViewerBuffs[viewer] = buffId;
                    var vw = viewer.Wire;
                    await SendWorldRecord(vw.Remote, vw.Stream, vw.Ciphers, viewer, key.MonsterId,
                        InstanceEntities.AddBuff(key.MonsterId, buffId, src.BuffRow, src.SourceId), null);
                }
            }
        }
    }

    static void AddPassiveAllyAurasLocked(ClientState conn, byte level, double now, uint actorId, List<AllyAuraSource> sources)
    {
        if (actorId == 0 || !conn.Monsters.TryGetValue(actorId, out var actor) || actor.Dead || actor.Variety is null) return;
        var kit = HarbingerCatalog.KitFor(actor.Variety.Id);
        foreach (var skill in kit.Skills)
        {
            if (skill.Role != SkillRole.AllyDamageAura) continue;
            var native = GolemData.Skill(SkillsFor(conn), skill.EffectRow, level);
            int Raw(string name) => Stats.Key(name) is uint k ? native.Stats.GetValueOrDefault(k) : 0;
            ushort buffRow;
            var statMap = new Dictionary<string, int>(StringComparer.Ordinal);
            switch (skill.EffectId)
            {
                case "AvatarHatred":
                    buffRow = HatredAuraBuffRow;
                    statMap["physical_damage_%_to_add_as_cold"] = Raw("physical_damage_%_to_add_as_cold") is int c and > 0 ? c : 30;
                    break;
                case "AvatarAnger":
                    buffRow = AngerAuraBuffRow;
                    statMap["attack_minimum_added_fire_damage"] = Math.Max(1, (int)Math.Round(Raw("attack_minimum_added_fire_damage") * native.Multiplier));
                    statMap["attack_maximum_added_fire_damage"] = Math.Max(1, (int)Math.Round(Raw("attack_maximum_added_fire_damage") * native.Multiplier));
                    statMap["physical_damage_%_to_add_as_fire"] = 25;
                    break;
                case "AvatarWrath":
                    buffRow = WrathAuraBuffRow;
                    statMap["attack_minimum_added_lightning_damage"] = Math.Max(1, (int)Math.Round(Raw("attack_minimum_added_lightning_damage") * native.Multiplier));
                    statMap["attack_maximum_added_lightning_damage"] = Math.Max(1, (int)Math.Round(Raw("attack_maximum_added_lightning_damage") * native.Multiplier));
                    statMap["physical_damage_%_to_add_as_lightning"] = 25;
                    break;
                default:
                    continue;
            }
            sources.Add(new(actorId, $"harbinger:aura:{actorId}:{skill.EffectId}", buffRow, actor.Walk.Position(now),
                AllyAuraDefaultRadius, statMap, null));
        }
    }

    static async Task RemoveAppliedAllyAura(ClientState conn, Area area, (uint MonsterId, string AuraKey) key)
    {
        if (!area.AppliedAllyAuras.Remove(key, out var applied)) return;
        lock (conn.Monsters)
            if (conn.Monsters.TryGetValue(key.MonsterId, out var m))
                m.TemporaryStats.Remove(key.AuraKey);
        foreach (var (viewer, buffId) in applied.ViewerBuffs)
        {
            if (viewer.Closed || viewer.LeavingArea) continue;
            bool visible;
            lock (viewer.Monsters) visible = viewer.MonsterViews.ContainsKey(key.MonsterId);
            if (!visible) continue;
            var vw = viewer.Wire;
            await SendWorldRecord(vw.Remote, vw.Stream, vw.Ciphers, viewer, key.MonsterId,
                InstanceEntities.RemoveBuff(key.MonsterId, buffId), null);
        }
    }

    static async Task ClearAurasFromSource(ClientState conn, Area area, uint sourceId)
    {
        if (sourceId == 0) return;
        List<(uint SourceId, string AuraKey)>? timedOwned = null;
        foreach (var key in area.ActiveTimedAuras.Keys)
            if (key.SourceId == sourceId)
                (timedOwned ??= []).Add(key);
        if (timedOwned is not null)
            foreach (var key in timedOwned)
                area.ActiveTimedAuras.Remove(key);
        List<(uint MonsterId, string AuraKey)>? owned = null;
        foreach (var (key, applied) in area.AppliedAllyAuras)
            if (applied.SourceId == sourceId)
                (owned ??= []).Add(key);
        if (owned is null) return;
        foreach (var key in owned)
            await RemoveAppliedAllyAura(conn, area, key);
    }

    static void ClearMinionAuraRecords(Area area, uint minionId)
    {
        List<(uint MonsterId, string AuraKey)>? stale = null;
        foreach (var key in area.AppliedAllyAuras.Keys)
            if (key.MonsterId == minionId)
                (stale ??= []).Add(key);
        if (stale is not null)
            foreach (var key in stale)
                area.AppliedAllyAuras.Remove(key);
    }

    static async Task SyncPlayerCurses(ClientState conn, Area area, ClientState[] members, (int X, int Y)[] positions)
    {
        // Gather curse aura emitters on active Harbingers and King helpers.
        List<(uint SourceId, string EffectId, ushort BuffRow, (int X, int Y) Pos, Dictionary<uint, int> Stats)>? emitters = null;
        double now = conn.Clock.Elapsed.TotalSeconds;
        byte level = AreaLevel(conn);
        lock (conn.Monsters)
        {
            foreach (var h in area.Encounters!)
            {
                if (h.State != HarbingerState.Active) continue;
                CollectCurseEmittersLocked(conn, level, now, h.Monster, ref emitters);
                foreach (uint helperId in h.Helpers)
                    CollectCurseEmittersLocked(conn, level, now, helperId, ref emitters);
            }
        }

        for (int i = 0; i < members.Length; i++)
        {
            var player = members[i];
            var ppos = positions[i];
            Dictionary<string, (ushort BuffRow, uint SourceId, Dictionary<uint, int> Stats)>? active = null;
            if (emitters is not null)
            {
                foreach (var e in emitters)
                {
                    if (Distance(ppos, e.Pos) <= CurseAuraDefaultRadius)
                        (active ??= new(StringComparer.Ordinal))[e.EffectId] = (e.BuffRow, e.SourceId, e.Stats);
                }
            }

            if (active is not null && CurseImmune(player)) active = null;
            if (active is null)
            {
                if (ActivePlayerCurses.TryGetValue(player, out var existing) && (existing.Stats.Count > 0 || existing.Buffs.Count > 0))
                    await ClearPlayerCursesAsync(player);
                continue;
            }

            var state = ActivePlayerCurses.GetValue(player, _ => new());
            bool statsChanged = false;
            var nextStats = new Dictionary<uint, int>();
            foreach (var (_, (_, _, es)) in active)
                foreach (var (k, v) in es)
                    nextStats[k] = nextStats.GetValueOrDefault(k) + v;

            if (nextStats.Count != state.Stats.Count || nextStats.Any(kv => state.Stats.GetValueOrDefault(kv.Key) != kv.Value))
            {
                state.Stats.Clear();
                foreach (var (k, v) in nextStats) state.Stats[k] = v;
                statsChanged = true;
            }

            var pw = player.Wire;
            foreach (string gone in state.Buffs.Keys.Where(k => !active.ContainsKey(k)).ToArray())
            {
                ushort bid = state.Buffs[gone];
                state.Buffs.Remove(gone);
                await SendWorldRecord(pw.Remote, pw.Stream, pw.Ciphers, player, PlayerId,
                    InstanceEntities.RemoveBuff(PlayerId, bid), null);
            }
            foreach (var (eid, (buffRow, srcId, _)) in active)
            {
                if (state.Buffs.ContainsKey(eid)) continue;
                ushort bid = player.NextBuff++;
                state.Buffs[eid] = bid;
                await SendWorldRecord(pw.Remote, pw.Stream, pw.Ciphers, player, PlayerId,
                    InstanceEntities.AddBuff(PlayerId, bid, buffRow, srcId), null);
            }
            if (statsChanged)
            {
                lock (player.Monsters) { player.Pools = PoolsFor(player, player.Pools.Level); player.ApplyMovementSpeed(); }
                await SendPlayerStatSnapshot(pw.Remote, pw.Stream, pw.Ciphers, player,
                    () => PlayerStatsUpdateAtGate(player, PlayerStatsWithCleared(player)), null);
            }
        }
    }

    static bool CurseImmune(ClientState player)
    {
        if (player.Immune || InGracePeriod(player)) return true;
        var stats = CharacterPlayerStats(player);
        int Has(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return Has("immune_to_curses") != 0 || Has("virtual_immune_to_curses") != 0 || Has("hexproof") != 0;
    }

    static void CollectCurseEmittersLocked(ClientState conn, byte level, double now, uint actorId,
        ref List<(uint SourceId, string EffectId, ushort BuffRow, (int X, int Y) Pos, Dictionary<uint, int> Stats)>? emitters)
    {
        if (actorId == 0 || !conn.Monsters.TryGetValue(actorId, out var actor) || actor.Dead || actor.Variety is null) return;
        var kit = HarbingerCatalog.KitFor(actor.Variety.Id);
        foreach (var skill in kit.Skills)
        {
            if (skill.Role != SkillRole.PlayerCurseAura) continue;
            var native = GolemData.Skill(SkillsFor(conn), skill.EffectRow, level);
            int Raw(string name) => Stats.Key(name) is uint k ? native.Stats.GetValueOrDefault(k) : 0;
            var curseStats = new Dictionary<uint, int>();
            void Put(string stat, int val) { if (val != 0 && Stats.Key(stat) is uint k) curseStats[k] = val; }

            ushort buffRow;
            switch (skill.EffectId)
            {
                case "AvatarEnfeebleAura":
                    buffRow = EnfeebleBuffRow;
                    Put("accuracy_rating_+%", Raw("accuracy_rating_+%"));
                    Put("enfeeble_damage_+%_final", Raw("enfeeble_damage_+%_final"));
                    Put("you_are_cursed_with_enfeeble", 1);
                    break;
                case "AvatarTemporalChainsAura":
                    buffRow = TemporalChainsBuffRow;
                    // curse_effect_+%_final_vs_players = -50 halves temporal_chains_action_speed_+%_final (-20 -> -10).
                    int tcBase = Raw("temporal_chains_action_speed_+%_final");
                    int vsPlayer = Raw("curse_effect_+%_final_vs_players");
                    int tcEffective = (int)Math.Round(tcBase * Math.Max(0, 1.0 + vsPlayer / 100.0));
                    Put("temporal_chains_action_speed_+%_final", tcEffective);
                    Put("you_are_cursed_with_temporal_chains", 1);
                    break;
                case "AvatarAssassinsMarkAura":
                    buffRow = AssassinsMarkBuffRow;
                    Put("enemy_additional_critical_strike_chance_against_self", Raw("enemy_additional_critical_strike_chance_against_self"));
                    Put("base_self_critical_strike_multiplier_-%", Raw("base_self_critical_strike_multiplier_-%"));
                    break;
                default:
                    continue;
            }
            (emitters ??= []).Add((actorId, skill.EffectId, buffRow, actor.Walk.Position(now), curseStats));
        }
    }

    static async Task ClearAllPlayerCurses(ClientState conn)
    {
        foreach (var member in Members(conn))
            if (ActivePlayerCurses.TryGetValue(member, out var s) && (s.Stats.Count > 0 || s.Buffs.Count > 0))
                await ClearPlayerCursesAsync(member);
    }

    static async Task ClearPlayerCursesAsync(ClientState player)
    {
        if (!ActivePlayerCurses.TryGetValue(player, out var state)) return;
        ushort[] buffs = [.. state.Buffs.Values];
        bool hadStats = state.Stats.Count > 0;
        state.Stats.Clear();
        state.Buffs.Clear();
        ActivePlayerCurses.Remove(player);
        if (player.Closed || player.LeavingArea) return;
        var pw = player.Wire;
        foreach (ushort bid in buffs)
            await SendWorldRecord(pw.Remote, pw.Stream, pw.Ciphers, player, PlayerId,
                InstanceEntities.RemoveBuff(PlayerId, bid), null);
        if (hadStats)
        {
            lock (player.Monsters) { player.Pools = PoolsFor(player, player.Pools.Level); player.ApplyMovementSpeed(); }
            await SendPlayerStatSnapshot(pw.Remote, pw.Stream, pw.Ciphers, player,
                () => PlayerStatsUpdateAtGate(player, PlayerStatsWithCleared(player)), null);
        }
    }

    static void ClearPlayerCursesSync(ClientState player)
    {
        if (!ActivePlayerCurses.TryGetValue(player, out var state)) return;
        state.Stats.Clear();
        state.Buffs.Clear();
        ActivePlayerCurses.Remove(player);
    }
}
