using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    static readonly Lazy<PassiveGrantedSkills> GrantedSkills = new(() => NewGrantedSkills(false));
    static readonly Lazy<PassiveGrantedSkills> RuthlessGrantedSkills = new(() => NewGrantedSkills(true));
    static PassiveGrantedSkills NewGrantedSkills(bool ruthless) => new(ruthless ? HardModeSkills.Value : Skills, ruthless: ruthless);

    /// <summary>The skills the player's allocated passives grant whose node carries this trigger stat
    /// (<c>has_trigger_on_crit_by_unique_enemy</c> and the like; the level row's AdditionalFlags name the same trigger).
    /// Call under the Monsters lock.</summary>
    static IEnumerable<PassiveGrantedSkills.Grant> TriggeredGrantsLocked(ClientState conn, string trigger)
    {
        if (Stats.Key(trigger) is not uint key) return [];
        var passives = PassivesFor(conn);
        return (LeagueFor(conn).HardMode ? RuthlessGrantedSkills : GrantedSkills).Value.For([.. conn.Passives, .. conn.MainAscendancy.Allocated])
            .Where(g => passives.Find(g.Node)?.Stats.Any(s => s.Key == key && s.Value != 0) == true);
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
    internal static bool TriggerElementalRelicLocked(ClientState conn, SpawnedMonster target, bool hit, bool killed,
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
    internal static int FootstepsDue(ClientState conn, ref double run, double from, double now)
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
    internal static IReadOnlyList<PassiveGrantedSkills.Grant> FootstepSkills(ClientState conn, double now)
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
    internal static DamagePacket GrantedSpellHit(ClientState conn, int effect, int level) => GrantedSpell(conn, effect, level).Hit;

    // The hit, and what its ignite takes: the flat damage (base and added, after conversion, before any increase; wiki
    // Ignite), the caster's and the skill's stats and the damage context.
    internal static (DamagePacket Hit, DamagePacket Flat, Dictionary<uint, int> Stats, SkillDamageContext Context) GrantedSpell(
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
    internal static async Task TriggerFootstepSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
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
            if (art is not null) effectObject = conn.NextObjectId++;
        }
        if (effectObject is uint obj)
            await Send(remote, stream, ciphers, [], null, atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (!CurrentSource() || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return null;
                    conn.TemporarySkillEffects[obj] = AreaTime(conn) + PassiveGrantedSkills.ExplosionArtMs / 1000.0;
                    return [.. InstanceEntities.CreateObject(obj, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                        new EffectObject(at.X, at.Y, art!).Blob()), .. InstanceProtocol.EnterWakeZone(obj)];
                }
            });
        if (!CurrentSource() || targets.Count == 0) return;

        using (ProjectileMutation(conn))
        {
            // Revalidate the actual grant after visual output. A refunded/retired source cannot admit a stale plan.
            if (!CurrentSource() || conn.Closed || conn.LeavingArea
                || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused || !FootstepSkills(conn, ProjectileWorldTime(conn)).Contains(skill)) return;
            var player = FinalPlayerStats(conn).ToDictionary(v => v.Item1, v => v.Item2);
            int Player(string name) => Stats.Key(name) is uint k ? player.GetValueOrDefault(k) : 0;
            int Own(string name) => Stats.Key(name) is uint k ? own.GetValueOrDefault(k) : 0;
            var (hit, flat, merged, context) = GrantedSpell(conn, skill.Effect, skill.Level);
            int Merged(string name) => Stats.Key(name) is uint k ? merged.GetValueOrDefault(k) : 0;
            var fire = flat[DamageType.Fire];
            double igniteMore = ChargeDamageMore(merged);
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
                    || m.Dead || m.Life == 0 || MonsterBlocks(m, Random.Shared, spell: true)) continue;
                var rolled = (gambler with { DamageMorePct = 0 }).ForSkill(Own, spell: true).RollOutgoingHit(hit, Random.Shared);
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
                    AilmentsOnHitLocked(conn, m, mitigated, spell: true, critical, multiplier, player, own, hitAt, Random.Shared, reflected);
                if (applied is not null) InflictMonsterBlind(conn, id, m, hitAt, BlindInputs(player, own));
                if (applied is not null && !rageGranted) rageGranted = RageOnHitLocked(conn, attack: false, melee: false, critical, own);
                // shortcut: the ignite's flat fire is rolled apart from the hit (the game takes the hit's own roll; the
                // spread is the same, and lucky or triple damage never reaches it either way), until a rolled hit
                // carries its roll position.
                if (applied is not null && IgniteFromHit(fire.Min + Random.Shared.NextDouble() * (fire.Max - fire.Min), critical,
                        Merged, igniteMore, m, Random.Shared) is { } ignite)
                    foreach (var packet in IgniteLocked(conn, id, m, context.OverTime(ignite), hitAt) ?? [])
                        conn.Minions.OutputQueue.Add(new(packet, $"0x01c1/0x01c2 monster 0x{id:x} ignited by {skill.EffectId}", id, m));

                if (applied is not { } landed) continue;
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
    static Task SendTemporarySkillEffects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return null;
                double now = AreaTime(conn);
                var due = conn.TemporarySkillEffects.Where(p => p.Value <= now).Select(p => p.Key).ToArray();
                if (due.Length == 0) return null;
                foreach (uint id in due) conn.TemporarySkillEffects.Remove(id);
                return [.. due.SelectMany(InstanceProtocol.RemoveObject)];
            }
        });
}
