using System.Runtime.CompilerServices;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Projectiles;

public static partial class Game
{
    internal static bool NativeProjectileTotem(Player player, int effect) =>
        GatewayProjectileSkillProfiles.IsHolyFlameTotem(SkillsFor(player).EffectId(effect));
    sealed class TotemChannel { public bool Active; public ushort Sequence; }
    static readonly RuleTable<TotemState.Totem, TotemChannel> TotemChannels = new();
    sealed record TotemSkillSource(SupportedGem Skill, GemSourceLifetimes.SourceLifetime? Lifetime);
    static readonly RuleTable<TotemState.Totem, TotemSkillSource> TotemSources = new();
    sealed record FormationTarget((int X, int Y) At);
    static readonly RuleTable<TotemState.Totem, FormationTarget> FormationTargets = new();
    public static bool SpellTotemEffect(Player player, int effect) => SkillsFor(player).IsSpell(effect)
        && !SkillsFor(player).IsAttack(effect) && (!SkillsFor(player).IsTravel(effect)
            || SkillsFor(player).EffectId(effect) is CorpseBodyswapEffect or BodyswapSacrificeEffect)
        && (SkillsFor(player).EffectId(effect) == WitherEffect || Supports.Of(effect)?.Types.Contains(47) != true)
        && (TotemSummon(player, effect) is not null || GolemData.Find(SkillsFor(player).EffectId(effect)) is not null
            || SkillsFor(player).EffectId(effect) is BloodOfferingEffect or CorpseBodyswapEffect or BodyswapSacrificeEffect or WitherEffect
            || CurseCatalog.All.ContainsKey(SkillsFor(player).EffectId(effect) ?? "")
            || (ArcProxyEffect(SkillsFor(player).EffectId(effect)) || IsAbsolutionHitSpell(player, effect) || SpellTotemNova(player, effect)
                || ShockNovaEffect(SkillsFor(player).EffectId(effect)) || GlacialCascadeEffect(SkillsFor(player).EffectId(effect))
                || BladefallEffect(SkillsFor(player).EffectId(effect)) || FrostBombEffect(SkillsFor(player).EffectId(effect))
                || VoidSphereEffect(SkillsFor(player).EffectId(effect)) || HydrosphereEffect(SkillsFor(player).EffectId(effect))
                || TornadoEffect(SkillsFor(player).EffectId(effect))
                || IsPurifyingFlame(SkillsFor(player).EffectId(effect)) || IsWaveOfConviction(SkillsFor(player).EffectId(effect))
                || IsGroundBladeBlast(SkillsFor(player).EffectId(effect)) || IsBladeBlastOfUnloading(SkillsFor(player).EffectId(effect))
                || IsBladeVortex(SkillsFor(player).EffectId(effect)) || SkillsFor(player).EffectId(effect) == VaalBladeVortexEffect
                || IsCracklingLance(SkillsFor(player).EffectId(effect)) || SkillsFor(player).EffectId(effect) == ManabondEffect
                || UsesProjectileRuntime(effect) && ResolveProjectileDefinition(effect) is not null)
                && SkillHitPlan.ForSkill(SkillsFor(player), effect, 1).CanHit);

    const string BladeBlastDaggerDetonation = "BladeBlastAltY";

    static bool SpellTotemNova(Player player, int effect) =>
        SkillsFor(player).EffectId(effect) is "IceNova" or "IceNovaAltY" or BladeBlastDaggerDetonation or TrarthanBargain;

    static SummonCatalog.Definition? TotemSummon(Player player, int effect) =>
        SummonData.Find(SkillsFor(player).EffectId(effect)) is { Family: not "Clone", Id: not "AnimateWeaponAltX" } summon ? summon : null;
    static bool SameLinkedSkillSource(SupportedGem a, SupportedGem b) => a.Host == b.Host && a.Group == b.Group
        && a.Effect == b.Effect && a.Quality == b.Quality
        && (a.WornGrant is { } worn ? b.WornGrant == worn && b.RequirementsMet
            : a.SourceGem is not null && b.SourceGem is not null
                && ReferenceEquals(PlayerInventories.GemIdentity(a.SourceGem), PlayerInventories.GemIdentity(b.SourceGem)))
        && a.Supports.Select(s => (s.Gem.Effect, s.Gem.Socket, s.Gem.Quality))
            .SequenceEqual(b.Supports.Select(s => (s.Gem.Effect, s.Gem.Socket, s.Gem.Quality)));
    public static void BindTotemSource(Player player, TotemState.Totem totem)
    {
        TotemSources.Remove(totem);
        var skill = SupportedSkill(player, totem.SkillId);
        TotemSources.Add(totem, new(skill, CaptureGemSourceLifetimeLocked(player, totem.SkillId, skill.Effect)));
    }

