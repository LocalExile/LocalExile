using System.Runtime.CompilerServices;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Projectiles;

public static partial class Game
{
    internal static bool NativeProjectileTotem(Player player, int effect) =>
        SkillsFor(player).EffectId(effect) is "HolyFlameTotem" or "HolyFlameTotemAltX";
    sealed class TotemChannel { public bool Active; public ushort Sequence; }
    static readonly RuleTable<TotemState.Totem, TotemChannel> TotemChannels = new();
    static readonly RuleTable<TotemState.Totem, SupportedGem> TotemSources = new();
    public static bool SpellTotemEffect(Player player, int effect) => SkillsFor(player).IsSpell(effect)
        && !SkillsFor(player).IsAttack(effect) && !SkillsFor(player).IsTravel(effect)
        && Supports.Of(effect)?.Types.Contains(47) != true
        && (TotemSummon(player, effect) is not null || GolemData.Find(SkillsFor(player).EffectId(effect)) is not null
            || (SkillsFor(player).EffectId(effect) == "Arc" || UsesProjectileRuntime(effect) && ResolveProjectileDefinition(effect) is not null)
                && SkillHitPlan.ForSkill(SkillsFor(player), effect, 1).CanHit);

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
        TotemSources.Remove(totem); TotemSources.Add(totem, SupportedSkill(player, totem.SkillId));
    }

    public static Dictionary<uint, int> TotemCasterStats(IReadOnlyDictionary<uint, int> owner)
    {
        var caster = new Dictionary<uint, int>(owner);
        // Only owner-only prohibitions are bypassed. Gruthkul's spell prohibition still belongs to the spell.
        foreach (uint name in new[] { Stat.KeystoneAncestralBond, Stat.DealNoDamageYourself })
            caster.Remove(name);
        // Arcane Surge is a buff on the owner, not on his totems: its more Spell Damage stays his.
        if (ArcaneSurgeSpellMore is var arcane) caster.Remove(arcane);
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
        int speed = FinalCastSpeedPct(Read, () => HoldingFishingRod(player),
            CastSpeedPct(owner) - GolemStat(owner, Stat.ArcaneSurgeCastSpeedPlusPct)
            + GolemStat(gem.Stats, Stat.BaseCastSpeedPlusPct) + Get(Stat.TotemSkillCastSpeedPlusPct));
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
        payload[Stat.SkillIsNonPathing] = 1;
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
            && supported.SourceGem is not null && totem.SourceItem is not null
            && ReferenceEquals(PlayerInventories.GemIdentity(supported.SourceGem), PlayerInventories.GemIdentity(totem.SourceItem))
            && (!TotemSources.TryGetValue(totem, out var captured) || SameLinkedSkillSource(captured, supported))
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
            double range = TotemTargetRange(player, totem.SkillId);
            var gem = GemSkillEffects(player).First(g => g.Id == totem.SkillId);
            var summon = TotemSummon(player, gem.Effect);
            var golem = GolemData.Find(SkillsFor(player).EffectId(gem.Effect));
            bool nativeChannel = NativeProjectileTotem(player, gem.Effect);
            var target = PickSpellTotemTarget(player, totem, range, now, preferSight: summon is null && golem is null);
            if (target.Monster is null && summon is null && golem is null
                || TotemCastSeconds(player, totem.SkillId) is not double seconds)
            {
                if (nativeChannel) StopTotemChannel(totem, bytes);
                continue;
            }
            var aim = target.Monster is not null ? target.Pos : totem.At;
            if (summon is null && golem is null) aim = NonZeroTotemAim(totem.At, aim);
            uint summonTarget = 0;
            if (summon?.Corpse == true)
            {
                if (SummonCorpse(player, totem.At, 0, range) is not { } corpse) continue;
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
            bool arc = SkillsFor(player).EffectId(gem.Effect) == "Arc";
            bytes.World(totem.Id, InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, aim.X, aim.Y,
                0x4000, sequence, player.MonsterOrderFlags, targetObjectId: arc ? target.Id : null));
            if (nativeChannel)
            {
                var channel = TotemChannels.GetValue(totem, static _ => new());
                channel.Active = true;
                channel.Sequence = sequence;
            }
            var lifetime = CombatLifetime;
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
                            player.ActionInterruptionGeneration, Current, triggered: true);
                    });
                });
                continue;
            }
            if (arc)
            {
                session.Runtime.Schedule(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(totem.NextCast)), () =>
                {
                    if (session.Active && ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem)
                        && player.Area.Monsters.GetValueOrDefault(target.Id) == target.Monster)
                        ReleaseArcTotem(player, session, totem, target.Id, sequence);
                });
                continue;
            }
            uint targetId = target.Id;
            SpawnedMonster? targetMonster = target.Monster;
            var initialAim = aim;
            session.Schedule(ProjectileRuntime.Time(totem.NextCast),
                () => ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem),
                () =>
                {
                    var releaseAim = initialAim;
                    if (targetMonster is not null
                        && player.Area.Monsters.TryGetValue(targetId, out var live) && ReferenceEquals(live, targetMonster)
                        && SpellTotemTargetable(player, targetId, live))
                    {
                        double releaseNow = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                        var livePos = live.Walk.Position(releaseNow);
                        if (Dist(livePos, totem.At) <= range)
                            releaseAim = NonZeroTotemAim(totem.At, livePos);
                    }
                    var use = CaptureGemProjectile(player, session, totem.SkillId, gem, releaseAim,
                        CaptureSkillCriticalRoll(player, totem.SkillId), sequence, totem: totem);
                    return ClientDrawnVolley(player, session, totem.SkillId, gem, use, sequence, 0);
                });
        }
    }
}
