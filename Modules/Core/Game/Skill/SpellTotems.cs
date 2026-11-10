using System.Runtime.CompilerServices;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Projectiles;

static partial class Game
{
    static readonly ConditionalWeakTable<TotemState.Totem, SupportedGem> TotemSources = new();
    internal static bool SpellTotemEffect(ClientState conn, int effect) => SkillsFor(conn).IsSpell(effect)
        && !SkillsFor(conn).IsAttack(effect) && !SkillsFor(conn).IsTravel(effect)
        && Supports.Of(effect)?.Types.Contains(47) != true && UsesProjectileRuntime(effect)
        && ResolveProjectileDefinition(effect) is not null
        && SkillHitPlan.ForSkill(SkillsFor(conn), effect, 1).CanHit;
    static bool SameTotemSource(SupportedGem a, SupportedGem b) => a.Host == b.Host && a.Group == b.Group
        && a.Effect == b.Effect && a.Level == b.Level && a.Quality == b.Quality
        && a.SourceGem is not null && b.SourceGem is not null
        && ReferenceEquals(PlayerInventories.GemIdentity(a.SourceGem), PlayerInventories.GemIdentity(b.SourceGem))
        && a.Supports.Select(s => s.Gem).SequenceEqual(b.Supports.Select(s => s.Gem));
    internal static void BindTotemSource(ClientState conn, TotemState.Totem totem)
    {
        TotemSources.Remove(totem); TotemSources.Add(totem, SupportedSkill(conn, totem.SkillId));
    }

    internal static Dictionary<uint, int> TotemCasterStats(IReadOnlyDictionary<uint, int> owner)
    {
        var caster = new Dictionary<uint, int>(owner);
        // This prohibition belongs to the owner (Ancestral Bond); type prohibitions remain inherited.
        caster.Remove(StatKeys.SpellSkillsDealNoDamage);
        return caster;
    }

    internal static double? TotemCastSeconds(ClientState conn, ushort skill)
    {
        var gem = SupportedSkill(conn, skill);
        var stats = MergeSkillStats(CharacterPlayerStats(conn), gem.Stats);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        // is_totem describes the placement skill. The proxy casts the underlying spell with its support penalty.
        int Read(string name) => name == "is_totem" ? 0 : Get(name);
        int speed = FinalCastSpeedPct(Read, () => HoldingFishingRod(conn),
            CastSpeedPct(CharacterPlayerStats(conn)) + GolemStat(gem.Stats, "base_cast_speed_+%") + Get("totem_skill_cast_speed_+%"));
        return SpellCastTiming.ForSkill(SkillsFor(conn), gem.Effect, speed)?.DurationMilliseconds is int ms
            ? Math.Max(TickSeconds, ms / 1000d) : null;
    }

    internal static GrantedSkill? TotemProxy(ClientState conn, ushort skill)
    {
        var gem = SupportedSkill(conn, skill);
        var payload = new Dictionary<uint, int>(gem.Stats);
        foreach (string name in new[] { "is_totem", "base_skill_is_totemified", "skill_is_totemified" })
            if (Stats.Key(name) is uint key) payload[key] = 0;
        foreach (var (name, value) in new[] { ("uses_no_mana", 1), ("mana_cost", 0),
            ("base_spell_cast_time_ms", SkillsFor(conn).CastTimeMs(gem.Effect)),
            ("virtual_base_spell_cast_time_ms", SkillsFor(conn).CastTimeMs(gem.Effect)) })
            if (Stats.Key(name) is uint key) payload[key] = value;
        if (TotemCastSeconds(conn, skill) is double seconds && Stats.Key("cast_speed_+%") is uint speed)
            payload[speed] = (int)Math.Round(100 * (SkillsFor(conn).CastTimeMs(gem.Effect) / (seconds * 1000) - 1));
        var proxy = SkillsFor(conn).FindByRow(0x4000, gem.Effect, gem.Level, [.. payload.Select(p => (p.Key, p.Value))]);
        return proxy is null ? null : proxy with { Parent = (PlayerId, skill) };
    }

    internal static bool SpellTotemCurrent(ClientState conn, TotemState.Totem totem)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
            || !totem.Alive(conn.Clock.Elapsed.TotalSeconds)) return false;
        var source = GemSkillEffects(conn).FirstOrDefault(g => g.Id == totem.SkillId);
        return source.Name is not null && source.Effect == totem.Effect && source.Gem == totem.SourceGem
            && SupportedSkill(conn, totem.SkillId) is var supported && supported.Host == totem.SourceHost
            && supported.SourceGem is not null && totem.SourceItem is not null
            && ReferenceEquals(PlayerInventories.GemIdentity(supported.SourceGem), PlayerInventories.GemIdentity(totem.SourceItem))
            && (!TotemSources.TryGetValue(totem, out var captured) || SameTotemSource(captured, supported))
            && TotemPathAvailable(conn, totem.SkillId, totem.Effect)
            && IsTotemSkill(conn, totem.SkillId);
    }

    internal static void TickSpellTotemsLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, List<byte> bytes)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        foreach (var totem in conn.Totems.Living(now).Where(t => t.Effect >= 0).ToArray())
        {
            if (!SpellTotemCurrent(conn, totem)) { totem.DiedAt = now; continue; }
            if (conn.Paused || now < totem.NextCast) continue;
            var target = conn.Monsters.Where(p => p.Value is { Hostile: true, Dead: false, Life: > 0 }
                && Dist(p.Value.Walk.Position(now), totem.At) <= 60)
                .OrderBy(p => Dist(p.Value.Walk.Position(now), totem.At)).FirstOrDefault();
            if (target.Value is null || TotemCastSeconds(conn, totem.SkillId) is not double seconds) continue;
            var aim = target.Value.Walk.Position(now);
            var gem = GemSkillEffects(conn).First(g => g.Id == totem.SkillId);
            var session = ProjectilesFor(remote, stream, ciphers, conn);
            ushort sequence = ++totem.Sequence;
            totem.NextCast = now + seconds;
            bytes.AddRange(InstanceProtocol.ActionStarted(totem.Id, totem.At.X, totem.At.Y, aim.X, aim.Y,
                0x4000, sequence, 0x0408));
            var lifetime = CombatLifetime;
            session.Schedule(ProjectileRuntime.Time(totem.NextCast),
                () => ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(conn, totem),
                () =>
                {
                    var use = CaptureGemProjectile(conn, session, totem.SkillId, gem, aim,
                        CaptureSkillCriticalRoll(conn, totem.SkillId), sequence, totem: totem);
                    return ClientDrawnVolley(conn, session, totem.SkillId, gem, use, sequence, 0);
                });
        }
    }
}