    public static Dictionary<uint, int> TotemCasterStats(IReadOnlyDictionary<uint, int> owner)
    {
        var caster = new Dictionary<uint, int>(owner);
        // Only owner-only prohibitions are bypassed. Gruthkul's spell prohibition still belongs to the spell.
        foreach (uint name in new[]
        {
            Stat.KeystoneAncestralBond, Stat.DealNoDamageYourself,
            Stat.RecoverPctMaximumLifeWhenCursingNonCursedEnemy, Stat.RecoverPctMaximumManaWhenCursingNonCursedEnemy,
        })
            caster.Remove(name);
        // Arcane Surge is a buff on the owner, not on his totems: its more Spell Damage stays his.
        if (ArcaneSurgeSpellMore is var arcane) caster.Remove(arcane);
        // "Gain Arcane Surge when you deal a Critical Strike": the owner's critical strikes, not his totems'.
        caster.Remove(Stat.GainArcaneSurgeOnCritPctChance);
        // "Curse Enemies with <Hex> on Hit" is the owner's hits too.
        foreach (var (stat, _) in ItemCurseOnHitStats) caster.Remove(stat);
        return caster;
    }

    public static double? TotemCastSeconds(Player player, ushort skill)
    {
        var gem = SupportedSkill(player, skill);
        if (SkillsFor(player).IsAttack(gem.Effect)) return TotemAttackSeconds(player, skill);
        var stats = MergeSkillStats(CharacterPlayerStats(player), gem.Stats);
        int Get(uint key) => stats.GetValueOrDefault(key);
        // is_totem describes the placement skill. The proxy casts the underlying spell with its support penalty.
        int Read(uint name) => name == Stat.IsTotem ? 0 : Get(name);
        // The owner's Arcane Surge cast speed is his buff's, not the totem's.
        var owner = CharacterPlayerStats(player);
        int speed = FinalCastSpeedPct(Read, player,
            CastSpeedPct(owner) - GolemStat(owner, Stat.ArcaneSurgeCastSpeedPlusPct)
            + GolemStat(gem.Stats, Stat.BaseCastSpeedPlusPct) + Get(Stat.TotemSkillCastSpeedPlusPct)
            + TaggedCastSpeedBonusPct(player, gem.Effect, owner.GetValueOrDefault, gem.Stats.GetValueOrDefault));
        return SpellCastTiming.ForSkill(SkillsFor(player), gem.Effect, speed)?.DurationMilliseconds is int ms
            ? Math.Max(TickSeconds, ms / 1000d) : null;
    }

