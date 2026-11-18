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
                ushort type = SentinelNativeType(minion.Definition.Kind);
                if (type == 0 || minion.Monster.Dead || minion.Monster.Life == 0) continue;
                var regular = SupportedSkill(conn, minion.Source);
                if (!regular.RequirementsMet || regular.SourceGem is null || minion.SourceIdentity is not null
                    && !ReferenceEquals(minion.SourceIdentity, PlayerInventories.GemIdentity(regular.SourceGem))) continue;
                string vaal = type is 50 or 51 ? "VaalAbsolution" : "VaalDomination";
                ushort source = minion.Source;
                foreach (var gem in gems.Where(g => SkillsFor(conn).EffectId(g.Effect) == vaal))
                {
                    var companion = SupportedSkill(conn, gem.Id);
                    if (companion.SourceGem is not null && companion.Host == regular.Host
                        && ReferenceEquals(PlayerInventories.GemIdentity(companion.SourceGem), PlayerInventories.GemIdentity(regular.SourceGem)))
                    { source = gem.Id; break; }
                }
                ushort group = SentinelSourceGroup(source);
                current.Add(minion.Id, new(minion.Id, group, type));
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
        Func<bool> current) => SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [], "owned sentinel roster",
            atGate: () => current() ? SentinelOwnershipPresentation(conn) : null);
}
