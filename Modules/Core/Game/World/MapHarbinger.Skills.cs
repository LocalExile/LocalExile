using GameServer.Core;
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

    static readonly IReadOnlyDictionary<uint, int> WeakMinionBuffStats =
        new Dictionary<uint, int> { [Stat.DamagePlusPct] = 30, [Stat.BaseActorScalePlusPct] = 15 };
    static readonly IReadOnlyDictionary<uint, int> StrongMinionBuffStats =
        new Dictionary<uint, int> { [Stat.DamagePlusPct] = 60, [Stat.BaseActorScalePlusPct] = 25 };

    sealed class ActiveTimedBuff(uint sourceId, string auraKey, ushort buffRow, double radius, double remaining,
        IReadOnlyDictionary<uint, int> stats, HashSet<uint>? targets = null)
    {
        public uint SourceId { get; } = sourceId;
        public string AuraKey { get; } = auraKey;
        public ushort BuffRow { get; } = buffRow;
        public double Radius { get; } = radius;
        public double Remaining { get; set; } = remaining;
        public IReadOnlyDictionary<uint, int> Stats { get; } = stats;
        public HashSet<uint>? Targets { get; } = targets;
    }

    sealed class AppliedAllyAura(uint sourceId, ushort buffRow)
    {
        public uint SourceId { get; } = sourceId;
        public ushort BuffRow { get; } = buffRow;
        public readonly Dictionary<Player, ushort> ViewerBuffs = [];
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
        public readonly Dictionary<string, object> Holders = new(StringComparer.Ordinal);
    }

    static readonly Store<Player, ArenaHazards.Exposure> GroundExposure = new();

    // O(1) zero-alloc lookup when no player stands inside a Harbinger curse aura.
    static readonly Store<Player, PlayerCurseState> ActivePlayerCurses = new();

    /// <summary>GameEvents.PlayerStatSources: active Harbinger curse aura stats on this player (zero-alloc early-out when inactive).</summary>
    public static Dictionary<uint, int> PlayerStats(Player player, Dictionary<uint, int> stats)
    {
        if (!ActivePlayerCurses.TryGetValue(player, out var curse) || curse.Stats.Count == 0) return stats;
        foreach (var (key, value) in curse.Stats)
            stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return stats;
    }

    static IReadOnlyList<GrantedSkill>? BuildOwnedSkills(Player player, MonsterCatalog.Variety variety, byte level)
    {
        var kit = HarbingerCatalog.KitFor(variety.Id);
        if (kit.Skills.Count == 0) return null;
        var catalog = SkillsFor(player);
        var granted = new List<GrantedSkill>(kit.Skills.Count);
        foreach (var skill in kit.Skills)
        {
            int effLevel = catalog.MaxLevelAt(skill.EffectRow, level);
            var attack = BuildSkillAttack(player, skill, level);
            var stats = attack.Projectile is not null
                ? MonsterActionSkillStats(player, skill.EffectRow, effLevel, attack)
                : WithBehaviourVariation(player, skill.EffectRow, attack.InPlaceSkillStats());
            if (catalog.FindByRow(skill.SkillId, skill.EffectRow, effLevel, stats) is { } gs)
                granted.Add(gs);
        }
        return granted.Count > 0 ? granted : null;
    }

    static MonsterAttack BuildSkillAttack(Player player, ActorSkill skill, byte level)
    {
        var catalog = SkillsFor(player);
        var native = GolemData.Skill(catalog, skill.EffectRow, level);
        int Raw(uint k) => native.Stats.GetValueOrDefault(k);
        int RawName(string name) => Stats.Key(name) is uint k ? native.Stats.GetValueOrDefault(k) : 0;

        var damage = DamagePacket.Empty;
        foreach (var type in DamageTypes.ConversionOrder)
        {
            string slug = type.ToString().ToLowerInvariant();
            int min = RawName($"spell_minimum_base_{slug}_damage");
            int max = RawName($"spell_maximum_base_{slug}_damage");
            if (max > 0) damage = damage.Add(type, new(min, Math.Max(min, max)));
        }
        damage = damage.Scale(native.Multiplier);

        // HarbingerUpheaval converts 100% physical spell damage to lightning (skill_physical_damage_%_to_convert_to_lightning).
        if (skill.Role == SkillRole.Upheaval && Raw(Stat.SkillPhysicalDamagePctToConvertToLightning) is int pct and > 0
            && damage[DamageType.Physical].Max > 0)
        {
            double frac = Math.Clamp(pct / 100.0, 0, 1);
            var phys = damage[DamageType.Physical];
            damage = damage
                .Add(DamageType.Lightning, phys.Scale(frac))
                .Add(DamageType.Physical, new(-phys.Min * frac, -phys.Max * frac));
        }

        // AttachedVaalChargedBlast2 scales with stage count (charged_blast_spell_damage_+%_final_per_stack = 40; 3 stages).
        if (skill.Role == SkillRole.ChargedBlast && Raw(Stat.ChargedBlastSpellDamagePlusPctFinalPerStack) is int perStack and > 0)
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

    static SkillCatalog.MonsterProjectileInfo BuildFireMortarProjectile(Func<uint, int> raw)
    {
        int count = 1 + Math.Max(0, raw(Stat.NumberOfAdditionalProjectiles));
        return new(FireMortarProjectile, FireMortarSpeed, Lobbed: true, CollisionRadius: 2)
        {
            Count = count,
            SpreadRadians = count > 1 ? 0.45 : 0,
            AreaRadius = 10,
        };
    }

    static SkillCatalog.MonsterProjectileInfo BuildBallLightningProjectile(Func<uint, int> raw)
    {
        int count = 1 + Math.Max(0, raw(Stat.NumberOfAdditionalProjectiles));
        int speedPct = raw(Stat.BaseProjectileSpeedPlusPct);
        int speed = Math.Max(60, (int)Math.Round(BallLightningSpeed * (1.0 + speedPct / 100.0)));
        return new(BallLightningProjectile, speed, Lobbed: false, CollisionRadius: 3)
        {
            Count = count,
            SpreadRadians = count > 1 ? Math.Tau : 0,
        };
    }

    static void SeedEncounterCooldowns(Player player, Area area, Encounter h, double cdScale)
    {
        SeedActorCooldowns(area, h.Monster, h.Variety, 1.0 / cdScale);
        foreach (uint helperId in h.Helpers)
        {
            if (player.Area.Monsters.TryGetValue(helperId, out var hm) && hm.Variety?.Id is { } vid)
                SeedActorCooldowns(area, helperId, vid, 0.5 / cdScale);
        }
        foreach (uint obeliskId in h.Obelisks)
        {
            if (player.Area.Monsters.TryGetValue(obeliskId, out var om) && om.Variety?.Id is { } vid)
                SeedActorCooldowns(area, obeliskId, vid, 1.0 / cdScale);
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

    static (int X, int Y) ActorPosition(Player player, uint actorId, (int X, int Y) fallback) =>
        player.Area.Monsters.TryGetValue(actorId, out var m) && !m.Dead ? m.Walk.Position(AreaTime(player)) : fallback;

    static async Task DriveEncounterSkills(Player player, Area area, Encounter h, byte level,
        double seconds, double cdScale, (int X, int Y)[] players)
    {
        if (h.State != HarbingerState.Active) return;

        // 1. Harbinger avatar's own skills.
        await DriveActorKit(player, area, h, h.Monster, h.Variety, level, seconds, cdScale, players, canWarp: true);

        // 2. King Harbinger's helper avatars (AvatarAtlasMinion1..6).
        foreach (uint helperId in h.Helpers)
        {
            if (player.Area.Monsters.TryGetValue(helperId, out var hm) && !hm.Dead && hm.Variety?.Id is { } vid)
                await DriveActorKit(player, area, h, helperId, vid, level, seconds, cdScale, players, canWarp: false);
        }

        // 3. Empowered Obelisk totems (HarbingerTotemScarab2 = Ball Lightning nova, HarbingerTotemScarab4 = Warp Flame).
        foreach (uint obeliskId in h.Obelisks)
        {
            if (player.Area.Monsters.TryGetValue(obeliskId, out var om) && !om.Dead && om.Life > 0 && om.Variety?.Id is { } vid)
                await DriveActorKit(player, area, h, obeliskId, vid, level, seconds, cdScale, players, canWarp: false);
        }
    }

    static async Task DriveActorKit(Player player, Area area, Encounter h, uint actorId, string varietyId,
        byte level, double seconds, double cdScale, (int X, int Y)[] players, bool canWarp)
    {
        var kit = HarbingerCatalog.KitFor(varietyId);
        if (kit.Skills.Count == 0) return;

        if (!area.ActorCooldowns.TryGetValue(actorId, out var cds))
            area.ActorCooldowns[actorId] = cds = [];

        var pos = ActorPosition(player, actorId, (h.X, h.Y));
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
            if (CanCastSkill(player, h, skill, pos, nearest)) ready = skill;
        }
        if (ready is null || area.Busy(actorId)) return;

        area.ActorBusyUntil[actorId] = area.Time + ready.CastSeconds;
        cds[ready.EffectRow] = Math.Max(0.5, ready.CooldownSeconds) / cdScale;
        await ExecuteActorSkill(player, area, h, actorId, ready, level, pos, nearest, canWarp);
    }

    static bool CanCastSkill(Player player, Encounter h, ActorSkill skill, (int X, int Y) pos, (int X, int Y) nearest) =>
        skill.Role switch
        {
            SkillRole.MinionBuffWeak or SkillRole.MinionBuffStrong or SkillRole.AllyActionSpeedAura => h.Minions.Count > 0,
            SkillRole.Convocation => HasStrayedMinion(player, h, pos, nearest),
            _ => true,
        };

    static bool HasStrayedMinion(Player player, Encounter h, (int X, int Y) pos, (int X, int Y) nearest)
    {
        if (h.Minions.Count == 0) return false;
        double now = AreaTime(player);
        foreach (uint mid in h.Minions)
            if (player.Area.Monsters.TryGetValue(mid, out var mm) && !mm.Dead && mm.Life > 0)
            {
                var mp = mm.Walk.Position(now);
                if (Distance(mp, pos) > ConvocationPullDistance && Distance(mp, nearest) > ConvocationPullDistance)
                    return true;
            }
        return false;
    }

    static async Task ExecuteActorSkill(Player player, Area area, Encounter h, uint actorId, ActorSkill skill,
        byte level, (int X, int Y) pos, (int X, int Y) target, bool canWarp)
    {
        var catalog = SkillsFor(player);
        var native = GolemData.Skill(catalog, skill.EffectRow, level);
        int Raw(uint k) => native.Stats.GetValueOrDefault(k);

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
                    dest = player.Layout.Near(((int)Math.Round(h.X + offset * Math.Cos(angle)),
                        (int)Math.Round(h.Y + offset * Math.Sin(angle))));
                    if (dest == pos)
                        dest = player.Layout.Near((h.X + (pos.X <= h.X ? 10 : -10), h.Y));
                }

                await SendActorSkillAction(player, actorId, skill.SkillId, dest);
                double duration = Raw(Stat.BaseSkillEffectDuration) is int ms and > 0 ? ms / 1000.0 : 5.0;
                double dps = Raw(Stat.BaseFireDamageToDealPerMinute) * native.Multiplier / 60.0;
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
                await SpawnEffect(player, area, groundAt, BurningGroundAo, Math.Min(duration, 5.0));

                if (canWarp && dest != pos)
                {
                    await SpawnEffect(player, area, pos, WarpMarkerAo, 0.8);
                    await TeleportActor(player, actorId, dest);
                    await SpawnEffect(player, area, dest, WarpMarkerAo, 0.8);
                }
                break;
            }

            case SkillRole.Convocation:
            {
                await SendActorSkillAction(player, actorId, skill.SkillId, pos);
                int maxPull = Raw(Stat.ConvocationMaxNumberOfMinionsToTeleport) is int cap and > 0 ? cap : 10;
                var pull = new List<uint>(maxPull);
                double now = AreaTime(player);
                foreach (uint mid in h.Minions)
                {
                    if (pull.Count >= maxPull) break;
                    if (player.Area.Monsters.TryGetValue(mid, out var mm) && !mm.Dead && mm.Life > 0
                        && Distance(mm.Walk.Position(now), pos) > ConvocationPullDistance)
                        pull.Add(mid);
                }
                await SpawnEffect(player, area, pos, WarpMarkerAo, 0.8);
                for (int i = 0; i < pull.Count; i++)
                {
                    double a = Math.Tau * i / Math.Max(1, pull.Count);
                    var dest = player.Layout.Near(((int)Math.Round(pos.X + 4 * Math.Cos(a)), (int)Math.Round(pos.Y + 4 * Math.Sin(a))));
                    await TeleportActor(player, pull[i], dest);
                }
                break;
            }

            case SkillRole.MinionBuffWeak or SkillRole.MinionBuffStrong:
            {
                uint[] targets = [.. h.Minions.Where(id => player.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0).Take(6)];
                if (targets.Length == 0) return;

                var aim = ActorPosition(player, targets[0], pos);
                await SendActorSkillAction(player, actorId, skill.SkillId, aim, targets[0]);
                var buffStats = skill.Role == SkillRole.MinionBuffStrong ? StrongMinionBuffStats : WeakMinionBuffStats;
                string auraKey = $"harbinger:empower:{actorId}";
                area.ActiveTimedAuras[(actorId, auraKey)] = new(actorId, auraKey, MinionEmpowerBuffRow,
                    AllyAuraDefaultRadius * 2, MinionEmpowerDuration, buffStats, [.. targets]);
                break;
            }

            case SkillRole.AllyActionSpeedAura:
            {
                await SendActorSkillAction(player, actorId, skill.SkillId, pos);
                double duration = Raw(Stat.BaseSkillEffectDuration) is int ms and > 0 ? ms / 1000.0 : 8.0;
                int speedStat = Raw(Stat.ActionSpeedMinusPct);
                if (speedStat == 0) speedStat = -30;
                var stats = new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = speedStat };
                string auraKey = $"harbinger:speed:{actorId}";
                area.ActiveTimedAuras[(actorId, auraKey)] = new(actorId, auraKey, ActionSpeedAuraAtlasBuffRow,
                    AllyAuraDefaultRadius, duration, stats);
                break;
            }

            case SkillRole.FireMortar:
            {
                var attack = BuildSkillAttack(player, skill, level);
                var (action, seq, monster) = await SendActorSkillAction(player, actorId, skill.SkillId, target, player.Id);
                if (monster is not null && attack.Projectile is { } proj)
                {
                    LaunchMonsterProjectile(player, actorId, monster, attack, proj, action,
                        orderedAim: target, sequence: seq);
                }
                // AvatarFireMortar also grants harbinger_mortar_buff (fire res + phys added as fire) to nearby allies.
                int fireRes = Raw(Stat.BaseFireDamageResistancePct);
                int addFire = Raw(Stat.PhysicalDamagePctToAddAsFire);
                if (fireRes > 0 || addFire > 0)
                {
                    double dur = Raw(Stat.BaseSkillEffectDuration) is int ms and > 0 ? ms / 1000.0 : 5.0;
                    var buffStats = new Dictionary<uint, int>();
                    if (fireRes > 0) buffStats[Stat.BaseFireDamageResistancePct] = fireRes;
                    if (addFire > 0) buffStats[Stat.PhysicalDamagePctToAddAsFire] = addFire;
                    string auraKey = $"harbinger:mortar:{actorId}";
                    area.ActiveTimedAuras[(actorId, auraKey)] = new(actorId, auraKey, MortarBuffRow,
                        AllyAuraDefaultRadius, dur, buffStats);
                }
                break;
            }

            case SkillRole.BallLightning:
            {
                var attack = BuildSkillAttack(player, skill, level);
                var (action, seq, monster) = await SendActorSkillAction(player, actorId, skill.SkillId, target, player.Id);
                if (monster is not null && attack.Projectile is { } proj)
                {
                    LaunchMonsterProjectile(player, actorId, monster, attack, proj, action,
                        orderedAim: target, sequence: seq);
                }
                break;
            }

            case SkillRole.BeamCannon:
            {
                var attack = BuildSkillAttack(player, skill, level);
                await SendActorSkillAction(player, actorId, skill.SkillId, target, player.Id);
                await SpawnEffect(player, area, target, BeamMarkerAo, 0.9);
                await SpawnEffect(player, area, target, BeamCannonAo, 1.1);
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
                int coldRes = Raw(Stat.BaseColdDamageResistancePct);
                int addCold = Raw(Stat.PhysicalDamagePctToAddAsCold);
                if (coldRes > 0 || addCold > 0)
                {
                    double dur = Raw(Stat.BaseSkillEffectDuration) is int ms and > 0 ? ms / 1000.0 : 5.0;
                    var buffStats = new Dictionary<uint, int>();
                    if (coldRes > 0) buffStats[Stat.BaseColdDamageResistancePct] = coldRes;
                    if (addCold > 0) buffStats[Stat.PhysicalDamagePctToAddAsCold] = addCold;
                    string auraKey = $"harbinger:cold:{actorId}";
                    area.ActiveTimedAuras[(actorId, auraKey)] = new(actorId, auraKey, ColdBeaconBuffRow,
                        AllyAuraDefaultRadius, dur, buffStats);
                }
                break;
            }

            case SkillRole.SkyBeam:
            {
                var attack = BuildSkillAttack(player, skill, level);
                await SendActorSkillAction(player, actorId, skill.SkillId, target, player.Id);
                await SpawnEffect(player, area, target, SkyBeamAo, 1.2);
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
                var attack = BuildSkillAttack(player, skill, level);
                await SendActorSkillAction(player, actorId, skill.SkillId, target, player.Id);
                await SpawnEffect(player, area, target, FlameblastMarkerAo, 1.2);
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
                var attack = BuildSkillAttack(player, skill, level);
                await SendActorSkillAction(player, actorId, skill.SkillId, target, player.Id);
                await SpawnEffect(player, area, target, UpheavalAo, 1.0);
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
                await SendActorSkillAction(player, actorId, skill.SkillId, pos);
                double duration = Raw(Stat.BaseSkillEffectDuration) is int ms and > 0 ? ms / 1000.0 : 4.0;
                double dps = Raw(Stat.BaseFireDamageToDealPerMinute) * native.Multiplier / 60.0;
                var center = h.Minions.Count > 0 ? ActorPosition(player, h.Minions[0], pos) : pos;
                await SpawnEffect(player, area, center, RighteousFireAo, Math.Min(duration, 4.0));
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
    // action never ended plays no new one), and mirrored to the other players via the area delta. One attempt, no
    // retry loop: this runs inside the player's tick, and a cast that misses its gate is cast again on its cooldown.
    static async Task<(int Action, ushort Sequence, SpawnedMonster? Monster)> SendActorSkillAction(
        Player player, uint actorId, ushort skillId, (int X, int Y) target, uint targetObjectId = 0)
    {
        if (!player.Area.Monsters.TryGetValue(actorId, out var monster) || monster.Dead)
            return (0, 0, null);
        int action = ++monster.ActionGeneration;
        ushort seq = 0;
        bool sent = false;
        await SendWorldRecord(player, actorId, [],
            $"0x01ab harbinger actor 0x{actorId:x} ({monster.Variety?.Id}) uses skill 0x{skillId:x} at ({target.X}, {target.Y})",
            atGate: () =>
            {
                using (ProjectileMutation(player, phase: "send"))
                {
                    if (!MonsterActionCurrent(player, actorId, monster, action) || player.Paused) return null;
                    var from = monster.Walk.Position(AreaTime(player));
                    seq = player.NextActionSequence();
                    byte[] bytes = [.. InstanceProtocol.ActionEnded(actorId),
                        .. InstanceProtocol.ActionStarted(actorId, from.X, from.Y, target.X, target.Y, skillId, seq,
                            player.MonsterOrderFlags, targetObjectId: targetObjectId == 0 ? null : targetObjectId)];
                    sent = true;
                    return bytes;
                }
            });
        return sent ? (action, seq, monster) : (0, 0, null);
    }

    static async Task TeleportActor(Player player, uint actorId, (int X, int Y) dest)
    {
        if (!player.Area.Monsters.TryGetValue(actorId, out var m) || m.Dead) return;
        m.Walk.Teleport(dest.X, dest.Y, AreaTime(player));
        byte[] packet = InstanceProtocol.Teleport(actorId, dest.X, dest.Y);
        foreach (var viewer in Members(player))
        {
            if (!MonsterShown(viewer, actorId)) continue;
            await SendWorldRecord(viewer, actorId, [.. packet], null);
        }
    }

    static async Task TickHazards(Player player, Area area, double dt)
    {
        if (area.Hazards.Count == 0) { area.HasHazards = false; return; }
        var members = Members(player).Where(c => c.Connection.PlayerSent && c.PlayerLife > 0).ToArray();

        // Burning ground: the shared ground damage (the player's and their minions' defences, the degeneration death).
        GroundHazard[] burning = [.. area.Hazards.Where(h => h.Dps > 0 && h.Remaining > 0)];
        if (burning.Length > 0)
            foreach (var p in members)
            {
                var exposure = GroundExposure.GetValue(p, _ => new());
                double now = AreaTime(p);
                // Ground damages the time since its last call: a player who was last burnt long ago starts anew.
                if (now - exposure.Last > 1) exposure.Last = double.NaN;
                bool unaffected = UnaffectedByGround(p).Burning;
                await ArenaHazards.Ground(p, exposure, now, (at, minion) =>
                {
                    var raw = DamagePacket.Empty;
                    foreach (var h in burning)
                        if (Distance(at, h.At) <= h.Radius && !(unaffected && !minion && h.Type == DamageType.Fire))
                            raw = raw.Add(h.Type, new(h.Dps, h.Dps));
                    return raw;
                }, () => !player.Closed && !player.LeavingArea);
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
                        await SpawnEffect(player, area, hazard.At, ao, 0.8);
                    player.Area.Monsters.TryGetValue(hazard.SourceId, out var source);
                    if (source is not null)
                    {
                        foreach (var member in members)
                            await ArenaHazards.Hit(member, hazard.SourceId, source, hit,
                                at => Distance(at, hazard.At) <= hazard.Radius, () => !player.Closed && !player.LeavingArea);
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
        IReadOnlyDictionary<uint, int> Stats, HashSet<uint>? Targets);

    static async Task SyncAllyAuras(Player player, Area area, double dt)
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
        byte level = AreaLevel(player);
        double now = AreaTime(player);
        var livingMinions = new List<(uint Id, (int X, int Y) Pos)>();

        foreach (var h in area.Encounters!)
        {
            if (h.State != HarbingerState.Active) continue;
            foreach (uint mid in h.Minions)
                if (player.Area.Monsters.TryGetValue(mid, out var mm) && !mm.Dead && mm.Life > 0)
                    livingMinions.Add((mid, mm.Walk.Position(now)));

            // Harbinger + King helpers: passive elemental ally damage auras (AvatarAnger/Hatred/Wrath).
            AddPassiveAllyAuras(player, level, now, h.Monster, sources);
            foreach (uint helperId in h.Helpers)
                AddPassiveAllyAuras(player, level, now, helperId, sources);

            // Empowered Obelisk auras (HarbingerTotemScarab3: DamageReduction -75%, ActionSpeed -20%).
            foreach (uint obeliskId in h.Obelisks)
            {
                if (!player.Area.Monsters.TryGetValue(obeliskId, out var om) || om.Dead || om.Life == 0 || om.Variety is null) continue;
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
            if (!player.Area.Monsters.TryGetValue(timed.SourceId, out var sm) || sm.Dead) continue;
            sources.Add(new(timed.SourceId, timed.AuraKey, timed.BuffRow, sm.Walk.Position(now),
                timed.Radius, timed.Stats, timed.Targets));
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
            await RemoveAppliedAllyAura(player, area, key);

        // Apply new auras & refresh viewer buff visuals.
        var viewers = Members(player).ToArray();
        foreach (var (key, src) in desired)
        {
            if (!player.Area.Monsters.TryGetValue(key.MonsterId, out var m) || m.Dead || m.Life == 0) continue;
            m.SetTemporaryStats(key.AuraKey, src.Stats);
            bool added;
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
                    if (!MonsterShown(viewer, key.MonsterId)) continue;
                    ushort buffId = viewer.NextBuff++;
                    applied.ViewerBuffs[viewer] = buffId;
                    await SendWorldRecord(viewer, key.MonsterId,
                        InstanceEntities.AddBuff(key.MonsterId, buffId, src.BuffRow, src.SourceId), null);
                }
            }
        }
    }

    static void AddPassiveAllyAuras(Player player, byte level, double now, uint actorId, List<AllyAuraSource> sources)
    {
        if (actorId == 0 || !player.Area.Monsters.TryGetValue(actorId, out var actor) || actor.Dead || actor.Variety is null) return;
        var kit = HarbingerCatalog.KitFor(actor.Variety.Id);
        foreach (var skill in kit.Skills)
        {
            if (skill.Role != SkillRole.AllyDamageAura) continue;
            var native = GolemData.Skill(SkillsFor(player), skill.EffectRow, level);
            int Raw(uint k) => native.Stats.GetValueOrDefault(k);
            ushort buffRow;
            var statMap = new Dictionary<uint, int>();
            switch (skill.EffectId)
            {
                case "AvatarHatred":
                    buffRow = HatredAuraBuffRow;
                    statMap[Stat.PhysicalDamagePctToAddAsCold] = Raw(Stat.PhysicalDamagePctToAddAsCold) is int c and > 0 ? c : 30;
                    break;
                case "AvatarAnger":
                    buffRow = AngerAuraBuffRow;
                    statMap[Stat.AttackMinimumAddedFireDamage] = Math.Max(1, (int)Math.Round(Raw(Stat.AttackMinimumAddedFireDamage) * native.Multiplier));
                    statMap[Stat.AttackMaximumAddedFireDamage] = Math.Max(1, (int)Math.Round(Raw(Stat.AttackMaximumAddedFireDamage) * native.Multiplier));
                    statMap[Stat.PhysicalDamagePctToAddAsFire] = 25;
                    break;
                case "AvatarWrath":
                    buffRow = WrathAuraBuffRow;
                    statMap[Stat.AttackMinimumAddedLightningDamage] = Math.Max(1, (int)Math.Round(Raw(Stat.AttackMinimumAddedLightningDamage) * native.Multiplier));
                    statMap[Stat.AttackMaximumAddedLightningDamage] = Math.Max(1, (int)Math.Round(Raw(Stat.AttackMaximumAddedLightningDamage) * native.Multiplier));
                    statMap[Stat.PhysicalDamagePctToAddAsLightning] = 25;
                    break;
                default:
                    continue;
            }
            sources.Add(new(actorId, $"harbinger:aura:{actorId}:{skill.EffectId}", buffRow, actor.Walk.Position(now),
                AllyAuraDefaultRadius, statMap, null));
        }
    }

    static async Task RemoveAppliedAllyAura(Player player, Area area, (uint MonsterId, string AuraKey) key)
    {
        if (!area.AppliedAllyAuras.Remove(key, out var applied)) return;
        if (player.Area.Monsters.TryGetValue(key.MonsterId, out var m))
            m.RemoveTemporaryStats(key.AuraKey);
        foreach (var (viewer, buffId) in applied.ViewerBuffs)
        {
            if (viewer.Closed || viewer.LeavingArea) continue;
            if (!MonsterShown(viewer, key.MonsterId)) continue;
            await SendWorldRecord(viewer, key.MonsterId,
                InstanceEntities.RemoveBuff(key.MonsterId, buffId), null);
        }
    }

    static async Task ClearAurasFromSource(Player player, Area area, uint sourceId)
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
            await RemoveAppliedAllyAura(player, area, key);
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

    static async Task SyncPlayerCurses(Player player, Area area, Player[] members, (int X, int Y)[] positions)
    {
        // Gather curse aura emitters on active Harbingers and King helpers.
        List<(uint SourceId, string EffectId, ushort BuffRow, (int X, int Y) Pos, Dictionary<uint, int> Stats)>? emitters = null;
        double now = AreaTime(player);
        byte level = AreaLevel(player);
        foreach (var h in area.Encounters!)
        {
            if (h.State != HarbingerState.Active) continue;
            CollectCurseEmitters(player, level, now, h.Monster, ref emitters);
            foreach (uint helperId in h.Helpers)
                CollectCurseEmitters(player, level, now, helperId, ref emitters);
        }

        for (int i = 0; i < members.Length; i++)
        {
            var member = members[i];
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

            if (active is not null && CurseImmune(member)) active = null;
            if (active is null)
            {
                if (ActivePlayerCurses.TryGetValue(member, out var existing) && (existing.Stats.Count > 0 || existing.Holders.Count > 0))
                    await ClearPlayerCursesAsync(member);
                continue;
            }

            var state = ActivePlayerCurses.GetValue(member, _ => new());
            var nextStats = new Dictionary<uint, int>();
            foreach (var (_, (_, _, es)) in active)
                foreach (var (k, v) in es)
                    nextStats[k] = nextStats.GetValueOrDefault(k) + v;

            if (nextStats.Count != state.Stats.Count || nextStats.Any(kv => state.Stats.GetValueOrDefault(kv.Key) != kv.Value))
            {
                state.Stats.Clear();
                foreach (var (k, v) in nextStats) state.Stats[k] = v;
            }

            foreach (string gone in state.Holders.Keys.Where(k => !active.ContainsKey(k)).ToArray())
            {
                object holder = state.Holders[gone];
                state.Holders.Remove(gone);
                await ReleasePlayerDebuff(member, holder);
            }
            foreach (var (eid, (buffRow, srcId, es)) in active)
            {
                if (!state.Holders.TryGetValue(eid, out var holder))
                    state.Holders[eid] = holder = new object();
                await HoldPlayerDebuff(member, holder, buffRow, srcId, es);
            }
        }
    }

    static bool CurseImmune(Player player)
    {
        if (player.Immune || InGracePeriod(player)) return true;
        var stats = CharacterPlayerStats(player);
        return PlayerCurseImmunityAt(player, AreaTime(player), stats.GetValueOrDefault)
            || stats.GetValueOrDefault(Stat.Hexproof) != 0;
    }

    static void CollectCurseEmitters(Player player, byte level, double now, uint actorId,
        ref List<(uint SourceId, string EffectId, ushort BuffRow, (int X, int Y) Pos, Dictionary<uint, int> Stats)>? emitters)
    {
        if (actorId == 0 || !player.Area.Monsters.TryGetValue(actorId, out var actor) || actor.Dead || actor.Variety is null) return;
        var kit = HarbingerCatalog.KitFor(actor.Variety.Id);
        foreach (var skill in kit.Skills)
        {
            if (skill.Role != SkillRole.PlayerCurseAura) continue;
            var native = GolemData.Skill(SkillsFor(player), skill.EffectRow, level);
            int Raw(uint k) => native.Stats.GetValueOrDefault(k);
            var curseStats = new Dictionary<uint, int>();
            void Put(uint key, int val) { if (val != 0) curseStats[key] = val; }

            ushort buffRow;
            switch (skill.EffectId)
            {
                case "AvatarEnfeebleAura":
                    buffRow = EnfeebleBuffRow;
                    Put(Stat.AccuracyRatingPlusPct, Raw(Stat.AccuracyRatingPlusPct));
                    Put(Stat.EnfeebleDamagePlusPctFinal, Raw(Stat.EnfeebleDamagePlusPctFinal));
                    Put(Stat.YouAreCursedWithEnfeeble, 1);
                    break;
                case "AvatarTemporalChainsAura":
                    buffRow = TemporalChainsBuffRow;
                    // curse_effect_+%_final_vs_players = -50 halves temporal_chains_action_speed_+%_final (-20 -> -10).
                    int tcBase = Raw(Stat.TemporalChainsActionSpeedPlusPctFinal);
                    int vsPlayer = Raw(Stat.CurseEffectPlusPctFinalVsPlayers);
                    int tcEffective = (int)Math.Round(tcBase * Math.Max(0, 1.0 + vsPlayer / 100.0));
                    Put(Stat.TemporalChainsActionSpeedPlusPctFinal, tcEffective);
                    Put(Stat.YouAreCursedWithTemporalChains, 1);
                    break;
                case "AvatarAssassinsMarkAura":
                    buffRow = AssassinsMarkBuffRow;
                    Put(Stat.EnemyAdditionalCriticalStrikeChanceAgainstSelf, Raw(Stat.EnemyAdditionalCriticalStrikeChanceAgainstSelf));
                    Put(Stat.BaseSelfCriticalStrikeMultiplierMinusPct, Raw(Stat.BaseSelfCriticalStrikeMultiplierMinusPct));
                    break;
                default:
                    continue;
            }
            (emitters ??= []).Add((actorId, skill.EffectId, buffRow, actor.Walk.Position(now), curseStats));
        }
    }

    static async Task ClearAllPlayerCurses(Player player)
    {
        foreach (var member in Members(player))
            if (ActivePlayerCurses.TryGetValue(member, out var s) && (s.Stats.Count > 0 || s.Holders.Count > 0))
                await ClearPlayerCursesAsync(member);
    }

    static async Task ClearPlayerCursesAsync(Player player)
    {
        if (!ActivePlayerCurses.TryGetValue(player, out var state)) return;
        object[] holders = [.. state.Holders.Values];
        state.Stats.Clear();
        state.Holders.Clear();
        ActivePlayerCurses.Remove(player);
        if (player.Closed || player.LeavingArea) return;
        foreach (var holder in holders)
            await ReleasePlayerDebuff(player, holder);
    }

    static void ClearPlayerCursesSync(Player player)
    {
        if (!ActivePlayerCurses.TryGetValue(player, out var state)) return;
        state.Stats.Clear();
        state.Holders.Clear();
        ActivePlayerCurses.Remove(player);
    }
}
