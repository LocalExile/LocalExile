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

    // ---- The other warcries' own buffs ----
    //
    // Only Enduring Cry had an executor (Charges.UseWarcry: charges + life regeneration) and Intimidating Cry its own
    // (IntimidatingCry.cs: the movement gift). The other warcries taunted and cooled down only. What each grants is
    // settled by its own BuffDefinitions row, whose GrantedStats are the stats the buff carries, in order, and by the
    // gem's per-power stats (GrantedEffectStatSets):
    //
    //   warcry            row  GrantedStats (count)                          per-power source
    //   Seismic Cry       1544 armour_+%_final_from_seismic_cry_buff,       seismic_cry_armour_+%_final_per_5_power (15)
    //                          stun_threshold_+% (2)                        seismic_cry_stun_threshold_+%_per_5_power (50)
    //   Ancestral Cry     1545 base_resist_all_elements_%,                 ancestral_cry_elemental_resist_%_per_5_power (5)
    //                          additional_maximum_all_elemental_             ancestral_cry_maximum_elemental_resist_%_per_10_power (1)
    //                          resistances_% (2)
    //   Infernal Cry      1540 physical_damage_%_to_add_as_fire (1)       infernal_cry_physical_damage_%_to_add_as_fire_per_5_power (6)
    //   Battlemage's Cry  1866 additional_base_critical_strike_chance (1)  divine_cry_additional_base_critical_strike_chance_per_5_power (50)
    //
    // Every row is IsRecovery = 0, so the buff carries no values (only IsRecovery rows carry one u32 per GrantedStats
    // entry); the stats reach the client through the player's stats, via the WarcryBuffs step, and the server reads
    // them from the same place. The power steps are whole 5s (10s for Ancestral's maximum resistance), capped at 25
    // power like Enduring Cry's regeneration, and scaled by warcry_buff_effect_+% like Enduring Cry's rate.
    //
    // Not granted: Rallying Cry (row 399) — its damage term is per nearby ally, not per power, and no gem stat
    // settles its mana-regeneration value; General's Cry — no BuffDefinitions row, its effect is exerted attacks
    // only; Vengeful Cry (row 2476) — a retaliation skill with no warcry handling here; Abyssal Cry (row 402) — a
    // monster skill. Exerted attacks are not built at all: the buffs above are the rows that fully settle.
    public const ushort SeismicCryBuffRow = 1544, AncestralCryBuffRow = 1545,
        InfernalCryBuffRow = 1540, BattlemagesCryBuffRow = 1866;

    /// <summary>The warcry power a buff's values stop growing at (Enduring Cry's regeneration cap).</summary>
    public const int WarcryBuffPowerCap = 25;

    /// <summary>The BuffDefinitions row a warcry's own buff uses, or null when no buff is granted for it.</summary>
    public static ushort? WarcryBuffRow(string effectId) => effectId switch
    {
        "SeismicCry" => SeismicCryBuffRow,
        "AncestralCry" => AncestralCryBuffRow,
        "InfernalCry" => InfernalCryBuffRow,
        "BattlemagesCry" => BattlemagesCryBuffRow,
        _ => null,
    };

    /// <summary>A warcry buff's stats, in its BuffDefinitions row's GrantedStats order, or null when the warcry has
    /// no granted buff. <paramref name="own"/> reads the gem's own stats by their <see cref="Stat"/> id.</summary>
    public static (uint Key, int Value)[]? WarcryBuffStats(string effectId, int power, double buffEffect, Func<uint, int> own)
    {
        if (WarcryBuffRow(effectId) is null) return null;
        int capped = Math.Min(WarcryBuffPowerCap, power);
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
            _ => null,
        };
    }

    /// <summary>One running warcry buff: the stats captured at the release (in the row's GrantedStats order) and
    /// when it ends.</summary>
    public sealed class WarcryBuffState
    {
        public string? EffectId;
        public ushort Row;
        public double Until;
        public List<(uint Key, int Value)> Values = [];
        public bool ActiveAt(double now) => EffectId is not null && Until > now;
    }

    static readonly Store<Player, WarcryBuffState> WarcryBuffStates = new();

    public static WarcryBuffState WarcryBuffsOf(Player player) => WarcryBuffStates.GetOrCreateValue(player);

    /// <summary>Whether a warcry buff is running: the WarcryBuffs step's key (a re-grant moves the deadline, so the
    /// active flag alone is enough to rebuild on).</summary>
    public static bool WarcryBuffActive(Player player) =>
        player.PlayerLife > 0 && WarcryBuffsOf(player).ActiveAt(AreaTime(player));

    /// <summary>A running warcry buff's stats, for the WarcryBuffs step: empty when none runs.</summary>
    public static IReadOnlyList<(uint Key, int Value)> WarcryBuffStatsOf(Player player)
    {
        var state = WarcryBuffsOf(player);
        if (player.PlayerLife == 0 || !state.ActiveAt(AreaTime(player))) return [];
        return state.Values;
    }

    /// <summary>Grant (or refresh) a warcry's own buff at its release. A warcry with no granted buff is a no-op, so
    /// Rallying Cry, General's Cry and Vengeful Cry keep today's behaviour. A re-cry replaces the running buff with
    /// the new release's values, as a fresh cast does.</summary>
    public static void ApplyWarcryBuffLocked(Player player, string effectId, IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> playerStats, (int X, int Y) at, int radius, double now, double duration, double buffEffect)
    {
        if (duration <= 0 || WarcryBuffRow(effectId) is not ushort row) return;
        int power = playerStats.GetValueOrDefault(Stat.WarcriesHaveInfinitePower) != 0 ? InfiniteWarcryPower
            : player.Area.Monsters.Values.Where(m => m.Hostile && !m.Dead && m.Life > 0 && Dist(m.Walk.Position(now), at) <= radius)
                .Sum(m => WarcryPower(m.Rarity));
        if (WarcryBuffStats(effectId, power, buffEffect, key => own.GetValueOrDefault(key)) is not { } stats) return;
        var state = WarcryBuffsOf(player);
        state.EffectId = effectId;
        state.Row = row;
        state.Values = stats.ToList();
        state.Until = now + duration;
        // The area publishes the buff (0x1c1) and removes it when its countdown ends; a re-cry's new duration is a
        // new instance. The row is not IsRecovery, so no values ride the buff itself.
        player.Area.Buffs.Set(player.Id, new Buff(row, player.Id, U68: Buff.Seconds(duration)));
        MarkPlayerStats(player, PlayerStatStep.WarcryBuffs);
        Console.WriteLine($"[gateway] {effectId}: warcry buff row {row} power={power} for {duration:0.###} s");
    }

    public static async Task WarcryReleased(Player player,
        (int X, int Y) at, int radius, int epoch)
    {
        if (!player.ProjectileActive(epoch) || player.PlayerLife == 0) return;
        player.RecentEvents.Record(StatConditions.UsedWarcry, ProjectileWorldTime(player));
        var stats = AreaPlayerStats(player);
        int Get(uint key) => stats.GetValueOrDefault(key);
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
        m.Dead && m.Life == 0 && !m.CorpseConsumed && m.Variety?.LifeData.IsUsableAsCorpse(m.Life) == true;

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
                    int damage = MonsterTakes(m, hit, AreaRng(player), spell: false, now: now);
                    if (ApplyMonsterHitLocked(player, m, damage) is not { } applied) continue;
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