    public static GrantedSkill? TotemProxy(Player player, ushort skill)
    {
        var gem = SupportedSkill(player, skill);
        var payload = new Dictionary<uint, int>(gem.Stats);
        foreach (uint name in new[] { Stat.IsTotem, Stat.BaseSkillIsTotemified, Stat.SkillIsTotemified })
            payload[name] = 0;
        foreach (var (name, value) in new[] { (Stat.UsesNoMana, 1), (Stat.ManaCost, 0),
            (Stat.BaseSpellCastTimeMs, SkillsFor(player).CastTimeMs(gem.Effect)),
            (Stat.VirtualBaseSpellCastTimeMs, SkillsFor(player).CastTimeMs(gem.Effect)) })
            if (name is var key) payload[key] = value;
        // A totem never walks. Without this the client sends a server-driven action through its own walk-into-range
        // step, which drops it (11 Â§"skill_is_non_pathing"): the totem stood still and its spell drew nothing.
        payload[Stat.SkillIsNonPathing] = 1;
        if (SpellTotemNova(player, gem.Effect) || IsGroundBladeBlast(SkillsFor(player).EffectId(gem.Effect))
            || IsBladeBlastOfUnloading(SkillsFor(player).EffectId(gem.Effect)) || GlacialCascadeEffect(SkillsFor(player).EffectId(gem.Effect)))
            payload[Stat.ActiveSkillAreaOfEffectRadius] = SkillRadius(player, gem.Stats, spell: true, gem.Effect);
        else if (ShockNovaEffect(SkillsFor(player).EffectId(gem.Effect)))
            payload[Stat.ActiveSkillAreaOfEffectRadius] = SkillRadius(player, 26, gem.Stats, spell: true, gem.Effect);
        else if (CurseCatalog.Hex(SkillsFor(player).EffectId(gem.Effect)))
            payload[Stat.ActiveSkillAreaOfEffectRadius] = CurseRadius(player, skill, aura: false, gem);
        else if (IsWaveOfConviction(SkillsFor(player).EffectId(gem.Effect)))
        {
            var caster = TotemCasterStats(CharacterPlayerStats(player));
            var area = SkillAreaStats(player, gem.Stats, gem.Effect, caster);
            double duration = SkillDurationModifiers.Seconds(gem.Stats.GetValueOrDefault(Stat.BaseSkillEffectDuration),
                caster.GetValueOrDefault, gem.Stats.GetValueOrDefault, DurationKinds(player, gem.Effect));
            if (SkillsFor(player).IsNova(gem.Effect) && caster.GetValueOrDefault(Stat.NovaSpellsCastAtTargetLocation) != 0)
                payload[Stat.VirtualSelfTargetedSkillsTargetLocationInstead] = 1;
            payload[Stat.SkillBehaviourVariation] = SkillsFor(player).BehaviourVariation(gem.Effect);
            payload[Stat.SkillEffectDuration] = (int)Math.Round(1000 * duration);
            payload[Stat.TotalSkillAreaOfEffectRadiusPlusPctIncludingFinal] = SkillArea.RadiusPercent(area.GetValueOrDefault, spell: true);
        }
        if (!SkillsFor(player).IsAttack(gem.Effect) && TotemCastSeconds(player, skill) is double seconds && Stat.CastSpeedPlusPct is var speed)
            payload[speed] = (int)Math.Round(100 * (SkillsFor(player).CastTimeMs(gem.Effect) / (seconds * 1000) - 1));
        if (SkillsFor(player).IsAttack(gem.Effect))
        {
            foreach (var (key, value) in WeaponStats(player, WeaponFor(player), CharacterPlayerStats(player))) payload[key] = value;
            double attackSeconds = TotemAttackSeconds(player, skill);
            payload[Stat.MainHandAttackSpeedPlusPct] = (int)Math.Round(100 * (WeaponFor(player).AttackMs / (attackSeconds * 1000) - 1));
            payload[Stat.HundredTimesAttacksPerSecond] = (int)Math.Round(100 / attackSeconds);
            if (Stat.MainHandWeaponType is var weaponType)
                payload[weaponType] = WeaponTypeStats.Of(ActiveMainHand(player)?.Type?.Class);
        }
        var proxy = SkillsFor(player).FindByRow(0x4000, gem.Effect, gem.Level, [.. payload.Select(p => (p.Key, p.Value))]);
        return proxy is null ? null : proxy with { Parent = (player.Id, skill) };
    }

