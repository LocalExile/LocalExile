using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string FrostShieldEffect = "FrostShield";
    const string SigilOfPowerEffect = "SigilOfPower";
    const ushort FrostShieldBuff = 1609;
    const ushort SigilOfPowerBuff = 1611;
    const ushort FrostShieldCriticalBuff = 1622;

    static readonly Store<InstanceState, ZoneSpellArea> ZoneSpellAreas = new();

    sealed class ZoneSpellArea
    {
        public readonly Dictionary<Player, FrostShieldZone> Shields = [];
        public readonly Dictionary<Player, SigilZone> Sigils = [];
    }

    abstract class ZoneSpell(Player owner, ushort skill, int effect, GemSourceLifetimes.SourceLifetime source,
        (int X, int Y) centre, int radius, double placed, double duration)
    {
        public readonly Player Owner = owner;
        public readonly uint Object = owner.TakeObjectId();
        public readonly GameArea Area = owner.Area;
        public readonly (int X, int Y) Centre = centre;
        public readonly int Radius = radius;
        public readonly double Placed = placed;
        public readonly double Until = placed + duration;
        public readonly HashSet<Player> Recipients = [];
        public int Stages = 1;
        public bool Ended;

        public abstract ushort Buff { get; }
        public bool Protects => this is FrostShieldZone;
        public bool Active(double at) => !Ended && at < Until && Owner is { Closed: false, LeavingArea: false, PlayerLife: > 0 };

        public bool Contains((int X, int Y) point)
        {
            long x = (long)point.X - Centre.X, y = (long)point.Y - Centre.Y;
            return Math.Abs(x) <= Radius && Math.Abs(y) <= Radius && x * x + y * y <= (long)Radius * Radius;
        }

        public bool SourceCurrent() => Owner is { Closed: false, LeavingArea: false, PlayerLife: > 0 }
            && ReferenceEquals(Owner.Area, Area) && GemSourceLifetimeCurrentLocked(Owner, skill, effect, source);
    }

    sealed class FrostShieldZone(Player owner, ushort skill, int effect, GemSourceLifetimes.SourceLifetime source,
        (int X, int Y) centre, int radius, double placed, double duration, IReadOnlyDictionary<uint, int> stats,
        IReadOnlyDictionary<uint, int> coldStats)
        : ZoneSpell(owner, skill, effect, source, centre, radius, placed, duration)
    {
        public readonly int LifePerStage = stats.GetValueOrDefault(Stat.FrostGlobeHealthPerStage);
        public readonly int MaxStages = stats.GetValueOrDefault(Stat.FrostGlobeMaxStages);
        public readonly double StageInterval = stats.GetValueOrDefault(Stat.FrostGlobeStageGainIntervalMs) / 1000d;
        public readonly double DrainPerSecond = stats.GetValueOrDefault(Stat.EnergyShieldLostPerMinute) / 60d;
        public readonly int CritPerStage = stats.GetValueOrDefault(Stat.FrostGlobeAdditionalSpellBaseCriticalStrikeChancePerStage);
        public readonly int InsideAbsorb = stats.GetValueOrDefault(Stat.FrostGlobeAbsorbDamagePctEnemyInBubble);
        public readonly int OutsideAbsorb = stats.GetValueOrDefault(Stat.FrostGlobeAbsorbDamagePctEnemyOutsideBubble);
        public readonly IReadOnlyDictionary<uint, int> ColdStats = coldStats;
        public readonly HashSet<SpawnedMonster> Chilled = [];
        public ulong ChillSource => Object;
        public int Life = stats.GetValueOrDefault(Stat.FrostGlobeHealthPerStage);
        public double DrainSeconds;
        public double DrainCarry;
        public bool Draining = owner.EnergyShield.Current > 0;

        public override ushort Buff => FrostShieldBuff;
    }

    sealed class SigilZone(Player owner, ushort skill, int effect, GemSourceLifetimes.SourceLifetime source,
        (int X, int Y) centre, int radius, double placed, double duration, IReadOnlyDictionary<uint, int> stats)
        : ZoneSpell(owner, skill, effect, source, centre, radius, placed, duration)
    {
        public readonly int MaxStages = stats.GetValueOrDefault(Stat.CircleOfPowerMaxStages);
        public readonly int ManaPerStage = Math.Max(1, stats.GetValueOrDefault(Stat.BaseCircleOfPowerManaSpendPerUpgrade));
        public readonly int LightningMin = stats.GetValueOrDefault(Stat.CircleOfPowerMinAddedLightningPerStage);
        public readonly int LightningMax = stats.GetValueOrDefault(Stat.CircleOfPowerMaxAddedLightningPerStage);
        public readonly int EnemyDamage = stats.GetValueOrDefault(Stat.CircleOfPowerEnemyDamagePlusPctFinalAtMaxStages);
        public long Mana;
        public double NextStage = placed + 1;

        public override ushort Buff => SigilOfPowerBuff;
    }

    internal static bool? CastZoneSpell(Player player, string? id, ushort skill, (int X, int Y) target, Func<bool> current)
    {
        if (id is not (FrostShieldEffect or SigilOfPowerEffect) || IsTotemSkill(player, skill)) return null;
        using var mutation = ProjectileMutation(player, advance: false);
        if (!current() || player.Closed || player.LeavingArea || player.PlayerLife == 0 || InTown(player)) return false;
        var gem = SupportedSkill(player, skill);
        if (!gem.RequirementsMet || CaptureGemSourceLifetimeLocked(player, skill, gem.Effect) is not { } source
            || !GemSourceLifetimeCurrentLocked(player, skill, gem.Effect, source)) return false;

        var stats = CharacterPlayerStats(player);
        var area = SkillAreaStats(player, gem.Stats, gem.Effect, stats);
        int Own(uint key) => gem.Stats.GetValueOrDefault(key);
        int AreaStat(uint key) => area.GetValueOrDefault(key);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), stats.GetValueOrDefault,
            Own, DurationKinds(player, gem.Effect));
        bool frost = id == FrostShieldEffect;
        // The unit radii of the exported skill definitions, as used in Path of Building's act_int.lua.
        int basis = AreaStat(Stat.ActiveSkillBaseAreaOfEffectRadius);
        if (basis <= 0) basis = frost ? 22 : 30;
        int radius = SkillArea.Radius(basis, AreaStat, spell: true);
        if (duration <= 0 || radius <= 0) return false;
        double now = AreaTime(player);
        var centre = player.Layout.Near(target);
        var state = ZoneSpellAreas.GetOrCreateValue(player.Instance);
        if (frost)
        {
            if (state.Shields.Remove(player, out var old)) EndZoneSpell(old);
            var shield = new FrostShieldZone(player, skill, gem.Effect, source, centre, radius, now, duration, gem.Stats, area);
            state.Shields.Add(player, shield);
            ShowZoneSpell(shield, "Metadata/Effects/Spells/FrostGlobe/shield/FrostGlobe.ao", basis);
            StepFrostShield(shield, now);
            UpdateZoneRecipients(shield, now, changedStage: false);
        }
        else
        {
            if (state.Sigils.Remove(player, out var old)) EndZoneSpell(old);
            var sigil = new SigilZone(player, skill, gem.Effect, source, centre, radius, now, duration, gem.Stats);
            state.Sigils.Add(player, sigil);
            ShowZoneSpell(sigil, "Metadata/Effects/Spells/circle_of_power/circle_of_power.ao", basis);
            UpdateZoneRecipients(sigil, now, changedStage: false);
        }
        return true;
    }

    static void ShowZoneSpell(ZoneSpell zone, string art, int basis)
    {
        var visual = new EffectObject(zone.Centre.X, zone.Centre.Y, art) { Scale = zone.Radius / (float)basis };
        zone.Owner.TrackProjectileObject(zone.Object);
        ProjectilesFor(zone.Owner).Create(zone.Object, () => zone.Ended ? null
            : InstanceEntities.CreateObject(zone.Object, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), visual.Blob()), wake: true);
    }

    internal static Task TickZoneSpells(Player player)
    {
        if (!ZoneSpellAreas.TryGetValue(player.Instance, out var area)
            || !(area.Shields.ContainsKey(player) || area.Sigils.ContainsKey(player))) return Task.CompletedTask;
        using var mutation = ProjectileMutation(player, advance: false);
        double now = AreaTime(player);
        if (area.Shields.TryGetValue(player, out var shield))
        {
            if (!shield.Active(now) || !shield.SourceCurrent())
            {
                area.Shields.Remove(player);
                EndZoneSpell(shield);
            }
            else
            {
                int before = shield.Stages;
                StepFrostShield(shield, now);
                UpdateZoneRecipients(shield, now, shield.Stages != before);
            }
        }
        if (area.Sigils.TryGetValue(player, out var sigil))
        {
            if (!sigil.Active(now) || !sigil.SourceCurrent())
            {
                area.Sigils.Remove(player);
                EndZoneSpell(sigil);
            }
            else UpdateZoneRecipients(sigil, now, changedStage: false);
        }
        return Task.CompletedTask;
    }

    internal static void EndZoneSpells(Player player)
    {
        if (!ZoneSpellAreas.TryGetValue(player.Instance, out var area)) return;
        var activeArea = player.Area;
        void Remove(GameArea _)
        {
            using var mutation = ProjectileMutation(player, advance: false);
            if (area.Shields.Remove(player, out var shield)) EndZoneSpell(shield);
            if (area.Sigils.Remove(player, out var sigil)) EndZoneSpell(sigil);
        }
        if (activeArea.Driven && !activeArea.Owned) activeArea.Defer(Remove);
        else Remove(activeArea);
    }

    static void EndZoneSpell(ZoneSpell zone)
    {
        if (zone.Ended) return;
        zone.Ended = true;
        foreach (var player in zone.Recipients) RemoveZoneRecipient(zone, player);
        zone.Recipients.Clear();
        if (zone is FrostShieldZone shield)
            foreach (var monster in shield.Chilled)
                monster.Ailments.RemoveAura(MonsterElementalAilments.Kind.Chill, shield.ChillSource);
        zone.Owner.ProjectileObjects.Remove(zone.Object);
        if (zone.Owner is { Closed: false, LeavingArea: false } owner && ReferenceEquals(owner.Area, zone.Area))
        {
            if (CurrentGameWork)
                ProjectilesFor(owner).WorldRecords(zone.Object, () => [InstanceProtocol.RemoveObject(zone.Object)], remove: true);
            else owner.RetiredCombatObjects.Add(zone.Object);
        }
    }

    static void StepFrostShield(FrostShieldZone zone, double now)
    {
        if (zone.Draining)
        {
            double delta = Math.Clamp(now - zone.Placed, 0, 1) - zone.DrainSeconds;
            if (delta > 0 && zone.DrainPerSecond > 0)
            {
                var energyShield = zone.Owner.EnergyShield;
                double drainedFor = Math.Min(delta, energyShield.Current / zone.DrainPerSecond);
                energyShield.Lose(drainedFor * zone.DrainPerSecond, ref zone.DrainCarry);
                zone.Owner.Frames.Area?.Life.Changed(zone.Owner);
                zone.DrainSeconds += drainedFor;
                if (zone.StageInterval > 0)
                {
                    int stages = Math.Min(zone.MaxStages, 1 + (int)(zone.DrainSeconds / zone.StageInterval));
                    zone.Life = ActorScale.Saturate((long)zone.Life + (stages - zone.Stages) * (long)zone.LifePerStage);
                    zone.Stages = stages;
                }
                zone.Draining = drainedFor == delta && zone.DrainSeconds < 1 && energyShield.Current > 0;
            }
        }
        bool Chills(SpawnedMonster monster) => monster is { Hostile: true, Dead: false, Life: > 0 }
            && zone.Contains(monster.Walk.Position(now));
        zone.Chilled.RemoveWhere(monster =>
        {
            if (Chills(monster)) return false;
            monster.Ailments.RemoveAura(MonsterElementalAilments.Kind.Chill, zone.ChillSource);
            return true;
        });
        foreach (var (_, monster) in zone.Area.MonstersNear((zone.Centre.X, zone.Centre.Y), zone.Radius))
        {
            if (!Chills(monster)) continue;
            monster.Ailments.ApplyAura(MonsterElementalAilments.Kind.Chill, zone.ChillSource, Math.Min(.3, zone.Until - now), now,
                zone.ColdStats.GetValueOrDefault, key => monster.Effects[key]);
            zone.Chilled.Add(monster);
        }
    }

    static bool ZoneReaches(ZoneSpell zone, Player player, double now) =>
        player is { Closed: false, LeavingArea: false, PlayerLife: > 0 } && ReferenceEquals(player.Area, zone.Area)
        && zone.Contains(player.Walk.Position(now));

    static void UpdateZoneRecipients(ZoneSpell zone, double now, bool changedStage)
    {
        zone.Recipients.RemoveWhere(player =>
        {
            if (ZoneReaches(zone, player, now))
            {
                if (changedStage) MarkPlayerStats(player, PlayerStatStep.Sources);
                return false;
            }
            RemoveZoneRecipient(zone, player);
            return true;
        });
        void Add(Player player)
        {
            if (!ZoneReaches(zone, player, now) || !zone.Recipients.Add(player)) return;
            zone.Area.Buffs.Set(player.Id, BuffRows.Spec(zone.Buff, zone.Object, null));
            if (zone.Protects && ReferenceEquals(player, zone.Owner))
                zone.Area.Buffs.Set(player.Id, BuffRows.Spec(FrostShieldCriticalBuff, zone.Object, null));
            MarkPlayerStats(player, PlayerStatStep.Sources);
        }
        Add(zone.Owner);
        foreach (var ally in zone.Owner.Instance.PlayersExcept(zone.Owner.Id)) Add(ally);
    }

    static void RemoveZoneRecipient(ZoneSpell zone, Player player)
    {
        zone.Area.Buffs.Clear(player.Id, zone.Buff, zone.Object);
        if (zone.Protects && ReferenceEquals(player, zone.Owner))
            zone.Area.Buffs.Clear(player.Id, FrostShieldCriticalBuff, zone.Object);
        MarkPlayerStats(player, PlayerStatStep.Sources);
    }

    internal static Dictionary<uint, int> ZoneSpellPlayerStats(Player player, Dictionary<uint, int> stats)
    {
        if (!ZoneSpellAreas.TryGetValue(player.Instance, out var area) || (area.Shields.Count == 0 && area.Sigils.Count == 0))
            return stats;
        double now = AreaTime(player);
        var here = player.Walk.Position(now);
        if (area.Shields.TryGetValue(player, out var shield) && shield.Active(now) && shield.Contains(here))
        {
            AddZoneStat(stats, Stat.SpellAdditionalCriticalStrikeChancePermyriad, (long)shield.CritPerStage * shield.Stages);
            StatsChangeIn(shield.Until - now);
        }
        AddSigilLightning(area, here, now, stats);
        return stats;
    }

    static void AddMinionSigilLightning(Player player, (int X, int Y) here, double now, Dictionary<uint, int> stats)
    {
        if (ZoneSpellAreas.TryGetValue(player.Instance, out var area)) AddSigilLightning(area, here, now, stats);
    }

    static void AddSigilLightning(ZoneSpellArea area, (int X, int Y) here, double now, Dictionary<uint, int> stats)
    {
        SigilZone? best = null;
        foreach (var sigil in area.Sigils.Values)
        {
            if (!sigil.Active(now) || !sigil.Contains(here)) continue;
            if (best is null || (long)sigil.LightningMax * sigil.Stages > (long)best.LightningMax * best.Stages)
                best = sigil;
        }
        if (best is null) return;
        AddZoneStat(stats, Stat.GlobalMinimumAddedLightningDamage, (long)best.LightningMin * best.Stages);
        AddZoneStat(stats, Stat.GlobalMaximumAddedLightningDamage, (long)best.LightningMax * best.Stages);
        StatsChangeIn(best.Until - now);
    }

    static void AddZoneStat(Dictionary<uint, int> stats, uint key, long value)
    {
        if (value != 0) stats[key] = ActorScale.Saturate(stats.GetValueOrDefault(key) + value);
    }

    static void SigilManaSpent(Player player, uint amount, double now)
    {
        if (!ZoneSpellAreas.TryGetValue(player.Instance, out var area) || !area.Sigils.TryGetValue(player, out var sigil)
            || !sigil.Active(now) || sigil.Stages >= sigil.MaxStages || !sigil.Contains(player.Walk.Position(now))) return;
        sigil.Mana = Math.Min((long)sigil.ManaPerStage * (sigil.MaxStages - sigil.Stages), sigil.Mana + amount);
        if (sigil.Mana < sigil.ManaPerStage || now < sigil.NextStage) return;
        sigil.Mana -= sigil.ManaPerStage;
        sigil.Stages++;
        sigil.NextStage = now + 1;
        UpdateZoneRecipients(sigil, now, changedStage: true);
    }

    static double SigilEnemyDamageFactor(Player player, SpawnedMonster attacker, double now)
    {
        if (!attacker.Hostile || !ZoneSpellAreas.TryGetValue(player.Instance, out var area) || area.Sigils.Count == 0) return 1;
        int modifier = 0;
        var here = attacker.Walk.Position(now);
        foreach (var sigil in area.Sigils.Values)
            if (sigil.Stages >= sigil.MaxStages && sigil.Active(now) && sigil.Contains(here))
                modifier = Math.Min(modifier, sigil.EnemyDamage);
        return Math.Max(0, 1 + modifier / 100d);
    }

    static bool HasFrostShieldZones(Player player) =>
        ZoneSpellAreas.TryGetValue(player.Instance, out var area) && area.Shields.Count > 0;

    static int AbsorbFrostShield(Player player, (int X, int Y) here, (int X, int Y) enemy, int damage, double now)
    {
        if (damage <= 0 || !ZoneSpellAreas.TryGetValue(player.Instance, out var area) || area.Shields.Count == 0) return damage;
        FrostShieldZone? best = null;
        int absorb = 0;
        foreach (var shield in area.Shields.Values)
        {
            if (!shield.Active(now) || shield.Life <= 0 || !shield.Contains(here)) continue;
            int percent = shield.Contains(enemy) ? shield.InsideAbsorb : shield.OutsideAbsorb;
            int share = (int)Math.Min(shield.Life, (long)damage * Math.Clamp(percent, 0, 100) / 100);
            if (share > absorb) { best = shield; absorb = share; }
        }
        if (best is null) return damage;
        best.Life -= absorb;
        if (best.Life <= 0)
        {
            area.Shields.Remove(best.Owner);
            EndZoneSpell(best);
        }
        return damage - absorb;
    }
}
