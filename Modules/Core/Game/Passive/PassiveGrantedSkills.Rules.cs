using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    static readonly Lazy<PassiveGrantedSkills> GrantedSkills = new(() => NewGrantedSkills(false));
    static readonly Lazy<PassiveGrantedSkills> RuthlessGrantedSkills = new(() => NewGrantedSkills(true));
    static PassiveGrantedSkills NewGrantedSkills(bool ruthless) => new(ruthless ? HardModeSkills.Value : Skills, ruthless: ruthless);

    /// <summary>The skills the player's allocated passives grant whose node carries this trigger stat
    /// (<c>has_trigger_on_crit_by_unique_enemy</c> and the like; the level row's AdditionalFlags name the same trigger).
    /// A tattoo that replaces such a node (the Loyalty summons) is a grant of its own: its
    /// PassiveSkillOverrides.Effect and the has_trigger_on_* stat from TattooStats. Call under the Monsters lock.</summary>
    static IEnumerable<PassiveGrantedSkills.Grant> TriggeredGrantsLocked(ClientState conn, string trigger)
    {
        if (Stats.Key(trigger) is not uint key) return [];
        var passives = PassivesFor(conn);
        var passiveGrants = (LeagueFor(conn).HardMode ? RuthlessGrantedSkills : GrantedSkills).Value
            .For([.. conn.Passives, .. conn.MainAscendancy.Allocated])
            .Where(g => passives.Find(g.Node)?.Stats.Any(s => s.Key == key && s.Value != 0) == true);
        return passiveGrants.Concat(TattooTriggeredGrantsLocked(conn, key));
    }

    // Tattooed passives whose effect is a granted triggered skill: the tattoo's trigger stat comes from its
    // TattooStats row and the skill from the client's PassiveSkillOverrides.Effect (a GrantedEffectsPerLevel row).
    internal static IEnumerable<PassiveGrantedSkills.Grant> TattooTriggeredGrantsLocked(ClientState conn, uint triggerKey)
    {
        var skills = SkillsFor(conn);
        var tree = PassivesFor(conn);
        Dictionary<ushort, string> tattoos;
        ushort[] allocated;
        lock (conn.Monsters)
        {
            tattoos = new(conn.Tattoos);
            allocated = [.. conn.Passives, .. conn.MainAscendancy.Allocated];
        }
        // Tattoos are saved apart from allocation, so refunding the node must remove its trigger: only allocated nodes
        // count. A timeless jewel that replaces an allocated node disables its tattoo in the stat pass; its trigger
        // stops with it.
        var allocatedNodes = allocated.ToHashSet();
        var replaced = new HashSet<ushort>();
        var catalog = ItemsFor(conn);
        foreach (var jewel in ItemsOf(conn).Items(PlayerInventories.PassiveJewels).OrderBy(i => i.X))
        {
            if (tree.JewelNodeForSlot(jewel.X) is not { } socket || !allocatedNodes.Contains(socket)) continue;
            if (TimelessJewelCatalog.Read(jewel.Item, catalog.Mods) is not { } spec) continue;
            replaced = [.. tree.NodesInRadius(socket, spec.Radius)];
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
    static IReadOnlyList<PassiveGrantedSkills.Grant> GrantedSkillsOf(ClientState conn)
    {
        ushort[] nodes;
        lock (conn.Monsters) nodes = [.. conn.Passives, .. conn.MainAscendancy.Allocated];
        return [.. (LeagueFor(conn).HardMode ? RuthlessGrantedSkills : GrantedSkills).Value.For(nodes)
            .Where(g => AuraCatalog.Supported(g.EffectId)
                || GameEvents.GrantedSkillExecutable.For(conn).Any(accept => accept(conn, g.EffectId)))];
    }

    // A granted triggered skill (InbuiltTrigger) has no bar use: it runs from its trigger and stays out of the skill list.
    // Summon Elemental Relic (GEPL 78412 at level 20: Paladin's Elemental Valiance and the Guardian's Unwavering Crusade,
    // the same buff and grant) is the one with an executor (PlayerMinions.cs).
    static bool IsRelicSkill(string effectId) => effectId is "SummonElementalRelic" or "SummonElementalRelicHardMode";

    /// <summary>The relic skill the player's passives grant and the radius of the buff (BuffTemplates 546, the player
    /// and his allies within 60 cells) whose can_trigger_summon_elemental_relic_on_nearby_ally_kill_or_on_hit_rare_or_unique
    /// lets their kills and hits trigger it; null without both.</summary>
    static (PassiveGrantedSkills.Grant Grant, int AllyRadius)? RelicTriggerOf(ClientState conn)
    {
        ushort[] nodes;
        lock (conn.Monsters) nodes = [.. conn.Passives, .. conn.MainAscendancy.Allocated];
        uint? enables = Stats.Key("can_trigger_summon_elemental_relic_on_nearby_ally_kill_or_on_hit_rare_or_unique");
        if (PassiveBuffAuras.AllyAurasFor(nodes, LeagueFor(conn).HardMode).FirstOrDefault(a => a.Stats.Any(s => s.Key == enables && s.Value != 0))
            is not { } buff) return null;
        var grant = (LeagueFor(conn).HardMode ? RuthlessGrantedSkills : GrantedSkills).Value.For(nodes).FirstOrDefault(g => IsRelicSkill(g.EffectId));
        return grant.Node == 0 ? null : (grant, buff.Radius);
    }

    /// <summary>
    /// A hit or kill that may trigger Summon Elemental Relic, under the Monsters lock: a kill, and a hit with damage on a
    /// rare or unique monster, each roll the skill's skill_triggered_by_nearby_allies_kill_or_hit_rare_unique_%_chance (25)
    /// once, by the living player or by his minion at <paramref name="allyAt"/> within the buff's radius of him. A success
    /// spends the skill's 0.3 s cooldown (with cooldown recovery; a roll while it recovers is not made) and queues the
    /// summon for the minion step. True when one was queued.
    /// </summary>
    public static bool TriggerElementalRelicLocked(ClientState conn, SpawnedMonster target, bool hit, bool killed,
        (int X, int Y)? allyAt = null)
    {
        if (!CurrentGameWork || !target.Hostile || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea) return false;
        bool queued = false;
        if (hit && target.Rarity is MonsterRarity.Rare or MonsterRarity.Unique) queued = Roll();
        if (killed) queued |= Roll();
        return queued;

        bool Roll()
        {
            if (RelicTriggerOf(conn) is not { } relic) return false;
            var (grant, radius) = relic;
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (allyAt is { } ally && Dist(ally, conn.Walk.Position(now)) > radius) return false;
            if (now < conn.SkillReadyAt.GetValueOrDefault(grant.Effect) - 1e-9) return false;
            var own = SkillsFor(conn).EffectStats(grant.Effect, grant.Level).ToDictionary(s => s.Item1, s => s.Item2);
            int chance = Stats.Key("skill_triggered_by_nearby_allies_kill_or_hit_rare_unique_%_chance") is uint c ? own.GetValueOrDefault(c) : 0;
            if (conn.Minions.Random.Next(100) >= chance) return false;
            if (SingleUseCooldown.ForSkill(SkillsFor(conn), Stats, grant.Effect, grant.Level, CharacterPlayerStats(conn), own)
                is not { } plan) return false;
            var state = CooldownStateLocked(conn, grant.Effect, plan, now);
            if (!state.TryUse(now, plan)) return false;
            conn.SkillReadyAt[grant.Effect] = now + state.RemainingSeconds;
            conn.Minions.PendingRelics++;
            return true;
        }
    }

    // A granted skill resolved like a gem: its own stats at the granted level and the aura markers SupportedSkillAt
    // adds. No EffectiveGemLevel or GemQualityBonus: those raise gems.
    static SupportedGem GrantedSupportedSkill(ClientState conn, PassiveGrantedSkills.Grant grant)
    {
        var own = SkillsFor(conn).EffectStats(grant.Effect, grant.Level).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        if (SkillsFor(conn).IsAura(grant.Effect)) own[OwnAuraRestriction.BaseAura] = 1;
        own[OwnAuraRestriction.Aura] = OwnAuraRestriction.IsAura(own) ? 1 : 0;
        return new(grant.Effect, grant.Level, 0, [], own, Granted: true);
    }

    // The footstep trigger's poll: the run animation's clock and each footstep are followed to within this.
    const int FootstepPollMs = 50;

    // The player's footstep skills (Abberath's Fury), for the connection's life: every poll moves the run animation's clock
    // and triggers each skill once per footstep it passed. Not while paused: the game clock stands still then.
    static async Task RunFootstepSkills(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlayerUpdatesState updates)
    {
        double last = updates.Schedule.Now(conn);
        while (PlayerUpdatesCurrent(conn, updates))
        {
            await updates.Schedule.Wait(PlayerUpdateRole.Footsteps, FootstepPollMs, updates.Stop.Token);
            double now = updates.Schedule.Now(conn);
            await SendTemporarySkillEffects(remote, stream, ciphers, conn);
            int steps;
            lock (conn.Monsters)
            {
                if (!PlayerUpdatesCurrent(conn, updates)) return;
                if (conn.Paused || now <= last) continue;
                steps = FootstepsDue(conn, ref conn.FootstepRun, last, now);
                last = now;
            }
            if (steps == 0) continue;
            foreach (var skill in FootstepSkills(conn, now))
                for (int i = 0; i < steps && PlayerUpdatesCurrent(conn, updates); i++)
                    await TriggerFootstepSkill(remote, stream, ciphers, conn, skill);
        }
    }

    // One poll of the run animation's clock, from → now, and the footsteps it passed. The clock runs while the player walks
    // (until Walk.ArrivesAt; a movement skill's blink is no walk) at the walk's speed over the base speed, so movement speed
    // and chill change the step rate (the wiki: movement speed raises the frequency). It starts over when the walk ends, as
    // the run animation does.
    // Shield Charge walks its charge (ShieldCharge.cs), so it takes steps at the charge speed, as the wiki says.
    public static int FootstepsDue(ClientState conn, ref double run, double from, double now)
    {
        double before = run, until = Math.Min(now, conn.Walk.ArrivesAt);
        if (until > from) run += (until - from) * conn.Walk.Speed / WalkSpeed;
        int steps = PassiveGrantedSkills.Footsteps(before, run);
        if (now >= conn.Walk.ArrivesAt) run = 0;
        return steps;
    }

    // The footstep skills that trigger now: none while the player is dead, leaving, frozen or stunned, in town or in the
    // grace period, nor a disabled one (Devout of Gruthkul's "Your Spells are disabled": the wiki says it disables Abberath's
    // Fury).
    public static IReadOnlyList<PassiveGrantedSkills.Grant> FootstepSkills(ClientState conn, double now)
    {
        List<ushort> nodes;
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || PlayerActionBlocked(conn, now, now)) return [];
            nodes = [.. conn.Passives, .. conn.MainAscendancy.Allocated];
        }
        var skills = (LeagueFor(conn).HardMode ? RuthlessGrantedSkills : GrantedSkills).Value.For(nodes).Where(s => PassiveGrantedSkills.IsFootstepSkill(s.EffectId)).ToList();
        if (skills.Count == 0 || InTown(conn) || InGracePeriod(conn)) return [];
        return [.. skills.Where(s => ActorSkillDisabledCore(conn, s.Effect, null) is null)];
    }

    // A granted spell's hit (no gem, no supports): its base damage with the player's increases, added damage and conversion,
    // as a supported spell's (SupportedHitFor); Spell Skills deal no Damage zeroes it (SkillDamageContext).
    public static DamagePacket GrantedSpellHit(ClientState conn, int effect, int level) => GrantedSpell(conn, effect, level).Hit;

    // The hit, and what its ignite takes: the flat damage (base and added, after conversion, before any increase; wiki
    // Ignite), the caster's and the skill's stats and the damage context.
    public static (DamagePacket Hit, DamagePacket Flat, Dictionary<uint, int> Stats, SkillDamageContext Context) GrantedSpell(
        ClientState conn, int effect, int level)
    {
        var caster = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
        var stats = MergeSkillStats(caster, SkillsFor(conn).EffectStats(effect, level).ToDictionary(v => v.Item1, v => v.Item2));
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        var plan = SkillHitPlan.ForSkill(SkillsFor(conn), effect, level);
        var context = new SkillDamageContext(plan.Kind, caster);
        if (!plan.CanHit) return (DamagePacket.Empty, DamagePacket.Empty, stats, context);
        var conversion = DamageStats.Conversion(stats, Stats, attack: false);
        double effectiveness = AddedEffectiveness(conn, effect, level);
        var hit = SpellDamage.Packet(plan.SpellBase, Get, effectiveness).Convert(conversion);
        var flat = DamageStats.RawAdded(plan.SpellBase, Get, spell: true, effectiveness: effectiveness).Convert(conversion);
        return (context.Hit(hit), flat, stats, context);
    }

    // One footstep's skill (Abberath's Fury): its explosion art where the player stands, and a spell hit on every living
    // hostile monster within its radius (FootstepExplosionRadius grown by area of effect, SkillArea) of the player, as an
    // area spell's sweep (AreaSkillHit) without a gem: no cost, no cast time, crits and rolls per monster, every hit before
    // the sweep's deaths. A landed hit ignites a surviving monster with its 25% chance or a critical strike (IgniteFromHit).
    public static async Task TriggerFootstepSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PassiveGrantedSkills.Grant skill)
    {
        var rules = CombatLifetime; var source = AscendancyPolicies.SourceOwner(conn);
        bool CurrentSource() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules) && source?.IsActive != false
            && ReferenceEquals(AscendancyPolicies.SourceOwner(conn), source);
        lock (conn.Monsters)
            if (!CurrentSource() || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
                || !conn.Passives.Contains(skill.Node) && !conn.MainAscendancy.Allocated.Contains(skill.Node)) return;
        var own = SkillsFor(conn).EffectStats(skill.Effect, skill.Level).ToDictionary(v => v.Item1, v => v.Item2);
        var area = MergeSkillStats(AreaPlayerStats(conn), own);
        int radius = SkillArea.Radius(PassiveGrantedSkills.FootstepExplosionRadius,
            name => Stats.Key(name) is uint k ? area.GetValueOrDefault(k) : 0, spell: true);
        string? art = Art.Animated(PassiveGrantedSkills.ExplosionArtVariation, 0);
        (int X, int Y) at;
        uint? effectObject = null;
        List<(uint Id, SpawnedMonster M)> targets;
        using (ProjectileMutation(conn))
        {
            if (!CurrentSource() || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Paused) return;
            double now = ProjectileWorldTime(conn);
            at = conn.Walk.Position(now);
            targets = [.. conn.Monsters.Where(e => e.Value is { Hostile: true, Dead: false, Life: > 0 }
                && Dist(e.Value.Walk.Position(now), at) <= radius).Select(e => (e.Key, e.Value))];
            if (art is not null) effectObject = conn.TakeObjectId();
        }
        if (effectObject is uint obj)
            await SendWorldCreate(conn, obj, () =>
            {
                lock (conn.Monsters)
                {
                    if (!CurrentSource() || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return null;
                    conn.TemporarySkillEffects[obj] = AreaTime(conn) + PassiveGrantedSkills.ExplosionArtMs / 1000.0;
                    return InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                        new EffectObject(at.X, at.Y, art!).Blob());
                }
            }, wake: true, transport: (remote, stream, ciphers));
        if (!CurrentSource() || targets.Count == 0) return;

        using (ProjectileMutation(conn))
        {
            // Revalidate the actual grant after visual output. A refunded/retired source cannot admit a stale plan.
            if (!CurrentSource() || conn.Closed || conn.LeavingArea
                || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused || !FootstepSkills(conn, ProjectileWorldTime(conn)).Contains(skill)) return;
            using var statScope = new PlayerStatScope(conn, fresh: true);
            var player = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
            int Player(string name) => Stats.Key(name) is uint k ? player.GetValueOrDefault(k) : 0;
            int Own(string name) => Stats.Key(name) is uint k ? own.GetValueOrDefault(k) : 0;
            var merged = MergeSkillStats(player, own);
            int Merged(string name) => Stats.Key(name) is uint k ? merged.GetValueOrDefault(k) : 0;
            var plan = SkillHitPlan.ForSkill(SkillsFor(conn), skill.Effect, skill.Level);
            if (!plan.CanHit) return;
            var context = new SkillDamageContext(plan.Kind, player);
            var raw = DamageStats.RawAdded(plan.SpellBase, Merged, spell: true,
                effectiveness: AddedEffectiveness(conn, skill.Effect, skill.Level));
            var conversion = DamageStats.Conversion(merged, Stats, attack: false);
            var types = Supports.Of(skill.Effect)?.Types.ToHashSet();
            long additionalIncreased = Stats.Key(PlayerHitDamage.VsBlindedDamageStat) is uint damageKey
                ? (long)player.GetValueOrDefault(damageKey) + own.GetValueOrDefault(damageKey) : 0;
            AreaSkillDamageProfile Profile(long increased, int webs = 0)
            {
                increased += SpiderDamageIncrease(Merged, webs);
                var woven = SpiderRawDamage(raw, Merged, webs, AddedEffectiveness(conn, skill.Effect, skill.Level));
                var hit = context.Hit(PlayerHitDamage.Resolve(woven, Merged, conversion, spell: true,
                    area: Merged("is_area_damage") != 0, skillTypes: types, additionalIncreased: increased));
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
            var pen = Penetration.Of(player, Stats, attack: false, own);
            var mods = HitResistMods(player, own);
            int baseChance = (SkillsFor(conn).SpellCritChance(skill.Effect, skill.Level) ?? 0) + Own("additional_base_critical_strike_chance");
            int chance = SpellCriticalStrike.Chance(baseChance, Player, Own, conn.Pools.SpellCriticalStrikeChancePct);
            int? vsBlinded = Player(VsBlindedCriticalStat) is not 0 and var extra
                ? SpellCriticalStrike.Chance(baseChance, Player, Own, conn.Pools.SpellCriticalStrikeChancePct + (long)extra) : null;
            int? vsProfane = SpellCriticalStrike.Chance(baseChance, Player, Own,
                conn.Pools.SpellCriticalStrikeChancePct + FlaskGrounds.ProfaneCritIncrease);
            int multiplier = (int)Math.Clamp((long)conn.Pools.SpellCriticalStrikeMultiplier + Own("base_critical_strike_multiplier_+"),
                int.MinValue, int.MaxValue);
            var gambler = GamblerFor(player);
            bool rageGranted = false;
            var deaths = new List<(uint Id, SpawnedMonster M)>();
            var charges = new List<PlayerMinions.AcceptedCharges>();
            var reflected = new List<MonsterElementalAilments.Effect>();
            foreach (var (id, m) in targets)
            {
                // Retirement stops new contacts, while the transaction still completes its accepted hits/deaths below.
                if (!CurrentSource() || conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused) break;
                if (!conn.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)
                    || !m.Hostile || m.Dead || m.Life == 0 || m.Invulnerable || m.Asleep
                    || conn.AsleepMonsters.Contains(id)) continue;
                var profile = m.SpiderWebs.Count(ProjectileWorldTime(conn)) is > 0 and var webs
                    ? Profile(Blinded(conn, id) ? additionalIncreased : 0, webs)
                    : Blinded(conn, id) ? blindedProfile : normalProfile;
                if (MonsterBlocks(m, Random.Shared, spell: true)) continue;
                var rolled = (gambler with { DamageMorePct = 0 }).ForSkill(Own, spell: true).RollOutgoingHit(profile.Hit, Random.Shared);
                bool critical = RollCriticalStrike(conn, CriticalChanceOn(conn, id, chance, vsBlinded, vsProfane), own, attack: false);
                double hitAt = ProjectileWorldTime(conn);
                var (damage, _, mitigated) = MonsterHitDamage(m, rolled, critical, attack: false, hitAt, mods, pen,
                    gambler.InvertsResistance(Random.Shared));
                if (critical) damage = DamageModel.CriticalDamage(damage, CriticalMultiplierOn(m, multiplier));
                var stun = OwnerStun(conn, m, damage, mitigated, melee: false, spell: true, critical, player, own);
                var applied = ApplyMonsterHitLocked(conn, m, damage);
                if (applied is { StageStarts: true } staged)
                {
                    CommitBossStage(remote, stream, ciphers, conn, id, m, ProjectileWorldTime(conn));
                    applied = staged with { StageStarts = false };
                }
                if (applied is not null)
                    AilmentsOnHitLocked(conn, id, m, mitigated, spell: true, critical, multiplier, player, own, hitAt,
                        Random.Shared, reflected, poison: profile.Poison, blind: BlindHitInput.Capture(player, own));
                if (applied is not null && !rageGranted) rageGranted = RageOnHitLocked(conn, attack: false, melee: false, critical, own);
                if (applied is not null && m.Life > 0 && rolled[DamageType.Fire].Max > 0)
                    foreach (var packet in IgniteOnHitLocked(conn, id, m, profile.Ignite, critical, hitAt) ?? [])
                        conn.Minions.OutputQueue.Add(new(packet, $"0x01c1/0x01c2 monster 0x{id:x} ignited by {skill.EffectId}", id, m));

                if (applied is not { } landed) continue;
                conn.RecentEvents.RecordCriticalOutcome(hitAt, critical);
                if (critical)
                {
                    CommitAcceptedCritFlasksLocked(conn, player, conn.Clock.Elapsed.TotalSeconds);
                    MaybeCreateProfaneGroundLocked(conn, m.Walk.Position(hitAt), player);
                }
                CommitAcceptedHitLocked(conn, id, m, landed, damage, hitAt,
                    $"{skill.EffectId} (footstep){(critical ? " (critical)" : "")}", playerKill: true, deferKill: true, stun: stun);
                if (m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.RareHit);
                charges.Add(new(false, critical, m.Rarity, skill.EffectId, own, id == targets[0].Id, hitAt, landed.Life == 0));
                if (landed.Life == 0) deaths.Add((id, m));
            }
            // All accepted hit consequences and reflected ailments precede the sweep's attributed kills.
            if (reflected.Count > 0) ReflectAilmentsLocked(conn, reflected, ProjectileWorldTime(conn), player, Random.Shared);
            foreach (var (id, m) in deaths) CommitAcceptedKillLocked(conn, id, m, ProjectileWorldTime(conn), playerKill: true, charges);
            if (charges.Count > 0) CommitAcceptedChargeBatchLocked(conn, charges, player);
        }
        await PublishMinionOutcomesFor(remote, stream, ciphers, conn, rules);
    }

    // The current footstep role removes accepted temporary art at its pause-clock deadline, including
    // art created before replacement. Permanent connection state holds only object ids and deadlines.
    // Polled every 50 ms: with no temporary art at all there is nothing to remove, so the send gate and the Monsters lock
    // are left alone (a racy read of the count only delays a removal to the next poll).
    public static Task SendTemporarySkillEffects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        conn.TemporarySkillEffects.Count == 0 ? Task.CompletedTask : SendWorldBatch(conn, [], admission =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return null;
                double now = AreaTime(conn);
                var due = conn.TemporarySkillEffects.Where(p => p.Value <= now).Select(p => p.Key).ToArray();
                if (due.Length == 0) return null;
                foreach (uint id in due) conn.TemporarySkillEffects.Remove(id);
                var records = new WorldRecordBuilder(conn, admission);
                foreach (uint id in due) records.Remove(id);
                return records.Build();
            }
        }, transport: (remote, stream, ciphers));
}
