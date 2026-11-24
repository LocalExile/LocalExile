using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    // What every warcry does at its release besides its own effect, within its own radius around where it was used: the
    // client's warcry action (0x141abfb30) scans that radius for Chest objects and corpses when Deathrattle's stats are set.
    public const uint CorpseExplosionStat = Stat.NearbyCorpsesExplodeDealingPctMaximumLifePhysicalDamageOnWarcry,
        OpenChestsStat = Stat.OpenNearbyChestsOnWarcry,
        CorpseLootStat = Stat.CorpsesDropLootAgainOnWarcryChancePct;

    // Path of Building's EnemyExplode radius (skill("radius", 22)), grown by area of effect.
    const int CorpseExplosionRadius = 22;

    // shortcut: the generic gore burst (MiscAnimated SuicideExplodeNoGibs) at its own size; which art the client's warcry
    // wave (0x141abfea0) plays for an exploding corpse is not found yet. Upgrade once that consumer is traced.
    public const string CorpseExplosionAo = "Metadata/Effects/Spells/monsters_effects/gore_explode/gore_explosion_no_gibs.ao";
    const int CorpseExplosionArtMs = 2000;

    public const ushort RallyingCryBuffRow = 1538, SeismicCryBuffRow = 1544, AncestralCryBuffRow = 1545,
        InfernalCryBuffRow = 1540, BattlemagesCryBuffRow = 1866, VengefulCryBuffRow = 2476;

    /// <summary>The BuffDefinitions row a warcry's own buff uses, or null when no buff is granted for it.</summary>
    public static ushort? WarcryBuffRow(string effectId) => effectId switch
    {
        "RallyingCry" => RallyingCryBuffRow,
        "SeismicCry" => SeismicCryBuffRow,
        "AncestralCry" => AncestralCryBuffRow,
        "InfernalCry" => InfernalCryBuffRow,
        "BattlemagesCry" => BattlemagesCryBuffRow,
        "VengefulCry" => VengefulCryBuffRow,
        _ => null,
    };

    /// <summary>Stats for buffs derived from the gem, in the order used by BuffDefinitions.
    /// Rallying Cry derives its damage separately from the main-hand weapon.</summary>
    public static (uint Key, int Value)[]? WarcryBuffStats(string effectId, int power, double buffEffect, Func<uint, int> own)
    {
        if (WarcryBuffRow(effectId) is null) return null;
        int capped = Math.Clamp(power, 0, effectId == "AncestralCry" ? 30 : 25);
        int steps5 = capped / 5, steps10 = capped / 10;
        int Scaled(int steps, int per) => (int)Math.Clamp(Math.Floor(steps * (double)per * buffEffect), 0, int.MaxValue);
        return effectId switch
        {
            "SeismicCry" =>
            [
                (Stat.ArmourPlusPctFinalFromSeismicCryBuff, Scaled(steps5, own(Stat.SeismicCryArmourPlusPctFinalPer5PowerUpToCap))),
                (Stat.StunThresholdPlusPct, Scaled(steps5, own(Stat.SeismicCryStunThresholdPlusPctPer5PowerUpToCap))),
            ],
            "AncestralCry" =>
            [
                (Stat.BaseResistAllElementsPct, Scaled(steps5, own(Stat.AncestralCryElementalResistPctPer5PowerUpToCap))),
                (Stat.AdditionalMaximumAllElementalResistancesPct, Scaled(steps10, own(Stat.AncestralCryMaximumElementalResistPctPer10PowerUpToCap))),
            ],
            "InfernalCry" =>
                [(Stat.PhysicalDamagePctToAddAsFire, Scaled(steps5, own(Stat.InfernalCryPhysicalDamagePctToAddAsFirePer5PowerUpToCap)))],
            "BattlemagesCry" =>
                [(Stat.AdditionalBaseCriticalStrikeChance, Scaled(steps5, own(Stat.DivineCryAdditionalBaseCriticalStrikeChancePer5PowerUpToCap)))],
            "VengefulCry" =>
            [
                (Stat.BaseRageRegenerationPerMinute, Scaled(steps5, own(Stat.RageWarcryGainXRagePerMinutePer5MonsterPowerMax25Power))),
                (Stat.MaximumRage, Scaled(1, own(Stat.RageWarcryMaximumRagePlus))),
                (Stat.NoInherentRageLoss, 1),
                (Stat.AffectedByAWarcry, 1),
            ],
            _ => null,
        };
    }

    // BuffDefinitions lists all five minima first, followed by all five maxima.
    static readonly (DamageType Type, uint Min, uint Max)[] RallyingDamageStats =
    [
        (DamageType.Physical, Stat.AttackMinimumAddedPhysicalDamage, Stat.AttackMaximumAddedPhysicalDamage),
        (DamageType.Fire, Stat.AttackMinimumAddedFireDamage, Stat.AttackMaximumAddedFireDamage),
        (DamageType.Cold, Stat.AttackMinimumAddedColdDamage, Stat.AttackMaximumAddedColdDamage),
        (DamageType.Lightning, Stat.AttackMinimumAddedLightningDamage, Stat.AttackMaximumAddedLightningDamage),
        (DamageType.Chaos, Stat.AttackMinimumAddedChaosDamage, Stat.AttackMaximumAddedChaosDamage),
    ];

    static (uint Key, int Value)[] RallyingCryDamage(DamagePacket weapon, double fraction)
    {
        var values = new (uint, int)[2 * RallyingDamageStats.Length];
        for (int i = 0; i < RallyingDamageStats.Length; i++)
        {
            var (type, min, max) = RallyingDamageStats[i];
            var range = weapon[type];
            values[i] = (min, (int)Math.Clamp(Math.Floor(range.Min * fraction), 0, int.MaxValue));
            values[i + RallyingDamageStats.Length] = (max, (int)Math.Clamp(Math.Floor(range.Max * fraction), 0, int.MaxValue));
        }
        return values;
    }

    sealed record WarcryGrant(GameArea Area, ushort Row, uint Source, double Until, (uint Key, int Value)[] Values, long Strength = 0);
    static readonly Store<Player, List<WarcryGrant>> WarcryGrants = new();
    static readonly RuleTable<SpawnedMonster, List<WarcryGrant>> MinionWarcryGrants = new();

    sealed record WarningCallGrant(GameArea Area, int Armour, double Until);
    static readonly Store<Player, WarningCallGrant> WarningCallGrants = new();

    static void AddWarcryStats(Player player, Dictionary<uint, int> stats)
    {
        if (player.PlayerLife == 0) return;
        var area = player.Area;
        double now = AreaTime(player);
        if (WarningCallGrants.TryGetValue(player, out var warning) && warning.Area == area && warning.Until > now)
        {
            uint key = Stat.PhysicalDamageReductionRatingPlusPct;
            stats[key] = ActorScale.Add(key, stats.GetValueOrDefault(key), warning.Armour);
            StatsChangeIn(warning.Until - now);
        }
        if (!WarcryGrants.TryGetValue(player, out var grants)) return;
        AddWarcryStats(grants, area, now, stats);
        foreach (var grant in grants)
            if (grant.Row == VengefulCryBuffRow && grant.Area == area && grant.Until > now)
                StatsChangeIn(grant.Until - now);
    }

    static void AddWarcryStats(List<WarcryGrant> grants, GameArea area, double now, Dictionary<uint, int> stats)
    {
        // One copy of a given buff applies. A weaker caster's unexpired grant takes over when the stronger ends.
        for (int i = 0; i < grants.Count; i++)
        {
            var grant = grants[i];
            if (grant.Until <= now || grant.Area != area) continue;
            for (int k = 0; k < grant.Values.Length; k++)
            {
                var (key, value) = grant.Values[k];
                bool strongest = true;
                for (int j = 0; j < grants.Count && strongest; j++)
                {
                    var other = grants[j];
                    if (j == i || other.Row != grant.Row || other.Area != area || other.Until <= now) continue;
                    long strength = grant.Row == RallyingCryBuffRow ? grant.Strength : value;
                    long competing = grant.Row == RallyingCryBuffRow ? other.Strength : other.Values[k].Value;
                    strongest = competing < strength || competing == strength && i < j;
                }
                if (strongest && value != 0) stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + value);
            }
        }
    }

    static void ReceiveWarcry(GameArea area, uint entity, List<WarcryGrant> grants, WarcryGrant gift, double now)
    {
        grants.RemoveAll(g => g.Until <= now || g.Row == gift.Row && g.Source == gift.Source);
        grants.Add(gift);
        double seconds = gift.Until - now;
        if (gift.Row == VengefulCryBuffRow)
        {
            area.Buffs.Set(entity, BuffRows.Spec(gift.Row, gift.Source,
                gift.Values.ToDictionary(v => v.Key, v => v.Value), seconds));
            return;
        }
        uint[]? values = gift.Row == RallyingCryBuffRow ? [.. gift.Values.Select(v => (uint)v.Value)] : null;
        area.Buffs.Set(entity, new Buff(gift.Row, gift.Source, Values: values, U68: Buff.Seconds(seconds)));
    }

    static void UpdateWarcryStats(Player player)
    {
        RebuildPools(player);
        MarkPlayerStats(player, PlayerStatStep.Sources);
        PresentStats(player);
    }

    internal static void ExpireWarcryBuffs(Player player)
    {
        double now = AreaTime(player);
        bool changed = WarningCallGrants.TryGetValue(player, out var warning)
            && (warning.Until <= now || warning.Area != player.Area || player.PlayerLife == 0)
            && WarningCallGrants.Remove(player);
        if (!WarcryGrants.TryGetValue(player, out var grants))
        {
            if (changed) UpdateWarcryStats(player);
            return;
        }
        for (int i = grants.Count - 1; i >= 0; i--)
        {
            var grant = grants[i];
            if (player.PlayerLife > 0 && grant.Until > now && grant.Area == player.Area) continue;
            if (grant.Area == player.Area) player.Area.Buffs.Clear(player.Id, grant.Row, grant.Source);
            grants.RemoveAt(i);
            changed = true;
        }
        if (grants.Count == 0) WarcryGrants.Remove(player);
        if (changed) UpdateWarcryStats(player);
    }

    static void RemoveRallyingCryOnWeaponSwapLocked(Player player)
    {
        var area = player.Area;
        bool RemoveOutgoing(uint recipient, List<WarcryGrant> grants)
        {
            if (grants.RemoveAll(grant => grant.Area == area && grant.Row == RallyingCryBuffRow
                && grant.Source == player.Id) == 0) return false;
            area.Buffs.Clear(recipient, RallyingCryBuffRow, player.Id);
            return true;
        }
        foreach (var ally in player.Instance.PlayersExcept(player.Id))
            if (WarcryGrants.TryGetValue(ally, out var grants) && RemoveOutgoing(ally.Id, grants))
            {
                if (grants.Count == 0) WarcryGrants.Remove(ally);
                UpdateWarcryStats(ally);
            }
        foreach (var (id, monster) in area.Monsters)
            if (MinionWarcryGrants.TryGetValue(monster, out var grants) && RemoveOutgoing(id, grants)
                && grants.Count == 0)
                MinionWarcryGrants.Remove(monster);
    }

    public static void ApplyWarcryBuffLocked(Player player, string effectId, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats, (int X, int Y) at, int radius, double now, double duration, double buffEffect)
    {
        if (playerStats.GetValueOrDefault(Stat.WarcriesDoNotApplyBuffsToSelfOrAllies) != 0
            || own.GetValueOrDefault(Stat.WarcriesDoNotApplyBuffsToSelfOrAllies) != 0
            || duration <= 0 || WarcryBuffRow(effectId) is not ushort row) return;
        bool rallying = row == RallyingCryBuffRow;
        var nearbyPlayers = player.Instance.PlayersExcept(player.Id)
            .Where(ally => !ally.LeavingArea && ally.PlayerLife > 0 && Dist(ally.Walk.Position(now), at) <= radius).ToArray();
        int power = WarcryPowerAt(player, at, radius, now, effectId, playerStats, nearbyPlayers.Length);
        if (effectId == "BattlemagesCry") buffEffect += playerStats.GetValueOrDefault(Stat.BattlemagesCryBuffEffectPlusPct) / 100d;
        WarcryGrant gift;
        WarcryGrant? ownMinionGift = null;
        if (rallying)
        {
            if (WeaponIn(player, ActiveMainHand(player)) is not { LocalDamage: { } weapon }) return;
            double fraction = Math.Clamp(power, 0, 30) / 5 * own.GetValueOrDefault(Stat.RallyingCryWeaponDamagePctForAlliesPer5MonsterPower)
                * Math.Max(0, buffEffect + playerStats.GetValueOrDefault(Stat.RallyingCryBuffEffectPlusPct) / 100d) / 100d;
            if (fraction <= 0) return;
            var values = RallyingCryDamage(weapon, fraction);
            gift = new(player.Area, row, player.Id, now + duration, values, values.Sum(v => (long)v.Value));
            double minionEffect = Math.Max(0, 1 + own.GetValueOrDefault(Stat.RallyingCryBuffEffectOnMinionsPlusPctFinal) / 100d);
            var minionValues = RallyingCryDamage(weapon, fraction * minionEffect);
            ownMinionGift = gift with { Values = minionValues, Strength = minionValues.Sum(v => (long)v.Value) };
        }
        else
        {
            if (WarcryBuffStats(effectId, power, buffEffect, key => own.GetValueOrDefault(key)) is not { } stats) return;
            gift = new(player.Area, row, player.Id, now + duration, stats);
        }
        void GrantPlayer(Player recipient)
        {
            ReceiveWarcry(player.Area, recipient.Id, WarcryGrants.GetOrCreateValue(recipient), gift, now);
            UpdateWarcryStats(recipient);
        }
        // Call to Arms suppresses the caster's own warcry buffs. Allies still get theirs.
        if (!rallying && playerStats.GetValueOrDefault(Stat.KeystoneCallToArms) == 0) GrantPlayer(player);
        foreach (var ally in nearbyPlayers) GrantPlayer(ally);
        var ownMinions = ownMinionGift is null ? null : player.Minions.All.Select(m => m.Id).ToHashSet();
        foreach (var (id, ally) in player.Area.Monsters)
            if (!ally.Hostile && !ally.Dead && ally.Life > 0 && Dist(ally.Walk.Position(now), at) <= radius)
                ReceiveWarcry(player.Area, id, MinionWarcryGrants.GetOrCreateValue(ally),
                    ownMinions?.Contains(id) == true ? ownMinionGift! : gift, now);
        Console.WriteLine($"[gateway] {effectId}: warcry buff row {row} power={power} for {duration:0.###} s");
    }

    static int WarcryPowerAt(Player player, (int X, int Y) at, int radius, double now, string effectId,
        IReadOnlyDictionary<uint, int> stats, int nearbyAllies)
    {
        if (stats.GetValueOrDefault(Stat.WarcriesHaveInfinitePower) != 0) return InfiniteWarcryPower;
        int minimum = stats.GetValueOrDefault(Stat.WarcriesHaveMinimum10Power) != 0 ? 10 : 0;
        minimum = Math.Max(minimum, effectId switch
        {
            "AncestralCry" => stats.GetValueOrDefault(Stat.AncestralCryMinimumPower),
            "SeismicCry" => stats.GetValueOrDefault(Stat.SeismicCryMinimumPower),
            _ => 0
        });
        bool rallying = effectId == "RallyingCry";
        long nearby = player.Area.Monsters.Values.Where(m => !m.Dead && m.Life > 0
            && (m.Hostile || rallying) && Dist(m.Walk.Position(now), at) <= radius).Sum(m => (long)WarcryPower(m.Rarity));
        if (rallying) nearby += 5L * nearbyAllies;
        return Math.Max(minimum, ActorScale.Saturate(nearby * (100L + stats.GetValueOrDefault(Stat.WarcryMonsterPowerPlusPct)) / 100));
    }

    public static Task WarcryReleased(Player player, (int X, int Y) at, int radius, int epoch) =>
        WarcryReleased(player, at, radius, epoch, "");

    static async Task WarcryReleased(Player player,
        (int X, int Y) at, int radius, int epoch, string effectId)
    {
        List<Player>? onslaught = null;
        Dictionary<uint, int> stats;
        using (ProjectileMutation(player, advance: false))
        {
            if (!player.ProjectileActive(epoch) || player.PlayerLife == 0) return;
            double now = ProjectileWorldTime(player);
            player.RecentEvents.RecordAmount(StatConditions.UsedWarcry, now);
            stats = AreaPlayerStats(player);
            if (stats.GetValueOrDefault(Stat.WarcryPhysicalDamageReductionRatingPlusPctPer5PowerFor8Seconds) is int armour and > 0)
            {
                int allies = effectId == "RallyingCry" ? player.Instance.PlayersExcept(player.Id)
                    .Count(ally => !ally.LeavingArea && ally.PlayerLife > 0 && Dist(ally.Walk.Position(now), at) <= radius) : 0;
                int power = WarcryPowerAt(player, at, radius, now, effectId, stats, allies);
                int increase = (int)Math.Min(100L, (long)(power / 5) * armour);
                if (increase > 0)
                {
                    WarningCallGrants.AddOrUpdate(player, new(player.Area, increase, now + 8));
                    UpdateWarcryStats(player);
                }
            }
            if (stats.GetValueOrDefault(Stat.YouAndNearbyAllysGainOnslaughtFor4SecondsOnWarcry) != 0)
            {
                const double duration = 4;
                foreach (var owner in player.Instance.PlayersExcept(player.Id).Prepend(player))
                {
                    if (owner.Closed || owner.LeavingArea || owner.PlayerLife == 0) continue;
                    if ((ReferenceEquals(owner, player) || Dist(owner.Walk.Position(now), at) <= radius)
                        && KillOnslaughtOf(owner).Gain(now, duration) && Marked(owner, PlayerStatStep.Onslaught))
                        (onslaught ??= []).Add(owner);
                    foreach (var fighter in owner.Summons.Fighters.Values)
                        if (!fighter.Monster.Hostile && !fighter.Monster.Dead && fighter.Monster.Life > 0
                            && Dist(fighter.Monster.Walk.Position(now), at) <= radius && GolemCurrent(owner, fighter))
                            GainMinionOnslaughtLocked(owner, fighter, now, duration);
                }
            }
        }
        if (onslaught is not null)
            foreach (var recipient in onslaught) await SendKillOnslaught(recipient);
        int Get(uint key) => stats.GetValueOrDefault(key);
        // "Removes Bleeding when you use a Warcry": the bleed owner a flask's bleed suffix also clears, with no protection.
        if (Get(Stat.RemoveBleedingOnWarcry) != 0) player.IncomingAilments.RemoveOrProtectBleeding(AreaTime(player), 0);
        if (Get(CorpseExplosionStat) is int percent and > 0)
            await ExplodeCorpses(player, at, radius, epoch, percent, Get,
                DamageStats.Conversion(stats, Stats, attack: null));
        bool current;
        current = player.ProjectileActive(epoch) && player.PlayerLife > 0;
        if (current && Get(OpenChestsStat) != 0) await OpenChestsInRadius(player, at, radius);
        if (current && Get(CorpseLootStat) is int lootChance and > 0)
            await DropCorpseLoot(player, at, radius, epoch, lootChance);
    }

    // Howl of the Wolf: each corpse within the warcry's radius drops its loot again with that chance. The corpse stays
    // usable (its explosion, consumption or removal is another effect's).
    static async Task DropCorpseLoot(Player player,
        (int X, int Y) at, int radius, int epoch, int chance)
    {
        List<(SpawnedMonster Monster, (int X, int Y) At)> corpses;
        if (player.Closed || player.LeavingArea || !player.ProjectileActive(epoch) || player.PlayerLife == 0) return;
        double now = ProjectileWorldTime(player);
        corpses = [];
        foreach (var m in player.Area.Monsters.Values)
        {
            if (!IsCorpse(m) || m.CorpseLooted || m.NoRewards || Dist(m.Walk.Position(now), at) > radius) continue;
            // One reroll per corpse: a later warcry sees the flag, so the same corpses cannot be farmed.
            m.CorpseLooted = true;
            corpses.Add((m, m.Walk.Position(now)));
        }
        Console.WriteLine($"[gateway] warcry corpse loot: {corpses.Count} new corpse(s) within {radius} cells, {Math.Min(100, chance)}% each");
        foreach (var (monster, corpseAt) in corpses)
            if (AreaRng(player).Next(100) < Math.Min(100, chance))
                await DropLoot(player, monster, corpseAt);
    }

    // A corpse the engine has: a monster killed in this area visit whose Life type allows corpse use, not yet consumed.
    static bool IsCorpse(SpawnedMonster m) =>
        m.Dead && m.Life == 0 && !m.CorpseConsumed && CorpseTypeAllowsUse(m);

    // The explosion's hit before the target's mitigation: the corpse's maximum Life x percent as physical damage, with the
    // owner's global increases and more multipliers, then his global conversion and gain as extra (poewiki Explode: global,
    // not attack or spell, stats apply). Increases apply before conversion, as for every hit on the server.
    public static DamagePacket CorpseExplosionHit(uint corpseMaxLife, int percent, Func<uint, int> get, DamageConversion conversion)
    {
        double damage = corpseMaxLife * (double)percent / 100;
        long increased = (long)get(Stat.DamagePlusPct) + get(Stat.PhysicalDamagePlusPct) + get(Stat.AreaDamagePlusPct);
        return DamagePacket.Of(DamageType.Physical, damage, damage)
            .Scale(Math.Max(0, 1 + increased / 100.0) * ChargeEffects.DamageMore(get)).Convert(conversion);
    }

    // Deathrattle: every corpse within the warcry's radius explodes and is consumed; each explosion hits the living hostile
    // monsters around its corpse (not an attack or a spell: no block, no evasion; 0% base crit, and flat crit chance and
    // global added damage, which the wiki's Explode page says apply, are not modelled).
    static async Task ExplodeCorpses(Player player,
        (int X, int Y) at, int radius, int epoch, int percent, Func<uint, int> get, DamageConversion conversion)
    {
        int blast = SkillArea.Radius(CorpseExplosionRadius, get, spell: false);
        var consumed = new List<(uint Id, string Label, (int X, int Y) At, bool Remove)>();
        var hits = new List<(uint Id, SpawnedMonster M, MonsterHit Hit, int Damage)>();
        using (ProjectileMutation(player))
        {
            if (player.Closed || player.LeavingArea || !player.ProjectileActive(epoch) || player.PlayerLife == 0) return;
            double now = ProjectileWorldTime(player);
            foreach (var (id, corpse) in player.Area.Monsters.Where(e => IsCorpse(e.Value) && Dist(e.Value.Walk.Position(now), at) <= radius).ToList())
            {
                // Gone for the client (0x2bd below), so gone here too: a later removal (a projectile kill's) must not send it again.
                corpse.CorpseConsumed = true;
                NoteCorpseConsumedLocked(player, corpse);
                player.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
                player.Area.Monsters.Remove(id);
                var hit = CorpseExplosionHit(corpse.MaxLife, percent, get, conversion);
                var centre = corpse.Walk.Position(now);
                consumed.Add((id, corpse.Label, centre, !corpse.CreatedAsCorpse));
                foreach (var (targetId, m) in player.Area.MonstersNear(centre, blast).Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0
                    && Dist(e.Value.Walk.Position(now), centre) <= blast).ToList())
                {
                    var rng = AreaRng(player);
                    var resisted = DamageModel.Mitigate(hit.Roll(rng), type => MonsterHitResistance(m, type, default, now));
                    int damage = MonsterTakesMitigated(m, resisted, false, now, random: rng);
                    if (ApplyTypedMonsterHitLocked(player, m, damage, MonsterHitChaosDamage(m, resisted, false, now, damage)) is not { } applied) continue;
                    if (applied.StageStarts)
                    {
                        CommitBossStage(player, targetId, m, now);
                        applied = applied with { StageStarts = false };
                    }
                    hits.Add((targetId, m, applied, damage));
                }
            }
        }
        var art = new List<uint>();
        foreach (var (id, label, centre, remove) in consumed)
        {
            uint effect;
            effect = player.TakeObjectId();
            art.Add(effect);
            await SendWorldCreate(player, effect, () => InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(centre.X, centre.Y, CorpseExplosionAo).Blob()),
                    $"0x02bb create effect 0x{effect:x} {CorpseExplosionAo} at ({centre.X}, {centre.Y})", wake: true, awaitBirth: false);
            if (remove) await Game.SendWorldRemoval(player, id, $"0x02bd corpse 0x{id:x} ({label}) explodes (Deathrattle)");
        }
        if (art.Count > 0)
            _ = AreaFrames.Start(async () =>
            {
                await AreaDelay(player, CorpseExplosionArtMs / 1000.0);
                foreach (uint effect in art)
                    if (player.ProjectileActive(epoch)) await Game.SendWorldRemoval(player, effect, null);
            });
        var deaths = new List<(uint Id, SpawnedMonster M)>();
        foreach (var (id, m, hit, damage) in hits)
            await NotifyMonsterHit(player, id, m, hit, damage, "a corpse explosion", deaths: deaths);
        foreach (var (id, m) in deaths) await MonsterKilled(player, id, m);
    }

    // Deathrattle: the warcry opens the chests within its radius, those the engine has (the quest chests, the QuestObjects
    // module's).
    static Task OpenChestsInRadius(Player player, (int X, int Y) at, int radius) => GameEvents.RunChestsOpened(player, at, radius);
}
