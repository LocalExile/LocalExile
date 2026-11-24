using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string WolfKill = "SummonWolfOnKillUnique__1", WolfCritical = "SummonWolfOnCritUnique__1",
        SpiritKill = "SummonRagingSpiritOnKillUnique__1", SpiderKill = "SummonSpidersOnKillUnique__1";
    const int SpiderCorpseRadius = 25;

    static readonly SummonCatalog.Definition ItemWolf = new("SummonSpectralWolf", "SpectralWolf",
        "Metadata/Monsters/Lion/SummonedSpectralWolf", Stat.NumberOfWolvesAllowed, 0);
    static readonly SummonCatalog.Definition ItemSpirit = new("AshwakerSummonRagingSpiritOnKill", "RagingSpirit",
        "Metadata/Monsters/SummonedSkull/SummonedSkull", Stat.BaseNumberOfRagingSpiritsAllowed, Stat.Of("active_raging_spirit_count"),
        Follows: false, Targetable: false);
    static readonly SummonCatalog.Definition ItemSpider = new("RaiseSpiders", "Spider",
        "Metadata/Monsters/SummonedSpider/SummonedSpider", Stat.NumberOfSpiderMinionsAllowed, 0, Targetable: false);

    static SummonCatalog.Definition? TriggeredItemMinion(WornGrantedSkills.Grant grant) => grant.ModId switch
    {
        WolfKill or WolfCritical when grant.EffectId == ItemWolf.Id => ItemWolf,
        SpiritKill when grant.EffectId == ItemSpirit.Id => ItemSpirit,
        SpiderKill when grant.EffectId == ItemSpider.Id => ItemSpider,
        _ => null,
    };

    sealed record ItemMinionTrigger(ushort Skill, WornGrantedSkills.Grant Grant,
        SummonCatalog.Definition Definition, (int X, int Y) From, int Epoch, GameServer.Modding.IModuleLifetime? Rules);

    static readonly RuleTable<Player, Queue<ItemMinionTrigger>> WeaponMinionTriggers = new();

    public static void ItemMinionsOnKill(KillContext kill)
    {
        var player = kill.Player;
        if (!kill.PlayerKill || !kill.Monster.Hostile || player.PlayerLife == 0 || player.Closed || player.LeavingArea
            || !AllWornGrantedSkillsOf(player).Any(grant => grant.ModId is WolfKill or SpiritKill or SpiderKill)) return;
        double now = AreaTime(player);
        foreach (var skill in GemSkillEffects(player))
        {
            var gem = SupportedSkill(player, skill.Id);
            if (!gem.RequirementsMet || gem.WornGrant is not { } grant || grant.ModId == WolfCritical
                || TriggeredItemMinion(grant) is not { } definition) continue;
            int chance = GolemStat(gem.Stats, Stat.ChanceToCastOnKillPctTargetSelf);
            if (!RandomRolls.Chance(chance, 100, AreaRng(player))) continue;
            ItemMinionTrigger trigger = new(skill.Id, grant, definition, player.Walk.Position(now),
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

    static async Task TickWeaponMinionTriggers(Player player, double now)
    {
        if (!WeaponMinionTriggers.TryGetValue(player, out var triggers) || triggers.Count == 0 || player.Paused) return;
        var pending = triggers.ToArray();
        triggers.Clear();
        foreach (var trigger in pending) await FireItemMinionTrigger(player, trigger, now);
    }

    static async Task FireItemMinionTrigger(Player player, ItemMinionTrigger trigger, double now)
    {
        bool Current() => CurrentGameWork && ReferenceEquals(trigger.Rules, CombatLifetime)
            && player.ProjectileActive(trigger.Epoch) && !player.Paused && player.PlayerLife > 0
            && SupportedSkill(player, trigger.Skill) is { RequirementsMet: true, WornGrant: { } grant } && grant == trigger.Grant;
        if (!Current()) return;
        var gem = SupportedSkill(player, trigger.Skill);
        if (GemSkillInstanceDisabled(player, gem.Effect, trigger.Skill, triggered: true) is not null) return;
        var definition = trigger.Definition;
        int limit = SummonLimit(player, definition, gem);
        if (limit <= 0) return;
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
            return;
        }
        if (definition != ItemSpider)
        {
            await SummonFighterCore(player, trigger.Skill, definition, trigger.From, current: Current, triggered: true);
            return;
        }

        int remaining = limit - active.Length;
        if (remaining <= 0) return;
        var corpses = player.Area.Monsters
            .Where(pair => IsCorpse(pair.Value) && !pair.Value.Ailments.Shattered
                && Dist(pair.Value.Walk.Position(now), trigger.From) <= SpiderCorpseRadius)
            .OrderBy(pair => Dist(pair.Value.Walk.Position(now), trigger.From)).ThenBy(pair => pair.Key)
            .Take((remaining + 1) / 2).ToArray();
        if (corpses.Length == 0 || !UseItemMinionCooldown(player, trigger.Skill, gem, now)) return;
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
        foreach (var fighter in player.Summons.Fighters.Values)
        {
            if (fighter.Definition.Kind is not ("SpectralWolf" or "Spider") || !GolemCurrent(player, fighter)
                || fighter.Monster.Dead || fighter.Monster.Life == 0) continue;
            if (fighter.Definition.Kind == "SpectralWolf")
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
        return sources;

        void Add(uint input, uint stat, GolemState.Golem fighter)
        {
            if (GolemStat(fighter.Own, input) is int value and not 0)
                sources[stat] = ActorScale.Saturate((long)sources.GetValueOrDefault(stat) + value);
        }
    }
}