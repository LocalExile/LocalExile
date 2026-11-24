using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string WolfKill = "SummonWolfOnKillUnique__1", WolfCritical = "SummonWolfOnCritUnique__1",
        SpiritKill = "SummonRagingSpiritOnKillUnique__1", SpiderKill = "SummonSpidersOnKillUnique__1",
        VolatileKill = "SummonVoidSphereOnKillUnique__1_", EssenceSpirits = "SpiritMinionEssence1",
        SoulwrestPhantasm = "TriggerSummonPhantasmOnCorpseConsumeUnique__1";
    const int SpiderCorpseRadius = 25;
    const string VoidSpawnFamily = "VoidSpawn";
    const double VoidSpawnInterval = 4;

    static readonly SummonCatalog.Definition ItemWolf = new("SummonSpectralWolf", "SpectralWolf",
        "Metadata/Monsters/Lion/SummonedSpectralWolf", Stat.NumberOfWolvesAllowed, 0);
    static readonly SummonCatalog.Definition ItemSpirit = new("AshwakerSummonRagingSpiritOnKill", "RagingSpirit",
        "Metadata/Monsters/SummonedSkull/SummonedSkull", Stat.BaseNumberOfRagingSpiritsAllowed, Stat.Of("active_raging_spirit_count"),
        Follows: false, Targetable: false);
    static readonly SummonCatalog.Definition ItemSpider = new("RaiseSpiders", "Spider",
        "Metadata/Monsters/SummonedSpider/SummonedSpider", Stat.NumberOfSpiderMinionsAllowed, 0, Targetable: false);

    static readonly SummonCatalog.Definition ItemVoidSpawn = new("SummonVoidSpawn", VoidSpawnFamily,
        "Metadata/Monsters/ElderTentacle/ElderTentacleMinionLargePlayer", Stat.BaseNumberOfVoidSpawnsAllowed, Stat.NumberOfActiveVoidSpawn);

    const string SpectralSpiritFamily = "SpectralSpirit";
    static readonly SummonCatalog.Definition ItemSpectralSpirit = new("SpectralSpirits", SpectralSpiritFamily,
        "Metadata/Monsters/SummonedSkull/SpectralSummonedSkull", Stat.BaseNumberOfEssenceSpiritsAllowed,
        Stat.NumberOfActiveEssenceSpirits, Targetable: false);
    static readonly SummonCatalog.Definition ItemSoulwrestPhantasm = PhantasmDefinition("TriggeredSummonGhostOnKill");

    static SummonCatalog.Definition? TriggeredItemMinion(WornGrantedSkills.Grant grant) => grant.ModId switch
    {
        WolfKill or WolfCritical when grant.EffectId == ItemWolf.Id => ItemWolf,
        SpiritKill when grant.EffectId == ItemSpirit.Id => ItemSpirit,
        SpiderKill when grant.EffectId == ItemSpider.Id => ItemSpider,
        VolatileKill when grant.EffectId == ItemVolatileAnomaly.Id => ItemVolatileAnomaly,
        EssenceSpirits when grant.EffectId == ItemSpectralSpirit.Id => ItemSpectralSpirit,
        SoulwrestPhantasm when grant.EffectId == ItemSoulwrestPhantasm.Id => ItemSoulwrestPhantasm,
        ("GrantsSummonVoidSpawnUnique__1" or "DivergentGrantsSummonVoidSpawnUnique__1")
            when grant.EffectId == ItemVoidSpawn.Id => ItemVoidSpawn,
        _ => null,
    };

    sealed record ItemMinionTrigger(ushort Skill, WornGrantedSkills.Grant Grant,
        SummonCatalog.Definition Definition, (int X, int Y) From, int Epoch, GameServer.Modding.IModuleLifetime? Rules);

    static readonly RuleTable<Player, Queue<ItemMinionTrigger>> WeaponMinionTriggers = new();

    public static void ItemMinionsOnKill(KillContext kill)
    {
        var player = kill.Player;
        if (!kill.PlayerKill || !kill.Monster.Hostile || player.PlayerLife == 0 || player.Closed || player.LeavingArea
            || !AllWornGrantedSkillsOf(player).Any(grant => grant.ModId is WolfKill or SpiritKill or SpiderKill or VolatileKill)) return;
        double now = AreaTime(player);
        foreach (var skill in GemSkillEffects(player))
        {
            var gem = SupportedSkill(player, skill.Id);
            if (!gem.RequirementsMet || gem.WornGrant is not { } grant || grant.ModId is not (WolfKill or SpiritKill or SpiderKill or VolatileKill)
                || TriggeredItemMinion(grant) is not { } definition) continue;
            bool volatileAnomaly = definition == ItemVolatileAnomaly;
            int chance = GolemStat(gem.Stats, volatileAnomaly ? Stat.ChanceToCastOnKillPct : Stat.ChanceToCastOnKillPctTargetSelf);
            if (!RandomRolls.Chance(chance, 100, AreaRng(player))) continue;
            ItemMinionTrigger trigger = new(skill.Id, grant, definition, volatileAnomaly ? kill.At : player.Walk.Position(now),
                player.ProjectileEpoch, CombatLifetime);
            kill.Later(() => FireItemMinionTrigger(player, trigger, now));
        }
    }

    static void ItemMinionsOnWeaponCriticalLocked(Player player, Item? weapon, SpawnedMonster target, double now)
    {
        if (weapon is null || !target.Hostile || player.PlayerLife == 0) return;
        var equipment = ItemsOf(player).EquipmentSnapshot();
        if (!equipment.Any(e => ReferenceEquals(e.Item.Item, weapon)
            && ItemModStats.Local(weapon, ItemsFor(player).Mods).GetValueOrDefault(Stat.LocalUseSkillOnCritPct) > 0)) return;
        var host = equipment.First(e => ReferenceEquals(e.Item.Item, weapon)).Item.Id;
        if (!AllWornGrantedSkillsOf(player, equipment).Any(grant => grant.Host == host && grant.ModId == WolfCritical)) return;
        foreach (var row in GemSkillEffects(player))
        {
            var gem = SupportedSkill(player, row.Id);
            if (!gem.RequirementsMet || gem.WornGrant is not { } grant || grant.Host != host || grant.ModId != WolfCritical
                || TriggeredItemMinion(grant) is not { } definition) continue;
            int chance = ItemModStats.Local(weapon, ItemsFor(player).Mods).GetValueOrDefault(Stat.LocalUseSkillOnCritPct);
            if (RandomRolls.Chance(chance, 100, AreaRng(player)))
                WeaponMinionTriggers.GetOrCreateValue(player).Enqueue(new(row.Id, grant, definition,
                    target.Walk.Position(now), player.ProjectileEpoch, CombatLifetime));
            return;
        }
    }

    sealed record VoidSpawnIntervalState(WornGrantedSkills.Grant Grant, int Epoch, double Next);
    static readonly RuleTable<Player, VoidSpawnIntervalState> VoidSpawnTimers = new();

    static void QueueSoulwrestPhantasmLocked(Player player, SpawnedMonster corpse, double now)
    {
        if (player.PlayerLife == 0 || !AllWornGrantedSkillsOf(player).Any(grant => TriggeredItemMinion(grant) == ItemSoulwrestPhantasm))
            return;
        foreach (var row in GemSkillEffects(player))
        {
            var gem = SupportedSkill(player, row.Id);
            if (!gem.RequirementsMet || gem.WornGrant is not { } grant || TriggeredItemMinion(grant) != ItemSoulwrestPhantasm)
                continue;
            if (RandomRolls.Chance(GolemStat(gem.Stats, Stat.TriggerOnCorpseConsumePctChance), 100, AreaRng(player)))
                WeaponMinionTriggers.GetOrCreateValue(player).Enqueue(new(row.Id, grant, ItemSoulwrestPhantasm,
                    corpse.Walk.Position(now), player.ProjectileEpoch, CombatLifetime));
        }
    }

    static async Task TickEquippedItemMinions(Player player, double now)
    {
        if (player.Closed || player.LeavingArea || player.Paused) return;
        if (player.PlayerLife == 0 || InTown(player))
        {
            VoidSpawnTimers.Remove(player);
            return;
        }
        var worn = AllWornGrantedSkillsOf(player);
        if (worn.Any(grant => TriggeredItemMinion(grant) == ItemVoidSpawn)) await TickVoidSpawnTimer(player, now);
        else VoidSpawnTimers.Remove(player);
        if (!worn.Any(grant => TriggeredItemMinion(grant) == ItemSpectralSpirit)
            || !DarkMonarchAllows(SpectralSpiritFamily, CharacterPlayerStats(player))) return;
        foreach (var row in GemSkillEffects(player))
        {
            var gem = SupportedSkill(player, row.Id);
            if (!gem.RequirementsMet || gem.WornGrant is not { } grant || TriggeredItemMinion(grant) != ItemSpectralSpirit) continue;
            int limit = GolemStat(gem.Stats, Stat.NumberOfMonstersToSummon);
            int active = player.Summons.Fighters.Values.Count(fighter => fighter.Definition.Kind == SpectralSpiritFamily
                && fighter.Source == row.Id && GolemCurrent(player, fighter) && !fighter.Monster.Dead && fighter.Monster.Life > 0);
            for (int i = active; i < limit; i++)
            {
                var trigger = new ItemMinionTrigger(row.Id, grant, ItemSpectralSpirit, NextToPlayer(player),
                    player.ProjectileEpoch, CombatLifetime);
                if (!await FireItemMinionTrigger(player, trigger, now)) break;
            }
        }
    }

    static async Task TickVoidSpawnTimer(Player player, double now)
    {
        if (player.PlayerLife == 0 || InTown(player) || player.Closed || player.LeavingArea || player.Paused)
        {
            VoidSpawnTimers.Remove(player);
            return;
        }
        foreach (var skill in GemSkillEffects(player))
        {
            var gem = SupportedSkill(player, skill.Id);
            if (!gem.RequirementsMet || gem.WornGrant is not { } grant || TriggeredItemMinion(grant) != ItemVoidSpawn) continue;
            if (!VoidSpawnTimers.TryGetValue(player, out var timer) || timer.Grant != grant || timer.Epoch != player.ProjectileEpoch)
            {
                VoidSpawnTimers.AddOrUpdate(player, new(grant, player.ProjectileEpoch, now + VoidSpawnInterval));
                return;
            }
            if (now < timer.Next) return;
            double elapsedTicks = 1 + Math.Floor((now - timer.Next) / VoidSpawnInterval);
            VoidSpawnTimers.AddOrUpdate(player, timer with { Next = timer.Next + elapsedTicks * VoidSpawnInterval });
            await FireItemMinionTrigger(player, new(skill.Id, grant, ItemVoidSpawn, player.Walk.Position(now),
                player.ProjectileEpoch, CombatLifetime), now);
            return;
        }
        VoidSpawnTimers.Remove(player);
    }

    static async Task TickWeaponMinionTriggers(Player player, double now)
    {
        if (!WeaponMinionTriggers.TryGetValue(player, out var triggers) || triggers.Count == 0 || player.Paused) return;
        var pending = triggers.ToArray();
        triggers.Clear();
        foreach (var trigger in pending) await FireItemMinionTrigger(player, trigger, now);
    }

    static async Task<bool> FireItemMinionTrigger(Player player, ItemMinionTrigger trigger, double now)
    {
        bool Current() => CurrentGameWork && ReferenceEquals(trigger.Rules, CombatLifetime)
            && player.ProjectileActive(trigger.Epoch) && !player.Paused && player.PlayerLife > 0
            && SupportedSkill(player, trigger.Skill) is { RequirementsMet: true, WornGrant: { } grant } && grant == trigger.Grant;
        if (!Current()) return false;
        var gem = SupportedSkill(player, trigger.Skill);
        if (GemSkillInstanceDisabled(player, gem.Effect, trigger.Skill, triggered: true) is not null) return false;
        var definition = trigger.Definition;
        int limit = SummonLimit(player, definition, gem);
        if (limit <= 0) return false;
        if (definition == ItemVolatileAnomaly)
            return UseItemMinionCooldown(player, trigger.Skill, gem, now)
                && await SummonFighterCore(player, trigger.Skill, definition, trigger.From, current: Current, triggered: true) != 0;
        var active = player.Summons.Fighters.Values.Where(f => f.Definition.Kind == definition.Family
            && !f.Monster.Dead && f.Monster.Life > 0 && GolemCurrent(player, f)).ToArray();

        if (definition == ItemWolf && active.Length >= limit)
        {
            var wolf = active.MinBy(f => player.Summons.Until[f.Id])!;
            double until = now + SummonDuration(player, definition, gem);
            player.Summons.Until[wolf.Id] = until;
            foreach (var minion in player.Minions.All.Where(m => m.Id == wolf.Id)) minion.Until = until;
            wolf.Monster.Life = wolf.Monster.MaxLife;
            player.Minions.OutputQueue.Add(new([], "spectral wolf refreshed", wolf.Id, wolf.Monster, PlayerMinions.OutputKind.Life));
            return true;
        }
        if (definition == ItemVoidSpawn && active.Length >= limit) return false;
        if (definition != ItemSpider)
            return await SummonFighterCore(player, trigger.Skill, definition, trigger.From, current: Current, triggered: true) != 0;

        int remaining = limit - active.Length;
        if (remaining <= 0) return false;
        var corpses = player.Area.Monsters
            .Where(pair => IsCorpse(pair.Value) && !pair.Value.Ailments.Shattered
                && Dist(pair.Value.Walk.Position(now), trigger.From) <= SpiderCorpseRadius)
            .OrderBy(pair => Dist(pair.Value.Walk.Position(now), trigger.From)).ThenBy(pair => pair.Key)
            .Take((remaining + 1) / 2).ToArray();
        if (corpses.Length == 0 || !UseItemMinionCooldown(player, trigger.Skill, gem, now)) return false;
        foreach (var (id, corpse) in corpses)
        {
            if (!Current() || remaining <= 0) break;
            if (!ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), corpse) || !IsCorpse(corpse)) continue;
            var at = corpse.Walk.Position(now);
            uint first = await SummonFighterCore(player, trigger.Skill, definition, at, current: Current, triggered: true,
                consumedCorpse: corpse);
            if (first == 0) continue;
            player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), "Raise Spiders consumes a corpse",
                WorldId: id, RemovesObject: true));
            if (--remaining > 0 && await SummonFighterCore(player, trigger.Skill, definition, at,
                    current: Current, triggered: true) != 0) remaining--;
        }
        return remaining < limit - active.Length;
    }

    static bool UseItemMinionCooldown(Player player, ushort skill, SupportedGem gem, double now)
    {
        if (CooldownForSkill(player, skill) is not { } cooldown) return true;
        int key = CooldownKey(player, gem.Effect, skill);
        var state = CooldownStateLocked(player, key, cooldown, now);
        if (!state.TryUse(now, cooldown)) return false;
        player.SkillReadyAt[key] = now + state.RemainingSeconds;
        state.ConfirmNativeUse(state.UseVersion, skill, synchronizeProgress: true);
        return true;
    }

    public static Dictionary<uint, int> ItemMinionStatSources(Player player, Dictionary<uint, int> sources)
    {
        if (player.Summons.Fighters.Count == 0) return sources;
        int voidSpawns = 0;
        foreach (var fighter in player.Summons.Fighters.Values)
        {
            if (fighter.Definition.Kind is not ("SpectralWolf" or "Spider" or "Spectre" or VoidSpawnFamily) || !GolemCurrent(player, fighter)
                || fighter.Monster.Dead || fighter.Monster.Life == 0) continue;
            if (fighter.Definition.Kind == "Spectre") AddSpectreOwnerBenefits(player, sources, fighter);
            else if (fighter.Definition.Kind == VoidSpawnFamily) voidSpawns++;
            else if (fighter.Definition.Kind == "SpectralWolf")
            {
                Add(Stat.SpectralWolfGrantsAttackMinimumAddedPhysicalDamage, Stat.AttackMinimumAddedPhysicalDamage, fighter);
                Add(Stat.SpectralWolfGrantsAttackMaximumAddedPhysicalDamage, Stat.AttackMaximumAddedPhysicalDamage, fighter);
            }
            else
            {
                Add(Stat.SummonedSpiderGrantsAttackSpeedPlusPct, Stat.AttackSpeedPlusPct, fighter);
                Add(Stat.SummonedSpiderGrantsPoisonDamagePlusPct, Stat.BasePoisonDamagePlusPct, fighter);
            }
        }
        if (voidSpawns > 0)
        {
            long extraChaos = (long)voidSpawns * sources.GetValueOrDefault(Stat.NonChaosDamagePctToAddAsChaosDamagePerVoidSpawn);
            if (extraChaos != 0) sources[Stat.NonChaosDamageToAddAsChaosDamagePct] =
                ActorScale.Saturate(sources.GetValueOrDefault(Stat.NonChaosDamageToAddAsChaosDamagePct) + extraChaos);
        }
        return sources;

        void Add(uint input, uint stat, GolemState.Golem fighter)
        {
            if (GolemStat(fighter.Own, input) is int value and not 0)
                sources[stat] = ActorScale.Saturate((long)sources.GetValueOrDefault(stat) + value);
        }
    }
}