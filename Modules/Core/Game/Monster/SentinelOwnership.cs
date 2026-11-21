using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static ushort SentinelNativeType(string family) => family switch
    {
        "Absolution" => 50, "VaalAbsolution" => 51,
        "DominatingNormal" => 1, "DominatingMagic" => 2, "DominatingRare" => 3, "VaalDomination" => 4, _ => 0,
    };

    // MinionType.dat row for every non-sentinel player minion family so the client's top-left HUD minion counter
    // (Actor 0x0100, 0x14219a648 / 0x1413119bd) shows live counts across all summon types. Row 0 is SpectreMinion.
    static ushort? MinionNativeType(GolemState.Golem minion)
    {
        if (minion.Definition.IsGolem) return 24;
        return minion.Definition.Kind switch
        {
            "Spectre" => 0,
            "Zombie" => minion.Definition.Id == "RaiseZombieAltY" ? (ushort)8 : (ushort)7,
            "Skeleton" => 9,
            "VaalSkeleton" => 10,
            "Phantasm" => 11,
            "AnimatedGuardian" => 18,
            "AnimatedWeapon" => minion.Definition.Id == "AnimateWeaponAltY" ? (ushort)27 : (ushort)19,
            "RagingSpirit" => 20,
            "Clone" => minion.Definition.Id.EndsWith("AltX", StringComparison.Ordinal) ? (ushort)66
                : minion.Definition.Id.EndsWith("AltY", StringComparison.Ordinal) ? (ushort)67 : (ushort)38,
            "Agony" => 39,
            "Purity" => 40,
            "HolyRelic" => 41,
            "Reaper" => 52,
            _ => null,
        };
    }

    static ushort? ElementalRelicNativeType(string kind) => kind switch
    {
        "Fire" => 61,
        "Cold" => 62,
        "Lightning" => 63,
        _ => null,
    };

    static ushort SentinelSourceGroup(ushort source) =>
        (ushort)(((source & 0xc000) == 0xc000 ? 0x8000 : 0xc000) | (source & 0x3fff));

    static IEnumerable<GrantedSkill> SentinelSourceSkills(ClientState conn, IReadOnlyList<GrantedSkill> skills)
    {
        var gems = GemSkillEffects(conn).ToArray();
        foreach (var vaal in gems.Where(g => SkillsFor(conn).EffectId(g.Effect) is "VaalAbsolution" or "VaalDomination"))
        {
            var parent = SupportedSkill(conn, vaal.Id);
            string normal = SkillsFor(conn).EffectId(vaal.Effect) == "VaalAbsolution" ? "Absolution" : "DominatingBlow";
            foreach (var regular in gems.Where(g => SkillsFor(conn).EffectId(g.Effect) == normal))
            {
                var source = SupportedSkill(conn, regular.Id);
                if (parent.SourceGem is null || source.SourceGem is null || parent.Host != source.Host
                    || !ReferenceEquals(PlayerInventories.GemIdentity(parent.SourceGem), PlayerInventories.GemIdentity(source.SourceGem))) continue;
                if (skills.FirstOrDefault(s => s.Id == regular.Id) is { } skill)
                    yield return skill with { Id = SentinelSourceGroup(vaal.Id), Parent = (PlayerId, regular.Id) };
                break;
            }
        }
    }

    internal static byte[]? SentinelOwnershipPresentation(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return null;
            var current = new Dictionary<uint, MinionOwnershipChange>();
            var gems = GemSkillEffects(conn);
            foreach (var minion in conn.Summons.Fighters.Values)
            {
                if (minion.Monster.Dead || minion.Monster.Life == 0) continue;
                ushort sentinelType = SentinelNativeType(minion.Definition.Kind);
                if (sentinelType != 0)
                {
                    var regular = SupportedSkill(conn, minion.Source);
                    if (!regular.RequirementsMet || regular.SourceGem is null || minion.SourceIdentity is not null
                        && !ReferenceEquals(minion.SourceIdentity, PlayerInventories.GemIdentity(regular.SourceGem))) continue;
                    string vaal = sentinelType is 50 or 51 ? "VaalAbsolution" : "VaalDomination";
                    ushort source = minion.Source;
                    foreach (var gem in gems.Where(g => SkillsFor(conn).EffectId(g.Effect) == vaal))
                    {
                        var companion = SupportedSkill(conn, gem.Id);
                        if (companion.SourceGem is not null && companion.Host == regular.Host
                            && ReferenceEquals(PlayerInventories.GemIdentity(companion.SourceGem), PlayerInventories.GemIdentity(regular.SourceGem)))
                        { source = gem.Id; break; }
                    }
                    ushort group = SentinelSourceGroup(source);
                    current.Add(minion.Id, new(minion.Id, group, sentinelType));
                    continue;
                }
                if (minion.Source != 0 && MinionNativeType(minion) is ushort summonType)
                    current[minion.Id] = new(minion.Id, minion.Source, summonType);
            }
            foreach (var golem in conn.Golems.All.Values)
                if (!golem.Monster.Dead && golem.Monster.Life > 0 && golem.Source != 0
                    && MinionNativeType(golem) is ushort golemType)
                    current[golem.Id] = new(golem.Id, golem.Source, golemType);
            foreach (var fighter in conn.Reservations.Fighters.Values)
                if (!fighter.Monster.Dead && fighter.Monster.Life > 0 && fighter.Source != 0
                    && MinionNativeType(fighter) is ushort reservationType)
                    current[fighter.Id] = new(fighter.Id, fighter.Source, reservationType);
            foreach (var minion in conn.Minions.All)
            {
                if (current.ContainsKey(minion.Id) || minion.Monster.Dead || minion.Monster.Life == 0) continue;
                if (ElementalRelicNativeType(minion.Kind) is not ushort relicType) continue;
                ushort skillId = gems.FirstOrDefault(g => SkillsFor(conn).EffectId(g.Effect) == minion.Skill).Id;
                if (skillId != 0) current[minion.Id] = new(minion.Id, skillId, relicType);
            }
            var sent = conn.Summons.PresentedOwnership;
            var changes = sent.Values.Where(old => !current.TryGetValue(old.Id, out var now) || old != now)
                .OrderBy(old => old.Id).Select(old => new MinionOwnershipChange(old.Id, 0, 0)).ToList();
            changes.AddRange(current.Values.Where(now => !sent.TryGetValue(now.Id, out var old) || old != now).OrderBy(now => now.Id));
            if (changes.Count == 0) return null;
            sent.Clear(); foreach (var pair in current) sent.Add(pair.Key, pair.Value);
            return InstanceEntities.PlayerMinionsChange(PlayerId, changes, CanDetonateMines(conn));
        }
    }

    static Task SendSentinelOwnership(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        Func<bool> current) => SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [], "owned minion roster",
            atGate: () => current() ? SentinelOwnershipPresentation(conn) : null);
}
