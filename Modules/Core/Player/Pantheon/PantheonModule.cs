using System.Net.Sockets;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Pantheon;

/// <summary>
/// The Pantheon (Agents/docs/game/campaign/pantheon.md §4–7): C→S 0x1ee chooses a major or a minor god of the
/// PantheonPanelLayout row it names, once the god's boss is dead (its unlock flag) and where the client allows it; the
/// choice is saved by the row's Id (CharacterSaves.SetPantheon), shown by the Player component's two bytes (the creation
/// blob and the 0x0080 update), and the god's power, with the upgrades the character's PantheonSouls flags unlock after An
/// End to Hunger, joins the character's stat sources. Also <c>/pantheon</c> (dev) and /state's <c>pantheon</c> field for
/// the campaign driver. The gods unlock by the act tables' kills (QuestObjects), and Sin's god rows open the panel and set
/// <c>CanChangePantheon</c> (NPCTalk scripts).
/// </summary>
public sealed class PantheonModule : IModule, IDisposable
{
    readonly List<IDisposable> _events = [];
    void On<T>(GameEvent<T> ev, T run) where T : Delegate => _events.Add(ev.Add("Pantheon", run));

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var e in _events) e.Dispose();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        module.OnPacket(InstancePantheon.ChooseGodId, async p =>
        {
            var conn = ConnOf(p.Player);
            var (remote, stream, ciphers) = conn.Wire;
            await PantheonRules.ChooseGod(remote, stream, ciphers, conn, InstancePantheon.ReadChooseGod(p.Bytes.Span));
            return true;
        });
        On(GameEvents.PlayerStatSources, PantheonRules.WithGods);
        On(GameEvents.StateBuilding, (conn, state) => state["pantheon"] = PantheonRules.Json(conn));
        On(GameEvents.CommandsBuilding, (commands, remote, stream, ciphers, conn) => commands
            .Add("pantheon", "/pantheon [<god> ...] (dev: list the Pantheon gods, or choose them as C→S 0x1ee does: an Id, name or panel row, e.g. /pantheon TheBrineKing Tukohama)", async args =>
            {
                var catalog = PantheonCatalog.Default.Value;
                foreach (string arg in args)
                {
                    var god = int.TryParse(arg, out int row) && row >= 0 && row < catalog.Gods.Count ? catalog.Gods[row]
                        : catalog.Gods.FirstOrDefault(g => g.Id.Equals(arg, StringComparison.OrdinalIgnoreCase))
                        ?? catalog.Gods.FirstOrDefault(g => g.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
                    if (god is null) { await conn.Commands!.Reply($"No Pantheon god {arg}."); continue; }
                    await PantheonRules.ChooseGod(remote, stream, ciphers, conn, god.Wire);
                }
                await conn.Commands!.Reply($"Pantheon: {PantheonRules.Json(conn).ToJsonString()}");
            }));
    }
}

/// <summary>The Pantheon's rules (<see cref="PantheonModule"/>).</summary>
static class PantheonRules
{
    /// <summary>QuestFlags row 1409, a session flag: Sin's god rows set it (NPCTalk 993 …) so a god can be chosen there,
    /// outside a town.</summary>
    public const string CanChangePantheon = "CanChangePantheon";

    // The Rogue Harbour's areas (WorldAreas HASH16 of HeistHub, HeistHubEndless), where the client lets the player choose too.
    static readonly HashSet<ushort> Hubs = [0xb5be, 0x3064];

    /// <summary>Where a god may be chosen: a town, a hideout, the Rogue Harbour, or with CanChangePantheon set (the
    /// client's own gate before it sends 0x1ee, 0x14097e000).</summary>
    internal static bool CanChoose(ClientState conn) =>
        InTown(conn) || HideoutCatalog.ByArea(conn.Layout.Hash) is not null || Hubs.Contains(conn.Layout.Hash)
        || ClientHasQuestFlag(conn, CanChangePantheon);

    /// <summary>C→S 0x1ee: the god of a PantheonPanelLayout row (+1) replaces the major or the minor one, when it is unlocked
    /// and the place allows it; the client hears the choice back either way (a refusal shows the gods it had).</summary>
    internal static async Task ChooseGod(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, byte wire)
    {
        var god = PantheonCatalog.Default.Value.ByWire(wire);
        if (god is null || !PantheonCatalog.Unlocked(god, f => ClientHasQuestFlag(conn, f)))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x01ee pantheon {wire}: {(god is null ? "no such row" : $"{god.Id} is not unlocked ({god.UnlockFlag})")}");
            await SendGods(remote, stream, ciphers, conn, "refused");
            return;
        }
        if (!CanChoose(conn))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x01ee pantheon {god.Id}: not in a town or hideout, and no {CanChangePantheon}");
            await SendGods(remote, stream, ciphers, conn, "refused");
            return;
        }
        lock (conn.Monsters)
        {
            if (god.Major) conn.PantheonMajor = god.Id; else conn.PantheonMinor = god.Id;
            CharacterSaves.SetPantheon(CharacterName(conn), conn.PantheonMajor, conn.PantheonMinor);
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: {(god.Major ? "major" : "minor")} Pantheon god {god.Name} ({god.Id})");
        await SendGods(remote, stream, ciphers, conn, god.Name);
        await SendCharacterStats(remote, stream, ciphers, conn, $"Pantheon {god.Name}");
    }

    static async Task SendGods(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
    {
        var catalog = PantheonCatalog.Default.Value;
        byte major, minor;
        lock (conn.Monsters) (major, minor) = (catalog.Wire(conn.PantheonMajor), catalog.Wire(conn.PantheonMinor));
        await Send(remote, stream, ciphers, InstanceEntities.PlayerPantheonUpdate(PlayerId, major, minor),
            $"0x02ba player Pantheon: major {major}, minor {minor} ({why})");
    }

    /// <summary>The chosen gods' stats on top of the character's other sources (PantheonCatalog.ChosenStats): a god whose
    /// unlock flag the character lost grants nothing, the souls' upgrades count once EndGameAvailable is earned.</summary>
    internal static Dictionary<uint, int> WithGods(ClientState conn, Dictionary<uint, int> sources)
    {
        foreach (var (key, value) in PantheonCatalog.Default.Value.ChosenStats(conn, f => ClientHasQuestFlag(conn, f)))
            sources[key] = (int)Math.Clamp((long)sources.GetValueOrDefault(key) + value, int.MinValue, int.MaxValue);
        return sources;
    }

    /// <summary>/state's "pantheon": the chosen gods (Ids, null = none), the unlocked ones (majors first) and whether a god
    /// may be chosen here.</summary>
    internal static JsonObject Json(ClientState conn)
    {
        bool Has(string f) => ClientHasQuestFlag(conn, f);
        var unlocked = PantheonCatalog.Default.Value.Gods.Where(g => PantheonCatalog.Unlocked(g, Has)).OrderBy(g => !g.Major).ThenBy(g => g.Row);
        return new()
        {
            ["major"] = conn.PantheonMajor,
            ["minor"] = conn.PantheonMinor,
            ["unlocked"] = new JsonArray([.. unlocked.Select(g => (JsonNode?)new JsonObject
                { ["id"] = g.Id, ["name"] = g.Name, ["major"] = g.Major, ["row"] = g.Row })]),
            ["canChoose"] = CanChoose(conn),
        };
    }
}