    public static bool SpellTotemCurrent(Player player, TotemState.Totem totem)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
            || !totem.Alive(AreaTime(player))) return false;
        var source = GemSkillEffects(player).FirstOrDefault(g => g.Id == totem.SkillId);
        return source.Name is not null && source.Effect == totem.Effect && source.Gem == totem.SourceGem
            && SupportedSkill(player, totem.SkillId) is var supported && supported.Host == totem.SourceHost
            && (TotemSources.TryGetValue(totem, out var captured)
                ? SameLinkedSkillSource(captured.Skill, supported)
                    && (captured.Lifetime is null || player.GemSourceLifetimes.IsCurrent(captured.Lifetime))
                : supported.SourceGem is not null && totem.SourceItem is not null
                    && ReferenceEquals(PlayerInventories.GemIdentity(supported.SourceGem), PlayerInventories.GemIdentity(totem.SourceItem)))
            && GemSkillInstanceDisabled(player, totem.Effect, totem.SkillId) is null
            && TotemPathAvailable(player, totem.SkillId, totem.Effect)
            && IsTotemSkill(player, totem.SkillId);
    }

    public static void TickSpellTotemsLocked(Player player, List<byte> bytes)
    {
        var records = new WorldRecordBuilder(player, null);
        TickSpellTotemsLocked(player, records);
        bytes.AddRange(records.Bytes());
    }

    static bool SpellTotemTargetable(Player player, uint id, SpawnedMonster monster) =>
        TotemTargetable(monster) && !MonsterAsleep(player, id) && !monster.WorldHidden && !ArenaHazards.Untargetable(player, monster);

    static (int X, int Y) NonZeroTotemAim((int X, int Y) origin, (int X, int Y) aim) =>
        aim == origin ? (origin.X + 1, origin.Y) : aim;

    static void StopTotemChannel(TotemState.Totem totem, WorldRecordBuilder bytes)
    {
        if (TotemChannels.TryGetValue(totem, out var channel) && channel.Active)
        {
            channel.Active = false;
            bytes.World(totem.Id, InstanceProtocol.ChannelFinished(totem.Id, channel.Sequence));
        }
    }

    static (uint Id, SpawnedMonster? Monster, (int X, int Y) Pos) PickSpellTotemTarget(
        Player player, TotemState.Totem totem, double range, double now, bool preferSight)
    {
        double rangeSq = range * range;
        uint bestId = 0;
        SpawnedMonster? bestMonster = null;
        (int X, int Y) bestPos = default;
        long bestDistSq = long.MaxValue;
        int count = 0;
        foreach (var p in player.Area.MonstersNear(totem.At, range))
        {
            if (!SpellTotemTargetable(player, p.Key, p.Value)) continue;
            var pos = p.Value.Walk.Position(now);
            long dx = pos.X - totem.At.X, dy = pos.Y - totem.At.Y;
            long distSq = dx * dx + dy * dy;
            if (distSq > rangeSq) continue;
            count++;
            if (distSq < bestDistSq)
            {
                bestId = p.Key;
                bestMonster = p.Value;
                bestPos = pos;
                bestDistSq = distSq;
            }
        }
        if (bestMonster is null) return default;
        if (!preferSight || count <= 1) return (bestId, bestMonster, bestPos);
        var session = ProjectilesFor(player);
        var origin = ProjectileSession.Vector(totem.At);
        if (session.TraceTerrain(origin, ProjectileSession.Vector(bestPos)) is null)
            return (bestId, bestMonster, bestPos);
        var others = new List<(uint Id, SpawnedMonster Monster, (int X, int Y) Pos, long DistSq)>(count - 1);
        foreach (var p in player.Area.MonstersNear(totem.At, range))
        {
            if (p.Key == bestId || !SpellTotemTargetable(player, p.Key, p.Value)) continue;
            var pos = p.Value.Walk.Position(now);
            long dx = pos.X - totem.At.X, dy = pos.Y - totem.At.Y;
            long distSq = dx * dx + dy * dy;
            if (distSq <= rangeSq) others.Add((p.Key, p.Value, pos, distSq));
        }
        others.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        for (int i = 0; i < others.Count; i++)
        {
            var candidate = others[i];
            if (session.TraceTerrain(origin, ProjectileSession.Vector(candidate.Pos)) is null)
                return (candidate.Id, candidate.Monster, candidate.Pos);
        }
        return (bestId, bestMonster, bestPos);
    }

    static void ScheduleBloodOfferingTotem(Player player, TotemState.Totem totem, double range, double now,
        WorldRecordBuilder bytes)
    {
        var skill = SupportedSkill(player, totem.SkillId);
        var stats = CharacterPlayerStats(player);
        var corpse = FindOfferingCorpse(player, totem.At, now, range,
            GolemStat(stats, Stat.RaisedZombiesAreUsableAsCorpsesWhenAlive) > 0, minionSkill: true,
            cannotUseSpectreCorpses: GolemStat(skill.Stats, Stat.CannotUseSpectreCorpses) != 0);
        if (corpse is null && GolemStat(stats, Stat.OfferingSkillsDoNotRequireCorpses) == 0)
        {
            totem.NextCast = now + .25;
            return;
        }
        if (TotemCastSeconds(player, totem.SkillId) is not double seconds) return;

        var aim = corpse?.Walk.Position(now) ?? totem.At;
        uint target = corpse?.ObjectId ?? 0;
        ushort sequence = ++totem.Sequence;
        totem.NextCast = now + seconds;
        bytes.World(totem.Id, InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, aim.X, aim.Y,
            0x4000, sequence, player.MonsterOrderFlags, targetObjectId: target == 0 ? null : target));
        var session = ProjectilesFor(player);
        var lifetime = CombatLifetime;
        bool Current() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem);
        session.ScheduleContact(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(totem.NextCast)), Current, () =>
        {
            double release = ProjectileWorldTime(player);
            uint currentTarget = player.Area.Monsters.GetValueOrDefault(target) == corpse ? target : 0;
            StartOfferingLocked(player, totem.SkillId, skill, BloodOfferingEffect, aim, currentTarget, release, totem: totem);
        });
    }

    static void TickSpellTotemsLocked(Player player, WorldRecordBuilder bytes)
    {
        double now = AreaTime(player);
        foreach (var totem in player.Totems.Living(now).Where(t => t.Effect >= 0).ToArray())
        {
            if (!SpellTotemCurrent(player, totem))
            {
                totem.DiedAt = now;
                player.RemoveBlocker(totem.Id);
                StopTotemChannel(totem, bytes);
                TotemChannels.Remove(totem);
                MarkPlayerStats(player, PlayerStatStep.TotemAura);
                PresentNearby(player);
                continue;
            }
            if (player.Paused || now < totem.NextCast) continue;
            // A lockstep client's totem exists from the end of the frame its create is written in: no order before that.
            if (player.Connection.LockstepClient && !player.Connection.WorldOutput.PublishedObject(totem.Id).IsReady) continue;
            // Focused Ballista: due, but no owner attack to spend, no attack. It spends one when due, found a target or not.
            if (!FocusedBallistaMayAttack(player, totem)) continue;
            double range = TotemTargetRange(player, totem.SkillId);
            var gem = GemSkillEffects(player).First(g => g.Id == totem.SkillId);
            string effect = SkillsFor(player).EffectId(gem.Effect) ?? "";
            if (effect == CorpseBodyswapEffect)
            {
                CastCorpseBodyswapTotem(player, totem, now, bytes);
                continue;
            }
            if (VoidSphereEffect(effect) && player.ProjectileRuntime is { Active: true } sphereRuntime
                && sphereRuntime.HasVoidSphere(totem.Id)) continue;
            if (effect == BloodOfferingEffect)
            {
                ScheduleBloodOfferingTotem(player, totem, range, now, bytes);
                continue;
            }
            if (effect == BodyswapSacrificeEffect)
            {
                ScheduleBodyswapTotem(player, totem, now, bytes);
                continue;
            }
            bool daggerDetonation = effect == BladeBlastDaggerDetonation;
            bool warchief = effect == VaalAncestralWarchiefEffect;
            if (warchief && !VaalWarchiefInRange(player, totem, now)) continue;
            var summon = TotemSummon(player, gem.Effect);
            var golem = GolemData.Find(effect);
            bool nova = SpellTotemNova(player, gem.Effect);
            bool nativeChannel = effect == WitherEffect || NativeProjectileTotem(player, gem.Effect);
            if (effect == TrarthanBargain)
                range = Math.Min(range, SkillRadius(player, SupportedSkill(player, totem.SkillId).Stats, spell: true, gem.Effect));
            else if (effect is "ShockNova" or "ShockNovaRoyale")
                range = Math.Min(range, SkillRadius(player, 26, SupportedSkill(player, totem.SkillId).Stats, spell: true, gem.Effect));
            FormationTargets.TryGetValue(totem, out var formation);
            var target = formation is null
                ? PickSpellTotemTarget(player, totem, range, now, preferSight: !nova && summon is null && golem is null)
                : default;
            if ((formation is null && target.Monster is null && summon is null && golem is null)
                || TotemCastSeconds(player, totem.SkillId) is not double seconds)
            {
                if (nativeChannel) StopTotemChannel(totem, bytes);
                continue;
            }
            var aim = formation?.At ?? (nova || target.Monster is null ? totem.At : target.Pos);
            if (daggerDetonation && target.Monster is not null)
                aim = SpellCentre(player, gem.Effect, totem.At, target.Pos);
            if (!nova && summon is null && golem is null) aim = NonZeroTotemAim(totem.At, aim);
            uint summonTarget = 0;
            if (summon is { Corpse: true } && SummonNeedsCorpse(summon, PlayerStatValues(player)))
            {
                var spell = SupportedSkill(player, totem.SkillId);
                if (SummonCorpse(player, totem.At, 0, range,
                    GolemStat(spell.Stats, Stat.CannotUseSpectreCorpses) != 0) is not { } corpse) continue;
                summonTarget = player.Area.Monsters.First(p => ReferenceEquals(p.Value, corpse)).Key;
                aim = corpse.Walk.Position(now);
            }
            else if (summon?.Family is "AnimatedWeapon" or "AnimatedGuardian")
            {
                if (AnimatedItems(player, summon, SupportedSkill(player, totem.SkillId), totem.At, 0, null, range) is not { } source
                    || source.Ground is null && player.Summons.Fighters.Values.Any(g => g.Definition.Kind == summon.Family && !g.Monster.Dead)) continue;
                summonTarget = source.Id;
                aim = source.Ground is { } item ? (item.GridX, item.GridY) : totem.At;
            }
            if (CooldownForSkill(player, totem.SkillId) is { } cooldown)
            {
                totem.Cooldown ??= new(now, cooldown.RecoveryRate);
                if (!totem.Cooldown.TryUse(now, cooldown))
                {
                    if (nativeChannel) StopTotemChannel(totem, bytes);
                    continue;
                }
            }
            var session = ProjectilesFor(player);
            ushort sequence = ++totem.Sequence;
            totem.NextCast = now + seconds;
            bool arc = ArcProxyEffect(effect);
            bool shockNova = ShockNovaEffect(effect);
            bool cascade = GlacialCascadeEffect(effect);
            var curse = CurseCatalog.All.GetValueOrDefault(effect);
            bytes.World(totem.Id, InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, aim.X, aim.Y,
                0x4000, sequence, player.MonsterOrderFlags, targetObjectId: arc || curse?.Mark == true ? target.Id : null));
            if (nativeChannel)
            {
                var channel = TotemChannels.GetValue(totem, static _ => new());
                channel.Active = true;
                channel.Sequence = sequence;
            }
            var lifetime = CombatLifetime;
            if (warchief)
            {
                session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(totem.NextCast)), () =>
                {
                    if (session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem))
                        ReleaseVaalWarchiefSlam(player, session, totem, aim, sequence);
                });
                continue;
            }
            if (summon is not null || golem is not null)
            {
                session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(totem.NextCast)), () =>
                {
                    bool Current() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem);
                    if (!Current()) return;
                    session.Enqueue(async () =>
                    {
                        if (!Current()) return;
                        if (summon is not null)
                            await CastSummon(player, totem.SkillId, summon, aim,
                                summonTarget, player.ActionInterruptionGeneration, Current, triggered: true, playerConsumesCorpse: false);
                        else await CastGolem(player, totem.SkillId, golem!, aim,
                            player.ActionInterruptionGeneration, Current, triggered: true, casterAt: totem.At);
                    });
                });
                continue;
            }
            if (effect == VaalBladeVortexEffect)
            {
                long due = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(totem.NextCast));
                // This proxy was paid for at placement and has one Vaal cast.
                totem.NextCast = double.PositiveInfinity;
                bool Current() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem);
                session.ScheduleContact(due, Current, () =>
                {
                    var spell = SupportedSkill(player, totem.SkillId);
                    var critical = CaptureSkillCriticalRoll(player, totem.SkillId);
                    session.ReleaseVaalBladeVortex(spell, TotemCasterStats(CharacterPlayerStats(player)), totem.At, Current,
                        (at, radius) => TotemNovaLocked(player, session, totem, at, radius, sequence, sharedCritical: critical));
                });
                continue;
            }
            if (IsBladeVortex(effect))
            {
                bool Current() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem);
                session.ScheduleContact(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(totem.NextCast)), Current, () =>
                {
                    var spell = SupportedSkill(player, totem.SkillId);
                    session.ReleaseBladeVortex(totem.Id, totem.SkillId, SkillArtVariation(player, gem.Gem), spell, TotemCasterStats(CharacterPlayerStats(player)), Current,
                        (radius, scale, critIncrease) => TotemNovaLocked(player, session, totem, totem.At, radius + BladeVortexSizeReach, sequence,
                            damageMore: scale, extraCriticalIncrease: critIncrease,
                            targetFilter: (id, p) => BladeVortexReaches(player, totem.At, radius, id, p)));
                });
                continue;
            }
            bool rain = totem.Ballista && BallistaRain(player, gem.Effect);
            if (arc || nova || shockNova || cascade || curse is not null)
            {
                bool Current() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem);
                session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(totem.NextCast)), () =>
                {
                    if (!Current()) return;
                    if (curse is not null)
                    {
                        if (curse.Mark && player.Area.Monsters.GetValueOrDefault(target.Id) != target.Monster) return;
                        double releasedAt = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                        var linked = SupportedSkill(player, totem.SkillId);
                        var caster = TotemCasterStats(CharacterPlayerStats(player));
                        ApplyCurseTargetsLocked(player, totem.SkillId, linked, curse, aim, target.Id, releasedAt, caster);
                        session.Enqueue(() => EnemyDebuffsTick(player, releasedAt));
                    }
                    else if (effect == TrarthanBargain)
                    {
                        var skill = SupportedSkill(player, totem.SkillId);
                        long chaos = GainTrarthanRuin(player, totem.Id, 0x4000, skill.Stats, totem.Life, totem.MaxLife, out uint paid);
                        if (paid > 0)
                        {
                            totem.Life -= paid;
                            session.World(totem.Id, InstanceProtocol.LifeUpdate(totem.Id, totem.Life));
                        }
                        int radius = SkillRadius(player, skill.Stats, spell: true, skill.Effect);
                        TotemNovaLocked(player, session, totem, totem.At, radius, sequence, baseChaos: chaos);
                    }
                    else if (nova)
                    {
                        int radius = SkillRadius(player, SupportedSkill(player, totem.SkillId).Stats, spell: true, gem.Effect);
                        if (daggerDetonation)
                            TotemNovaLocked(player, session, totem, aim, radius, sequence);
                        else
                            ReleaseTotemNova(player, session, totem, radius, sequence, SkillArtVariation(player, gem.Gem));
                    }
                    else if (shockNova)
                    {
                        double releasedAt = ProjectileWorldTime(player);
                        var at = target.Monster is { } live && player.Area.Monsters.GetValueOrDefault(target.Id) == live
                            && SpellTotemTargetable(player, target.Id, live) ? live.Walk.Position(releasedAt) : aim;
                        session.ReleaseTotemShockNova(totem, at, sequence, Current);
                    }
                    else if (cascade)
                        session.ReleaseTotemGlacialCascade(effect, totem, aim, sequence, Current);
                    else if (player.Area.Monsters.GetValueOrDefault(target.Id) == target.Monster)
                        ReleaseArcTotem(player, session, totem, target.Id, sequence, Current);
                });
                continue;
            }
            if (IsAbsolutionHitSpell(player, gem.Effect))
            {
                var spell = SupportedSkill(player, totem.SkillId);
                int radius = SkillRadius(player, spell.Stats, spell: true, gem.Effect);
                session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(totem.NextCast)), () =>
                {
                    if (session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem))
                        TotemNovaLocked(player, session, totem, aim, radius, sequence, hitSummon: true);
                });
                continue;
            }
            uint targetId = target.Id;
            SpawnedMonster? targetMonster = target.Monster;
            var initialAim = aim;
            (int X, int Y) ReleaseAim()
            {
                if (targetMonster is null
                    || !player.Area.Monsters.TryGetValue(targetId, out var live) || !ReferenceEquals(live, targetMonster)
                    || !SpellTotemTargetable(player, targetId, live)) return initialAim;
                double releaseNow = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                var livePos = live.Walk.Position(releaseNow);
                return Dist(livePos, totem.At) <= range ? NonZeroTotemAim(totem.At, livePos) : initialAim;
            }

            if (effect == WitherEffect)
            {
                bool Current() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem);
                session.ScheduleContact(ProjectileRuntime.Time(totem.NextCast), Current, () =>
                    WitherPulseLocked(player, SupportedSkill(player, totem.SkillId), ReleaseAim(), ProjectileWorldTime(player), totem: true));
                continue;
            }
            if (effect == ManabondEffect)
            {
                bool Current() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem);
                session.ScheduleContact(ProjectileRuntime.Time(totem.NextCast), Current, () =>
                {
                    var centre = ReleaseAim();
                    var spell = SupportedSkill(player, totem.SkillId);
                    var area = ManabondArea(player, gem.Effect, spell.Stats, centre,
                        TotemCasterStats(CharacterPlayerStats(player)));
                    TotemNovaLocked(player, session, totem, centre, area.Reach, sequence, targetFilter: area.Covers);
                });
                continue;
            }
            if (IsCracklingLance(effect))
            {
                bool Current() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem);
                session.ScheduleContact(ProjectileRuntime.Time(totem.NextCast), Current, () =>
                {
                    var spell = SupportedSkill(player, totem.SkillId);
                    session.ReleaseCracklingLance(gem.Effect, spell.Stats, TotemCasterStats(CharacterPlayerStats(player)),
                        totem.At, ReleaseAim(), (reach, filter, _) =>
                            TotemNovaLocked(player, session, totem, totem.At, reach, sequence, targetFilter: filter));
                });
                continue;
            }
            if (BladefallEffect(effect) || FrostBombEffect(effect) || VoidSphereEffect(effect) || HydrosphereEffect(effect) || TornadoEffect(effect)
                || IsPurifyingFlame(effect) || IsWaveOfConviction(effect)
                || IsGroundBladeBlast(effect) || IsBladeBlastOfUnloading(effect))
            {
                bool Current() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem);
                session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(totem.NextCast)), () =>
                {
                    if (!Current()) return;
                    var at = ReleaseAim();
                    if (BladefallEffect(effect)) session.ReleaseTotemBladefall(totem, at, sequence, Current);
                    else if (FrostBombEffect(effect))
                        session.ReleaseFrostBomb(gem.Effect, SupportedSkill(player, totem.SkillId).Stats, at, Current,
                            (centre, radius, _) => TotemNovaLocked(player, session, totem, centre, radius, sequence));
                    else if (VoidSphereEffect(effect))
                    {
                        var own = SupportedSkill(player, totem.SkillId).Stats;
                        double more = Math.Max(0, 1 + own.GetValueOrDefault(Stat.SupportTotemDamagePlusPctFinal) / 100d);
                        session.ReleaseVoidSphere(totem.Id, gem.Effect, own, at, Current,
                            (centre, radius, _) => TotemNovaLocked(player, session, totem, centre, radius, sequence,
                                damageMore: more));
                    }
                    else if (HydrosphereEffect(effect))
                    {
                        var source = SupportedSkill(player, totem.SkillId);
                        double more = Math.Max(0, 1 + source.Stats.GetValueOrDefault(Stat.SupportTotemDamagePlusPctFinal) / 100d);
                        session.ReleaseHydrosphere(totem.Id, gem.Effect, source.Stats, at, Current,
                            (centre, radius, drenched, cold, lightning) => TotemNovaLocked(player, session, totem, centre, radius,
                                sequence, damageMore: more, targetFilter: drenched, capturedGem: HydroDamageSource(source, cold, lightning)));
                    }
                    else if (TornadoEffect(effect))
                    {
                        var source = SupportedSkill(player, totem.SkillId);
                        var element = TornadoElement(player, source);
                        var damage = TornadoDamageSource(source, element);
                        double more = Math.Max(0, 1 + source.Stats.GetValueOrDefault(Stat.SupportTotemDamagePlusPctFinal) / 100d);
                        session.ReleaseTornado(gem.Effect, source.Level, source, element, totem.At, at, Current,
                            (centre, radius) => TotemNovaLocked(player, session, totem, centre, radius, sequence,
                                damageMore: more, capturedGem: damage));
                    }
                    else if (IsPurifyingFlame(effect))
                        SchedulePurifyingFlame(player, session, gem.Effect, SupportedSkill(player, totem.SkillId).Stats,
                            TotemCasterStats(CharacterPlayerStats(player)), totem.At, at, Current,
                            (radius, covers, more, onHit) => TotemNovaLocked(player, session, totem, at, radius, sequence,
                                damageMore: more, targetFilter: covers, onHit: onHit));
                    else if (IsGroundBladeBlast(effect) || IsBladeBlastOfUnloading(effect))
                    {
                        var critical = CaptureSkillCriticalRoll(player, totem.SkillId);
                        var own = SupportedSkill(player, totem.SkillId).Stats;
                        var caster = TotemCasterStats(CharacterPlayerStats(player));
                        void Hit((int X, int Y) centre, int radius, double scale) =>
                            TotemNovaLocked(player, session, totem, centre, radius, sequence, damageMore: scale, sharedCritical: critical);
                        if (IsBladeBlastOfUnloading(effect))
                            session.ReleaseBladeBlastOfUnloading(totem.Id, gem.Effect, own, caster,
                                () => SpellCentre(player, gem.Effect, totem.At, at), Current, Hit);
                        else
                            session.ReleaseBladeBlast(gem.Effect, own, caster, at, Current,
                                (centre, radius, _) => Hit(centre, radius, 1));
                    }
                    else
                        session.ReleaseConvictionWave(totem.Id, gem.Effect, SupportedSkill(player, totem.SkillId).Stats,
                            TotemCasterStats(CharacterPlayerStats(player)), totem.At, at, Current,
                            (origin, reach, filter) => TotemNovaLocked(player, session, totem, origin, reach, sequence,
                                targetFilter: filter));
                });
                continue;
            }
            if (rain)
            {
                session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(totem.NextCast)), () =>
                {
                    if (session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem))
                        ReleaseArrowRain(session, totem.SkillId, gem, ReleaseAim(),
                            CaptureSkillCriticalRoll(player, totem.SkillId), sequence,
                            mirage: false, transform: null, podDamageFactor: 1, totem: totem);
                });
                continue;
            }
            if (totem.Ballista && IsBarrageGem(SkillsFor(player).EffectId(gem.Effect)))
            {
                ScheduleBarrage(player, session, totem.SkillId, gem.Effect, now, seconds, sequence,
                    () => ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem),
                    _ => ClientDrawnVolley(player, session, totem.SkillId, gem,
                        CaptureGemProjectile(player, session, totem.SkillId, gem, ReleaseAim(),
                            CaptureSkillCriticalRoll(player, totem.SkillId), sequence, totem: totem), sequence, 0));
                continue;
            }
            long releaseAt = ProjectileRuntime.Time(totem.NextCast);
            if (formation is not null) totem.NextCast = double.PositiveInfinity;
            session.Schedule(releaseAt,
                () => ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem),
                () =>
                {
                    var use = CaptureGemProjectile(player, session, totem.SkillId, gem, ReleaseAim(),
                        CaptureSkillCriticalRoll(player, totem.SkillId), sequence, totem: totem);
                    if (formation is not null)
                        use = use with { Profile = use.Profile with { Range = 3 * Dist(totem.At, aim) } };
                    return ClientDrawnVolley(player, session, totem.SkillId, gem, use, sequence, 0);
                }, released: formation is null ? null : () => totem.Until = Math.Min(totem.Until, AreaTime(player)));
        }
    }
}
