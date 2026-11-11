using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Delve;

public sealed partial class DelveModule
{
    // Dedicated IDs make a module reload replace its own presentation rather than leave duplicate deposits/Nikos.
    const uint MineralFirstId = 0x0d100000, WildNikoId = MineralFirstId + 3;
    sealed record Deposit(uint Id, int Index, int X, int Y, string Type);
    sealed class SulphiteEncounter
    {
        public required MapDevice.OpenMap Map;
        public required string Owner;
        public required Deposit[] Deposits;
        public readonly HashSet<uint> Depleted = [];
        public readonly HashSet<uint> Enraged = [];
        public int NikoAt = -1;
        public double GaugeAt = double.NegativeInfinity;
    }
    readonly ConditionalWeakTable<ClientState, SulphiteEncounter> _sulphite = new();

    void RegisterSulphite(IModuleContext module)
    {
        GameEvents.AreaGenerated.Add(module.Manifest.Id, conn => EnsureSulphite(module, PlayerOf(conn)));
        GameEvents.Tick.Add(module.Manifest.Id, async (connection, _) =>
        {
            var p = PlayerOf(connection);
            await EnsureSulphite(module, p);
            var conn = ConnOf(p);
            if (!_sulphite.TryGetValue(conn, out var encounter)) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (now >= encounter.GaugeAt)
            {
                // Outside a mine the native HUD times out its transient resource bar. A loss-side UI event refreshes
                // that timeout without a gain popup; its target is always the real saved balance, never a debit.
                ushort amount = Sulphite(WorldOf(p).CharacterData);
                await p.Send(InstanceDelve.SulphiteChanged(amount < ushort.MaxValue ? (ushort)(amount + 1) : amount, amount));
                encounter.GaugeAt = now + 3;
            }
            foreach (var deposit in encounter.Deposits)
                if (conn.Instance.HasState(MinedKey(deposit.Index)) && encounter.Depleted.Add(deposit.Id))
                {
                    await ShowDeposit(module, p, encounter, deposit, replace: true);
                    await MoveNiko(module, p, encounter, deposit);
                }
            // Shared monsters keep their encounter stats across a Delve module reload and a new observer joining.
            List<(uint Id, ushort Buff)> shown = [];
            lock (conn.Monsters)
                foreach (uint id in conn.AwakeMonsters)
                    if (conn.Monsters.TryGetValue(id, out var m) && m.Life > 0
                        && m.EncounterStats?.GetValueOrDefault(MapSulphite.EnragedQuantity) >= MapSulphite.FumesQuantity
                        && encounter.Enraged.Add(id)) shown.Add((id, conn.NextBuff++));
            foreach (var (id, buff) in shown)
                await global::Game.SendWorldRecord(p, id, InstanceEntities.AddBuff(id, buff, 2420), "Sulphite Madness");
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, (conn, use, at) => MineSulphite(module, conn, use.TargetId, at));
        module.Lifetime.Own(GameEvents.StateBuilding.Add(module.Manifest.Id, (conn, json) =>
        {
            if (!_sulphite.TryGetValue(conn, out var encounter)) return;
            json["sulphite"] = new JsonObject
            {
                ["amount"] = Sulphite(WorldOf(PlayerOf(conn)).CharacterData),
                ["capacity"] = Capacity(WorldOf(PlayerOf(conn))),
                ["owner"] = encounter.Owner,
                ["yield"] = MapSulphite.Yield(encounter.Map.Area.Level, encounter.Map.Mods, CharacterName(conn).Equals(encounter.Owner, StringComparison.OrdinalIgnoreCase)),
                ["fumes"] = MapSulphite.Fumes(encounter.Map.Mods),
                ["deposits"] = new JsonArray(encounter.Deposits.Select(d => (JsonNode)new JsonObject
                {
                    ["id"] = d.Id, ["x"] = d.X, ["y"] = d.Y, ["type"] = d.Type,
                    ["mined"] = conn.Instance.HasState(MinedKey(d.Index)),
                }).ToArray()),
            };
        }));
    }

    static int Capacity(IPlayerWorld world) => Math.Clamp(DelveEquipment.Value(Equipment(world.AccountData), 0), 0, ushort.MaxValue);
    static string MinedKey(int index) => $"delve.sulphite.mined.{index}";

    async Task EnsureSulphite(IModuleContext module, IPlayer player)
    {
        var conn = ConnOf(player);
        if (_sulphite.TryGetValue(conn, out _) || !conn.PlayerSent || conn.Closed || conn.LeavingArea || conn.Instance is null) return;
        var opened = MapDevice.InInstance(conn.Layout.Hash, conn.InstanceNumber);
        if (opened is null || !MapSulphite.Present(opened.Value.Map.Mods)) return;
        if (!GameServer.Core.ClientTables.Exists("data/tables/DelveResourcePerLevel.json") || !GameServer.Core.ClientTables.Exists("data/tables/DelveUpgrades.json")) return;
        var positions = MapSulphite.Positions(conn.Layout, conn.Seed);
        var encounter = new SulphiteEncounter
        {
            Map = opened.Value.Map, Owner = opened.Value.Owner,
            Deposits = positions.Select((p, i) => new Deposit(MineralFirstId + (uint)i, i, p.X, p.Y, (conn.Seed + i) % 3 == 0 ? MapSulphite.Chest : MapSulphite.Vein)).ToArray(),
        };
        // Generated/tick hooks for one connection are sequential. Publish before sending so a later tick cannot duplicate it.
        _sulphite.Add(conn, encounter);
        foreach (var deposit in encounter.Deposits)
        {
            await ShowDeposit(module, player, encounter, deposit, replace: true);
            if (conn.Instance.HasState(MinedKey(deposit.Index))) encounter.Depleted.Add(deposit.Id);
            if (MapSulphite.Fumes(encounter.Map.Mods) && !conn.Instance.HasState(MinedKey(deposit.Index))
                && conn.Instance.SetState($"delve.sulphite.guards.{deposit.Index}"))
                await SulphiteGuards(conn, encounter, deposit);
        }
        if (encounter.Deposits.Length > 0) await MoveNiko(module, player, encounter, encounter.Deposits[0]);
        await player.Send(DelveData(WorldOf(player)), "Map sulphite and equipment");
        module.Log($"Sulphite: {encounter.Deposits.Length} deposits in {conn.Layout.Id}/{conn.Seed}, owner {encounter.Owner}, level {encounter.Map.Area.Level}, quantity {encounter.Map.Mods.Quantity}%, owner bonus {encounter.Map.Mods.Get("map_owner_sulphite_gained_+%")}%");
    }

    static async Task ShowDeposit(IModuleContext module, IPlayer player, SulphiteEncounter encounter, Deposit deposit, bool replace)
    {
        var conn = ConnOf(player); var world = WorldOf(player);
        if (replace) await RemoveSceneObject(player, deposit.Id);
        bool mined = conn.Instance.HasState(MinedKey(deposit.Index));
        await global::Game.SendWorldCreate(player, deposit.Id, InstanceEntities.CreateObject(deposit.Id, InstanceEntities.ObjectTypeHash(deposit.Type),
            MapSulphite.Blob(deposit.X, deposit.Y, conn.Layout.GroundHeight(deposit.X, deposit.Y), mined, MapSulphite.Fumes(encounter.Map.Mods))),
            $"Sulphite deposit {deposit.Index + 1} ({deposit.X}, {deposit.Y}), mined={mined}");
        await global::Game.SendWorldRecord(player, deposit.Id, InstanceProtocol.EnterWakeZone(deposit.Id));
        world.SetBlocker(deposit.Id, deposit.X, deposit.Y, 4);
    }

    static async Task MoveNiko(IModuleContext module, IPlayer player, SulphiteEncounter encounter, Deposit deposit)
    {
        if (encounter.NikoAt == deposit.Index) return;
        var conn = ConnOf(player); var world = WorldOf(player);
        string type = "Metadata/NPC/League/Delve/DelveMinerWild" + (deposit.Type == MapSulphite.Chest ? "Chest" : "Vein");
        if (Npcs.Value.Find(type) is not { Supported: true } npc) return;
        var at = conn.Layout.Near((deposit.X + 9, deposit.Y));
        float rotation = MathF.Atan2(deposit.X - at.X, -(deposit.Y - at.Y));
        await RemoveSceneObject(player, WildNikoId);
        world.RemoveBlocker(WildNikoId);
        // Let Positioned follow the client's ground, as town NPCs do; a fixed Z pins Wild Niko above uneven terrain.
        var obj = new NpcObject(at.X, at.Y, npc.Components!, rotation, npc.StateMachine, npc.TransitionState, npc.Model);
        await global::Game.SendWorldCreate(player, WildNikoId, InstanceEntities.CreateObject(WildNikoId, InstanceEntities.ObjectTypeHash(type), obj.Blob()), "Niko at sulphite deposit");
        await global::Game.SendWorldRecord(player, WildNikoId, InstanceProtocol.EnterWakeZone(WildNikoId));
        lock (conn.Npcs) conn.Npcs[WildNikoId] = new SpawnedNpc(npc, at.X, at.Y);
        world.SetBlocker(WildNikoId, at.X, at.Y, 2);
        encounter.NikoAt = deposit.Index;
    }

    async Task<bool> MineSulphite(IModuleContext module, ClientState conn, uint objectId, double requestedAt)
    {
        if (!_sulphite.TryGetValue(conn, out var encounter) || encounter.Deposits.FirstOrDefault(d => d.Id == objectId) is not { } deposit) return false;
        if (conn.PlayerLife == 0 || conn.LeavingArea || conn.Closed || !module.Lifetime.IsActive) return true;
        var world = WorldOf(PlayerOf(conn));
        if (!await world.WalkUpTo(deposit.X, deposit.Y, 4, requestedAt)) return true;
        var recipients = InGame.Keys.Where(p => ReferenceEquals(p.Instance, conn.Instance) && p.PlayerSent && !p.LeavingArea && !p.Closed && p.PlayerLife > 0
            && LeagueFor(p).Modules.Contains(module.Manifest.Id) && MapSulphite.Distance(p.Walk.Position(p.Clock.Elapsed.TotalSeconds), (deposit.X, deposit.Y)) <= 150).ToList();
        if (!recipients.Contains(conn)) recipients.Add(conn);
        List<(ClientState Conn, ushort Before, ushort After)> awards = [];
        // The instance lock serializes simultaneous clicks, claims and the durable resource credit. No network awaits here.
        lock (conn.Monsters)
        {
            if (!module.Lifetime.IsActive || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || conn.Instance.HasState(MinedKey(deposit.Index))) return true;
            Saves.Atomically(() =>
            {
                foreach (var recipient in recipients)
                {
                    var recipientWorld = WorldOf(PlayerOf(recipient));
                    int gain = MapSulphite.Yield(encounter.Map.Area.Level, encounter.Map.Mods,
                        CharacterName(recipient).Equals(encounter.Owner, StringComparison.OrdinalIgnoreCase), recipients.Count);
                    ushort before = 0, after = 0;
                    recipientWorld.CharacterData.Change(SulphiteKey, old =>
                    {
                        before = ushort.TryParse(old, out var n) ? n : (ushort)0;
                        after = MapSulphite.Credit(before, gain, Capacity(recipientWorld));
                        return after.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    });
                    awards.Add((recipient, before, after));
                }
                return true;
            });
            conn.Instance.SetState(MinedKey(deposit.Index));
            if (MapSulphite.Fumes(encounter.Map.Mods))
                foreach (var monster in conn.Monsters.Values.Where(m => m.Hostile && m.Life > 0 && MapSulphite.Distance(m.Walk.Position(conn.Clock.Elapsed.TotalSeconds), (deposit.X, deposit.Y)) <= MapSulphite.FumesRadius))
                {
                    var stats = monster.EncounterStats is null ? new Dictionary<string, int>() : new Dictionary<string, int>(monster.EncounterStats);
                    stats[MapSulphite.EnragedQuantity] = Math.Max(MapSulphite.FumesQuantity, stats.GetValueOrDefault(MapSulphite.EnragedQuantity));
                    monster.EncounterStats = stats;
                }
        }
        foreach (var (recipient, before, after) in awards)
        {
            var p = PlayerOf(recipient);
            await p.Send(InstanceDelve.SulphiteChanged(before, after), $"Sulphite mined: {before} -> {after} (deposit {deposit.Index + 1})");
            var wire = recipient.Wire;
            await SetQuestFlags(wire.Remote, wire.Stream, wire.Ciphers, recipient, ["NikoHaveMinedSulphite"]);
        }
        await global::Game.SendWorldRecord(PlayerOf(conn), deposit.Id, InstanceEntities.StateMachineUpdate(deposit.Id, 5, MapSulphite.States(true, MapSulphite.Fumes(encounter.Map.Mods))), "Mine sulphite deposit");
        await MoveNiko(module, PlayerOf(conn), encounter, deposit);
        await global::Game.SendWorldRecord(PlayerOf(conn), WildNikoId, InstanceProtocol.AnimationUpdate(WildNikoId, 0, deposit.Type == MapSulphite.Chest ? 7u : 2u, 1), "Niko mines the sulphite");
        return true;
    }

    static async Task SulphiteGuards(ClientState conn, SulphiteEncounter encounter, Deposit deposit)
    {
        // Authored mine inhabitants, not ordinary packs from the surrounding map.
        string[] varieties = ["Metadata/Monsters/Spiders/DelveSpiderPacks", "Metadata/Monsters/Spiders/DelveSpiderPacksMedium"];
        var (remote, stream, ciphers) = conn.Wire;
        for (int i = 0; i < 8 && !conn.Closed && !conn.LeavingArea; i++)
            if (Monsters.Find(varieties[(i + deposit.Index) % varieties.Length]) is { Supported: true } variety)
            {
                double angle = i * Math.Tau / 8;
                var at = conn.Layout.Near((deposit.X + (int)(22 * Math.Cos(angle)), deposit.Y + (int)(22 * Math.Sin(angle))));
                await SpawnMonster(remote, stream, ciphers, conn, variety, (byte)encounter.Map.Area.Level, GameServer.Core.Protocol.Instance.MonsterRarity.Normal, at.X, at.Y);
            }
    }
}
