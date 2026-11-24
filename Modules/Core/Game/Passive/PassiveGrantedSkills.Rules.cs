using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadPassiveGrantedSkillsRulesData()
    {
        GrantedSkills = NewGrantedSkills(false);
    }

    static PassiveGrantedSkills GrantedSkills = default!;
    static PassiveGrantedSkills RuthlessGrantedSkills = default!;
    static PassiveGrantedSkills NewGrantedSkills(bool ruthless) => new(ruthless ? HardModeSkills : Skills, ruthless: ruthless);

    /// <summary>The skills the player's allocated passives grant whose node carries this trigger stat
    /// (<c>has_trigger_on_crit_by_unique_enemy</c> and the like; the level row's AdditionalFlags name the same trigger).
    /// A tattoo that replaces such a node (the Loyalty summons) is a grant of its own: its
    /// PassiveSkillOverrides.Effect and the has_trigger_on_* stat from TattooStats. Call in the area's frame.</summary>
    static IEnumerable<PassiveGrantedSkills.Grant> TriggeredGrantsLocked(Player player, uint key)
    {
        var passives = PassivesFor(player);
        var passiveGrants = (LeagueFor(player).HardMode ? RuthlessGrantedSkills : GrantedSkills)
            .For([.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated])
            .Where(g => passives.Find(g.Node)?.Stats.Any(s => s.Key == key && s.Value != 0) == true);
        return passiveGrants.Concat(TattooTriggeredGrantsLocked(player, key));
    }

    // Tattooed passives whose effect is a granted triggered skill: the tattoo's trigger stat comes from its
    // TattooStats row and the skill from the client's PassiveSkillOverrides.Effect (a GrantedEffectsPerLevel row).
    internal static IEnumerable<PassiveGrantedSkills.Grant> TattooTriggeredGrantsLocked(Player player, uint triggerKey)
    {
        var skills = SkillsFor(player);
        var tree = PassivesFor(player);
        Dictionary<ushort, string> tattoos;
        ushort[] allocated;
        tattoos = new(player.Character.Tattoos);
        allocated = [.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated];
        // Tattoos are saved apart from allocation, so refunding the node must remove its trigger: only allocated nodes
        // count. A timeless jewel that replaces an allocated node disables its tattoo in the stat pass; its trigger
        // stops with it, while an augmenting or unaffected node in the radius keeps its tattoo and trigger.
        var allocatedNodes = allocated.ToHashSet();
        var replaced = new HashSet<ushort>();
        var catalog = ItemsFor(player);
        foreach (var jewel in ItemsOf(player).Items(PlayerInventories.PassiveJewels).OrderBy(i => i.X))
        {
            if (tree.JewelNodeForSlot(jewel.X) is not { } socket || !allocatedNodes.Contains(socket)) continue;
            if (TimelessJewelCatalog.Read(jewel.Item, catalog.Mods) is not { } spec) continue;
            var inRadius = tree.NodesInRadius(socket, spec.Radius).ToHashSet();
            foreach (ushort node in allocatedNodes)
                if (inRadius.Contains(node) && tree.Find(node) is { } passive
                    && TimelessJewels.Alternate(passive, spec, node) is { Replaces: true })
                    replaced.Add(node);
            break;
        }
        foreach (var (node, name) in tattoos)
        {
            if (!allocatedNodes.Contains(node) || replaced.Contains(node)) continue;
            if (TattooCatalogue.Find(name) is not { GrantsSkill: true } entry) continue;
            if (TattooStats.For(name)?.Any(s => s.Key == triggerKey && s.Value != 0) != true) continue;
            if (skills.EffectsPerLevel(entry.Effect) is not { } level || skills.EffectId(level.Effect) is not { } effectId) continue;
            yield return new(node, entry.Effect, level.Effect, effectId, level.Level);
        }
    }

    // The skills the player's allocated passives grant, as virtual gem sources after the socketed gems
    // (GemSkillEffectsAt, SupportedSkillAt): same id rules, the granted level, quality 0, no supports.
    // shortcut: only self auras (AuraCatalog) have an executor; a granted triggered, minion or other skill stays out of
    // the skill list until its mechanic exists (the client would offer a skill that does nothing).
    static IReadOnlyList<PassiveGrantedSkills.Grant> GrantedSkillsOf(Player player)
    {
        ushort[] nodes;
        nodes = [.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated];
        return [.. (LeagueFor(player).HardMode ? RuthlessGrantedSkills : GrantedSkills).For(nodes)
            .Where(g => AuraCatalog.Supported(g.EffectId) || IsRelicSkill(g.EffectId)
                || GameEvents.GrantedSkillExecutable.For(player).Any(accept => accept(player, g.EffectId)))];
    }

    // A granted triggered skill (InbuiltTrigger) has no bar use: it runs from its trigger and stays out of the skill list.
    // Summon Elemental Relic (GEPL 78412 at level 20: Paladin's Elemental Valiance and the Guardian's Unwavering Crusade,
    // the same buff and grant) is the one with an executor (PlayerMinions.cs).
    static bool IsRelicSkill(string effectId) => effectId is "SummonElementalRelic" or "SummonElementalRelicHardMode";

    sealed class RelicTriggerCache
    {
        public int PassivesCount = -1;
        public int AscendancyCount = -1;
        public int NodeHash;
        public bool HardMode;
        public (PassiveGrantedSkills.Grant Grant, int AllyRadius)? Result;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, RelicTriggerCache> RelicTriggerCaches = new();

    /// <summary>The relic skill the player's passives grant and the radius of the buff (BuffTemplates 546, the player
    /// and his allies within 60 cells) whose can_trigger_summon_elemental_relic_on_nearby_ally_kill_or_on_hit_rare_or_unique
    /// lets their kills and hits trigger it; null without both.</summary>
    static (PassiveGrantedSkills.Grant Grant, int AllyRadius)? RelicTriggerOf(Player player)
    {
        var passives = player.Character.Passives;
        var ascendancy = player.Character.MainAscendancy.Allocated;
        if (passives.Count == 0 && ascendancy.Count == 0) return null;
        bool hardMode = LeagueFor(player).HardMode;
        int hash = 17;
        foreach (ushort n in passives) hash = unchecked(hash * 31 + n);
        foreach (ushort n in ascendancy) hash = unchecked(hash * 31 + n);
        var cache = RelicTriggerCaches.GetOrCreateValue(player);
        if (cache.PassivesCount == passives.Count && cache.AscendancyCount == ascendancy.Count
            && cache.NodeHash == hash && cache.HardMode == hardMode)
            return cache.Result;
        ushort[] nodes = [.. passives, .. ascendancy];
        uint? enables = Stat.CanTriggerSummonElementalRelicOnNearbyAllyKillOrOnHitRareOrUnique;
        (PassiveGrantedSkills.Grant Grant, int AllyRadius)? result = null;
        if (PassiveBuffAuras.AllyAurasFor(nodes, hardMode).FirstOrDefault(a => a.Stats.Any(s => s.Key == enables && s.Value != 0))
            is { } buff)
        {
            var grant = (hardMode ? RuthlessGrantedSkills : GrantedSkills).For(nodes).FirstOrDefault(g => IsRelicSkill(g.EffectId));
            if (grant.Node != 0) result = (grant, buff.Radius);
        }
        cache.PassivesCount = passives.Count;
        cache.AscendancyCount = ascendancy.Count;
        cache.NodeHash = hash;
        cache.HardMode = hardMode;
        cache.Result = result;
        return result;
    }

    /// <summary>
    /// A hit or kill that may trigger Summon Elemental Relic, in the area's frame: a kill, and a hit with damage on a
    /// rare or unique monster, each roll the skill's skill_triggered_by_nearby_allies_kill_or_hit_rare_unique_%_chance (25)
    /// once, by the living player or by his minion at <paramref name="allyAt"/> within the buff's radius of him. A success
    /// spends the skill's 0.3 s cooldown (with cooldown recovery; a roll while it recovers is not made) and queues the
    /// summon for the minion step. True when one was queued.
    /// </summary>
    public static bool TriggerElementalRelicLocked(Player player, SpawnedMonster target, bool hit, bool killed,
        (int X, int Y)? allyAt = null)
    {
        if (!CurrentGameWork || !target.Hostile || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return false;
        bool queued = false;
        if (hit && target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique) queued = Roll();
        if (killed) queued |= Roll();
        return queued;

        bool Roll()
        {
            if (RelicTriggerOf(player) is not { } relic) return false;
            var (grant, radius) = relic;
            double now = AreaTime(player);
            if (allyAt is { } ally && Dist(ally, player.Walk.Position(now)) > radius) return false;
            if (now < player.SkillReadyAt.GetValueOrDefault(grant.Effect) - 1e-9) return false;
            var own = SkillsFor(player).EffectStats(grant.Effect, grant.Level).ToDictionary(s => s.Item1, s => s.Item2);
            int chance = Stat.SkillTriggeredByNearbyAlliesKillOrHitRareUniquePctChance is var c ? own.GetValueOrDefault(c) : 0;
            if (player.Minions.Random.Next(100) >= chance) return false;
            if (SingleUseCooldown.ForSkill(SkillsFor(player), Stats, grant.Effect, grant.Level, CharacterPlayerStats(player), own)
                is not { } plan) return false;
            var state = CooldownStateLocked(player, grant.Effect, plan, now);
            if (!state.TryUse(now, plan)) return false;
            player.SkillReadyAt[grant.Effect] = now + state.RemainingSeconds;
            player.Minions.PendingRelics++;
            return true;
        }
    }

    // A granted skill resolved like a gem: its own stats at the granted level and the aura markers SupportedSkillAt
    // adds. No EffectiveGemLevel or GemQualityBonus: those raise gems.
    static SupportedGem GrantedSupportedSkill(Player player, PassiveGrantedSkills.Grant grant)
    {
        var own = SkillsFor(player).EffectStats(grant.Effect, grant.Level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        if (SkillsFor(player).IsAura(grant.Effect)) own[OwnAuraRestriction.BaseAura] = 1;
        own[OwnAuraRestriction.Aura] = OwnAuraRestriction.IsAura(own) ? 1 : 0;
        return new(grant.Effect, grant.Level, 0, [], own, Granted: true);
    }

    sealed class FootstepTimer { public bool Has, Running; public double Last = double.NaN; }
    static readonly RuleTable<PlayerUpdatesState, FootstepTimer> FootstepTimers = new();

    // The player's footstep skills (Abberath's Fury): every frame of the area moves the run animation's clock and
    // triggers each skill once per footstep it passed, while the character has a footstep skill. A stats update (the
    // passives changed) starts it again (WakeFootsteps). A pause stops the area's frames, so the clock stands still.
    static void WakeFootsteps(Player player)
    {
        if (player.PlayerUpdatesState is not PlayerUpdatesState updates || !PlayerUpdatesCurrent(player, updates)) return;
        List<ushort> nodes;
        nodes = [.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated];
        bool has = (LeagueFor(player).HardMode ? RuthlessGrantedSkills : GrantedSkills).For(nodes)
            .Any(s => PassiveGrantedSkills.IsFootstepSkill(s.EffectId));
        var timer = FootstepTimers.GetOrCreateValue(updates);
        lock (timer)
        {
            timer.Has = has;
            if (!has || timer.Running) return;
            timer.Running = true;
            timer.Last = updates.Schedule.Now(player);
        }
        EveryFrameAsPlayer(player, () =>
        {
            lock (timer)
            {
                if (timer.Has && PlayerUpdatesCurrent(player, updates)) return true;
                timer.Running = false;
                return false;
            }
        }, async player =>
        {
            double now = updates.Schedule.Now(player);
            int steps;
            if (!PlayerUpdatesCurrent(player, updates) || player.Paused || now <= timer.Last) return;
            steps = FootstepsDue(player, ref player.FootstepRun, timer.Last, now);
            timer.Last = now;
            if (steps == 0) return;
            foreach (var skill in FootstepSkills(player, now))
                for (int i = 0; i < steps && PlayerUpdatesCurrent(player, updates); i++)
                    await TriggerFootstepSkill(player, skill);
        });
    }

    // One poll of the run animation's clock, from → now, and the footsteps it passed. The clock runs while the player walks
    // (until Walk.ArrivesAt; a movement skill's blink is no walk) at the walk's speed over the base speed, so movement speed
    // and chill change the step rate (the wiki: movement speed raises the frequency). It starts over when the walk ends, as
    // the run animation does.
    // Shield Charge walks its charge (ShieldCharge.cs), so it takes steps at the charge speed, as the wiki says.
    public static int FootstepsDue(Player player, ref double run, double from, double now)
    {
        double before = run, until = Math.Min(now, player.Walk.ArrivesAt);
        if (until > from) run += (until - from) * player.Walk.Speed / WalkSpeed;
        int steps = PassiveGrantedSkills.Footsteps(before, run);
        if (now >= player.Walk.ArrivesAt) run = 0;
        return steps;
    }

    // The footstep skills that trigger now: none while the player is dead, leaving, frozen or stunned, in town or in the
    // grace period, nor a disabled one (Devout of Gruthkul's "Your Spells are disabled": the wiki says it disables Abberath's
    // Fury).
    public static IReadOnlyList<PassiveGrantedSkills.Grant> FootstepSkills(Player player, double now)
    {
        List<ushort> nodes;
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0 || PlayerActionBlocked(player, now, now)) return [];
        nodes = [.. player.Character.Passives, .. player.Character.MainAscendancy.Allocated];
        var skills = (LeagueFor(player).HardMode ? RuthlessGrantedSkills : GrantedSkills).For(nodes).Where(s => PassiveGrantedSkills.IsFootstepSkill(s.EffectId)).ToList();
        if (skills.Count == 0 || InTown(player) || InGracePeriod(player)) return [];
        return [.. skills.Where(s => ActorSkillDisabledCore(player, s.Effect, null) is null)];
    }

    // A granted spell's hit (no gem, no supports): its base damage with the player's increases, added damage and conversion,
    // as a supported spell's (SupportedHitFor); Spell Skills deal no Damage zeroes it (SkillDamageContext).
    public static DamagePacket GrantedSpellHit(Player player, int effect, int level) => GrantedSpell(player, effect, level).Hit;

    // The hit, and what its ignite takes: the flat damage (base and added, after conversion, before any increase; wiki
    // Ignite), the caster's and the skill's stats and the damage context.
    public static (DamagePacket Hit, DamagePacket Flat, Dictionary<uint, int> Stats, SkillDamageContext Context) GrantedSpell(
        Player player, int effect, int level)
    {
        var caster = FinalPlayerStats(player).ToDictionary(v => v.Item1, v => v.Item2);
        var stats = MergeSkillStats(caster, SkillsFor(player).EffectStats(effect, level).ToDictionary(v => v.Item1, v => v.Item2));
        int Get(uint k) => stats.GetValueOrDefault(k);
        var plan = SkillHitPlan.ForSkill(SkillsFor(player), effect, level);
        var context = new SkillDamageContext(plan.Kind, caster);
        if (!plan.CanHit) return (DamagePacket.Empty, DamagePacket.Empty, stats, context);
        var conversion = DamageStats.Conversion(stats, Stats, attack: false);
        double effectiveness = AddedEffectiveness(player, effect, level);
        var hit = SpellDamage.Packet(plan.SpellBase, Get, effectiveness).Convert(conversion);
        var flat = DamageStats.RawAdded(plan.SpellBase, Get, spell: true, effectiveness: effectiveness).Convert(conversion);
        return (context.Hit(hit), flat, stats, context);
    }

    // One footstep's skill (Abberath's Fury): its explosion art where the player stands, and a spell hit on every living
    // hostile monster within its radius (FootstepExplosionRadius grown by area of effect, SkillArea) of the player, as an
    // area spell's sweep (AreaSkillHit) without a gem: no cost, no cast time, crits and rolls per monster, every hit before
    // the sweep's deaths. A landed hit ignites a surviving monster with its 25% chance or a critical strike (IgniteFromHit).
    public static async Task TriggerFootstepSkill(Player player,
        PassiveGrantedSkills.Grant skill)
    {
        var rules = CombatLifetime; var source = AscendancyPolicies.SourceOwner(player);
        bool CurrentSource() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules) && source?.IsActive != false
            && ReferenceEquals(AscendancyPolicies.SourceOwner(player), source);
        if (!CurrentSource() || player.Closed || player.LeavingArea || player.PlayerLife == 0
            || !player.Character.Passives.Contains(skill.Node) && !player.Character.MainAscendancy.Allocated.Contains(skill.Node)) return;
        var own = SkillsFor(player).EffectStats(skill.Effect, skill.Level).ToDictionary(v => v.Item1, v => v.Item2);
        var area = SkillAreaStats(player, own, skill.Effect);
        int radius = SkillArea.Radius(PassiveGrantedSkills.FootstepExplosionRadius,
            name => name is var k ? area.GetValueOrDefault(k) : 0, spell: true);
        string? art = Art.Animated(PassiveGrantedSkills.ExplosionArtVariation, 0);
        (int X, int Y) at;
        uint? effectObject = null;
        List<(uint Id, SpawnedMonster M)> targets;
        using (ProjectileMutation(player))
        {
            if (!CurrentSource() || player.Closed || player.LeavingArea || player.PlayerLife == 0 || player.Paused) return;
            double now = ProjectileWorldTime(player);
            at = player.Walk.Position(now);
            targets = [.. player.Area.MonstersNear(at, radius).Where(e => e.Value is { Hostile: true, Dead: false, Life: > 0 }
                && Dist(e.Value.Walk.Position(now), at) <= radius).Select(e => (e.Key, e.Value))];
            if (art is not null) effectObject = player.TakeObjectId();
        }
        if (effectObject is uint obj)
            await SendWorldCreate(player, obj, () =>
            {
                if (!CurrentSource() || player.Closed || player.LeavingArea || player.PlayerLife == 0) return null;
                AddTemporarySkillEffect(player, obj, PassiveGrantedSkills.ExplosionArtMs / 1000.0);
                return InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                    new EffectObject(at.X, at.Y, art!).Blob());
            }, wake: true);
        if (!CurrentSource() || targets.Count == 0) return;

        using (ProjectileMutation(player))
        {
            // Revalidate the actual grant after visual output. A refunded/retired source cannot admit a stale plan.
            if (!CurrentSource() || player.Closed || player.LeavingArea
                || player.RageRetired || player.PlayerLife == 0 || player.Paused || !FootstepSkills(player, ProjectileWorldTime(player)).Contains(skill)) return;
            using var statScope = new PlayerStatScope(player, fresh: true);
            var playerStats = FinalPlayerStats(player).ToDictionary(v => v.Item1, v => v.Item2);
            int Player(uint k) => playerStats.GetValueOrDefault(k);
            int Own(uint k) => own.GetValueOrDefault(k);
            var merged = MergeSkillStats(playerStats, own);
            // Elemental Overload's per-skill window for this granted spell (see WithElementalOverload).
            AddElementalOverload(player, skill.EffectId, merged);
            int Merged(uint k) => merged.GetValueOrDefault(k);
            var plan = SkillHitPlan.ForSkill(SkillsFor(player), skill.Effect, skill.Level);
            if (!plan.CanHit) return;
            var context = new SkillDamageContext(plan.Kind, playerStats);
            var raw = DamageStats.RawAdded(plan.SpellBase, Merged, spell: true,
                effectiveness: AddedEffectiveness(player, skill.Effect, skill.Level));
            var conversion = DamageStats.Conversion(merged, Stats, attack: false);
            var types = Supports.Of(skill.Effect)?.Types.ToHashSet();
            long additionalIncreased = PlayerHitDamage.VsBlindedDamageStat is var damageKey
                ? (long)playerStats.GetValueOrDefault(damageKey) + own.GetValueOrDefault(damageKey) : 0;
            int staffSpell = Merged(Stat.SpellStaffDamagePlusPct) is int staff and not 0
                && WeaponTypeDamage.FamilyOf(WeaponFor(player).ItemClass) == "staff" ? staff : 0;
            AreaSkillDamageProfile Profile(long increased, int webs = 0)
            {
                increased += SpiderDamageIncrease(Merged, webs);
                var woven = SpiderRawDamage(raw, Merged, webs, AddedEffectiveness(player, skill.Effect, skill.Level));
                var hit = context.Hit(PlayerHitDamage.Resolve(woven, Merged, conversion, spell: true,
                    area: Merged(Stat.IsAreaDamage) != 0, skillTypes: types, additionalIncreased: increased + staffSpell));
                var rows = AilmentPanelStats.Compute(new("spell", Spell: true), woven, conversion, Merged, increased);
                var ignite = MonsterIgnites.Settings.From(rows, "spell", Merged);
                var poison = MonsterPoisons.Settings.From(rows, "spell", Merged);
                double fireFactor = context.OverTime(new(DamageType.Fire, 1, 1)).DamagePerSecond;
                double chaosFactor = context.OverTime(new(DamageType.Chaos, 1, 1)).DamagePerSecond;
                return new(hit,
                    ignite with { Min = ignite.Min * fireFactor, Max = ignite.Max * fireFactor,
                        CriticalMin = ignite.CriticalMin * fireFactor, CriticalMax = ignite.CriticalMax * fireFactor },
                    MonsterIgnites.Settings.None,
                    poison with { Min = poison.Min * chaosFactor, Max = poison.Max * chaosFactor,
                        CriticalMin = poison.CriticalMin * chaosFactor, CriticalMax = poison.CriticalMax * chaosFactor });
            }
            var normalProfile = Profile(0);
            var blindedProfile = additionalIncreased == 0 ? normalProfile : Profile(additionalIncreased);
            var pen = Penetration.Of(playerStats, Stats, attack: false, own);
            var mods = HitResistMods(playerStats, own);
            int baseChance = (SkillsFor(player).SpellCritChance(skill.Effect, skill.Level) ?? 0) + Own(Stat.AdditionalBaseCriticalStrikeChance);
            int chance = SpellCriticalStrike.Chance(baseChance, Player, Own, player.Pools.SpellCriticalStrikeChancePct);
            int? vsBlinded = Player(VsBlindedCriticalStat) is not 0 and var extra
                ? SpellCriticalStrike.Chance(baseChance, Player, Own, player.Pools.SpellCriticalStrikeChancePct + (long)extra) : null;
            var vsProfane = SpellCriticalChanceVsGround(player,
                extra => SpellCriticalStrike.Chance(baseChance, Player, Own, player.Pools.SpellCriticalStrikeChancePct + extra),
                Player(VsBlindedCriticalStat));
            int multiplier = CriticalHitMultiplier((long)player.Pools.SpellCriticalStrikeMultiplier
                + Own(Stat.BaseCriticalStrikeMultiplierPlus), playerStats, own);
            var gambler = GamblerFor(playerStats);
            bool rageGranted = false;
            var deaths = new List<(uint Id, SpawnedMonster M)>();
            var charges = new List<PlayerMinions.AcceptedCharges>();
            var reflected = new List<MonsterElementalAilments.Effect>();
            foreach (var (id, m) in targets)
            {
                // Retirement stops new contacts, while the transaction still completes its accepted hits/deaths below.
                if (!CurrentSource() || player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0 || player.Paused) break;
                if (!player.Area.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)
                    || !m.Hostile || m.Dead || m.Life == 0 || m.Invulnerable || m.Asleep
                    || MonsterAsleep(player, id)) continue;
                var profile = m.SpiderWebs.Count(ProjectileWorldTime(player)) is > 0 and var webs
                    ? Profile(Blinded(player, id) ? additionalIncreased : 0, webs)
                    : Blinded(player, id) ? blindedProfile : normalProfile;
                if (MonsterBlocks(m, AreaRng(player), spell: true)) continue;
                var rolled = (gambler with { DamageMorePct = 0 }).ForSkill(Own, spell: true).RollOutgoingHit(profile.Hit, AreaRng(player));
                bool critical = RollCriticalStrike(player, CriticalChanceOn(player, id, chance, vsBlinded, vsProfane), own, attack: false);
                double hitAt = ProjectileWorldTime(player);
                var (damage, _, mitigated) = MonsterHitDamage(m, rolled, critical, attack: false, hitAt, mods, pen,
                    gambler.InvertsResistance(AreaRng(player)), AreaRng(player));
                if (critical) damage = DamageModel.CriticalDamage(damage, CriticalMultiplierOn(m, multiplier, playerStats, own));
                int chaosDamage = MonsterHitChaosDamage(m, mitigated, spell: true, hitAt, damage);
                var stun = OwnerStun(player, m, damage, mitigated, melee: false, spell: true, critical, playerStats, own);
                var applied = ApplyMonsterHitLocked(player, m, damage, null, null, null, default, null, chaosDamage);
                if (applied is { StageStarts: true } staged)
                {
                    CommitBossStage(player, id, m, ProjectileWorldTime(player));
                    applied = staged with { StageStarts = false };
                }
                if (applied is not null)
                    AilmentsOnHitLocked(player, id, m, mitigated, spell: true, critical, multiplier, playerStats, own, hitAt,
                        AreaRng(player), reflected, poison: profile.Poison, blind: BlindHitInput.Capture(playerStats, own));
                if (applied is not null && !rageGranted) rageGranted = RageOnHitLocked(player, attack: false, melee: false, critical, own);
                if (applied is not null && m.Life > 0 && rolled[DamageType.Fire].Max > 0)
                    foreach (var packet in IgniteOnHitLocked(player, id, m, profile.Ignite, critical, hitAt) ?? [])
                        player.Minions.OutputQueue.Add(new(packet, $"0x01c1/0x01c2 monster 0x{id:x} ignited by {skill.EffectId}", id, m));

                if (applied is not { } landed) continue;
                player.RecentEvents.RecordCriticalOutcome(hitAt, critical);
                if (critical && HasElementalOverload(playerStats))
                    player.RecentEvents.RecordSkillCrit(skill.EffectId, AreaTime(player), true);
                if (critical)
                {
                    CommitAcceptedCritFlasksLocked(player, playerStats, AreaTime(player));
                    MaybeCreateProfaneGroundLocked(player, m.Walk.Position(hitAt), playerStats);
                }
                CommitAcceptedHitLocked(player, id, m, landed, damage, hitAt,
                    $"{skill.EffectId} (footstep){(critical ? " (critical)" : "")}", playerKill: true, deferKill: true, stun: stun);
                if (m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique) QueueFlaskTrigger(player, FlaskEnchantments.Trigger.RareHit);
                charges.Add(new(false, critical, m.Rarity, skill.EffectId, own, id == targets[0].Id, hitAt, landed.Life == 0));
                if (landed.Life == 0) deaths.Add((id, m));
            }
            // All accepted hit consequences and reflected ailments precede the sweep's attributed kills.
            if (reflected.Count > 0) ReflectAilmentsLocked(player, reflected, ProjectileWorldTime(player), playerStats, AreaRng(player));
            foreach (var (id, m) in deaths) CommitAcceptedKillLocked(player, id, m, ProjectileWorldTime(player), playerKill: true, charges);
            if (charges.Count > 0) CommitAcceptedChargeBatchLocked(player, charges, playerStats);
        }
        await PublishMinionOutcomesFor(player, rules);
    }

    // The current footstep role removes accepted temporary art at its pause-clock deadline, including
    // art created before replacement. Permanent connection state holds only object ids and deadlines.
    // Polled every 50 ms: with no temporary art at all there is nothing to remove, so the send gate and the area's frame
    // are left alone (a racy read of the count only delays a removal to the next poll).
    /// <summary>A short-lived effect object the player is shown, removed after <paramref name="seconds"/>. Under the
    /// Monsters lock.</summary>
    public static void AddTemporarySkillEffect(Player player, uint id, double seconds)
    {
        player.TemporarySkillEffects[id] = AreaTime(player) + seconds;
        AfterAsPlayer(player, seconds, SendTemporarySkillEffects);
    }

    public static Task SendTemporarySkillEffects(Player player) =>
        player.TemporarySkillEffects.Count == 0 ? Task.CompletedTask : SendWorldBatch(player, [], admission =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return null;
            double now = AreaTime(player);
            var due = player.TemporarySkillEffects.Where(p => p.Value <= now).Select(p => p.Key).ToArray();
            if (due.Length == 0) return null;
            foreach (uint id in due) player.TemporarySkillEffects.Remove(id);
            var records = new WorldRecordBuilder(player, admission);
            foreach (uint id in due) records.Remove(id);
            return records.Build();
        });
}
