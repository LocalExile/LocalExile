using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Core;

namespace GameServer.Modules.Delve;

public sealed partial class DelveModule
{
    // Dedicated IDs make a module reload replace its own presentation rather than leave duplicate deposits/Nikos.
    const uint MineralFirstId = 0x0d100000, WildNikoId = MineralFirstId + 3;
    sealed record Deposit(uint Id, int Index, int X, int Y, string Type);
    sealed class SulphiteEncounter
    {
        public required int AreaLevel;
        /// <summary>The map mods the instance plays with (Game.MapModsOf: its area stats), or MapMods.None in the campaign.</summary>
        public required MapMods Mods;
        public required string Owner;
        public required Deposit[] Deposits;
        public readonly HashSet<uint> Depleted = [];
        public readonly HashSet<uint> Enraged = [];
        public int NikoAt = -1;
    }
    sealed class SulphiteState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool Checked;
        public SulphiteEncounter? Encounter;
    }
    sealed class PlayerSulphite
    {
        public bool Joined;
        public double GaugeAt = double.NegativeInfinity;
    }
    readonly RuleTable<InstanceState, SulphiteState> _sulphite = new();
    readonly RuleTable<Player, PlayerSulphite> _playerSulphite = new();

    void RegisterSulphite(IModuleContext module)
    {
        GameEvents.MapOpening.Add(module.Manifest.Id, request =>
        {
            if (!request.Campaign && request.Opening is { } opening && MapSulphite.Eligible(opening.Area.Id)
                && ClientTables.Has("DelveResourcePerLevel") && ClientTables.Has("DelveUpgrades"))
                request.HandledContent.Add(MapScarabContent.Sulphite);
        });
        GameEvents.AreaGenerated.Add(module.Manifest.Id, player => EnsureSulphite(module, PlayerOf(player)));
        GameEvents.PlayerEntered.Add(module.Manifest.Id, player => EnsureSulphite(module, PlayerOf(player)));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => { _playerSulphite.Remove(player); return Task.CompletedTask; });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always(async (player, _) =>
        {
            if (player.Instance is null) return;
            // Fast path: negative-cached non-Sulphite instances do zero work per tick.
            if (!_sulphite.TryGetValue(player.Instance, out var state) || !state.Checked)
            {
                await EnsureSulphite(module, PlayerOf(player));
                if (!_sulphite.TryGetValue(player.Instance, out state)) return;
            }
            if (state.Encounter is not { } encounter) return;
            var p = PlayerOf(player);
            var ps = _playerSulphite.GetOrCreateValue(player);
            if (!ps.Joined) await EnsureSulphite(module, p);
            double now = AreaTime(player);
            if (now >= ps.GaugeAt)
            {
                // Outside a mine the native HUD times out its transient resource bar. A loss-side UI event refreshes
                // that timeout without a gain popup; its target is always the real saved balance, never a debit.
                ushort amount = Sulphite(WorldOf(p).CharacterData);
                await p.Send(InstanceDelve.SulphiteChanged(amount < ushort.MaxValue ? (ushort)(amount + 1) : amount, amount));
                ps.GaugeAt = now + 3;
            }
            foreach (var deposit in encounter.Deposits)
                if (player.Instance.HasState(MinedKey(deposit.Index)) && encounter.Depleted.Add(deposit.Id))
                {
                    await ShowDeposit(module, p, encounter, deposit, replace: true);
                    await MoveNiko(module, p, encounter, deposit);
                }
            // Shared monsters keep their encounter stats across a Delve module reload and a new observer joining.
            List<(uint Id, ushort Buff)> shown = [];
            foreach (uint id in player.Area.Monsters.Where(e => !e.Value.Dead && !e.Value.Asleep).Select(e => e.Key))
                if (player.Area.Monsters.TryGetValue(id, out var m) && m.Life > 0
                    && MapSulphite.Enraged(player.Instance, id)
                    && encounter.Enraged.Add(id)) shown.Add((id, 1));
            foreach (var (id, buff) in shown)
                player.Area.Buffs.Set(id, new(2420));
        }));
        GameEvents.ObjectUse.Add(module.Manifest.Id, (player, use, at) => MineSulphite(module, player, use.TargetId, at));
        module.Lifetime.Own(GameEvents.StateBuilding.Add(module.Manifest.Id, (player, json) =>
        {
            if (player.Instance is null || !_sulphite.TryGetValue(player.Instance, out var state) || state.Encounter is not { } encounter) return;
            json["sulphite"] = new JsonObject
            {
                ["amount"] = Sulphite(WorldOf(PlayerOf(player)).CharacterData),
                ["capacity"] = Capacity(WorldOf(PlayerOf(player))),
                ["owner"] = encounter.Owner,
                ["yield"] = MapSulphite.Yield(encounter.AreaLevel, encounter.Mods, CharacterName(player).Equals(encounter.Owner, StringComparison.OrdinalIgnoreCase)),
                ["fumes"] = MapSulphite.Fumes(encounter.Mods),
                ["enraged"] = new JsonArray(player.Area.Monsters.Keys.Where(id => MapSulphite.Enraged(player.Instance, id))
                    .Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
                ["deposits"] = new JsonArray(encounter.Deposits.Select(d => (JsonNode)new JsonObject
                {
                    ["id"] = d.Id, ["x"] = d.X, ["y"] = d.Y, ["type"] = d.Type,
                    ["mined"] = player.Instance.HasState(MinedKey(d.Index)),
                }).ToArray()),
            };
        }));
    }

    static int Capacity(IPlayerWorld world) => Math.Clamp(DelveEquipment.Value(Equipment(world.AccountData), 0), 0, ushort.MaxValue);
    static string MinedKey(int index) => $"delve.sulphite.mined.{index}";

    async Task EnsureSulphite(IModuleContext module, IPlayer modPlayer)
    {
        var player = PlayerOf(modPlayer);
        if (!player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Instance is null) return;
        var state = _sulphite.GetOrCreateValue(player.Instance);
        var ps = _playerSulphite.GetOrCreateValue(player);
        if (state.Checked && (state.Encounter is null || ps.Joined)) return;
        await state.Gate.WaitAsync();
        try
        {
            if (player.Closed || player.LeavingArea) return;
            if (!state.Checked)
            {
                state.Checked = true;
                if (global::Game.InTown(player) || HideoutCatalog.ByArea(player.Layout.Hash) is not null || player.Layout.Walkable is null)
                    return;
                var opened = global::Game.MapRunOf(player);
                string areaId = opened?.Area.Id ?? player.Layout.Id;
                int areaLevel = Math.Clamp(opened is { Area.Level: > 0 } ? opened.Area.Level : ServerLevels.AreaLevel(areaId), 1, 100);
                var mods = global::Game.MapModsOf(player) ?? opened?.Mods ?? MapMods.None;
                if (!MapSulphite.Present(player, areaId, mods)) return;
                if (!ClientTables.Has("DelveResourcePerLevel") || !ClientTables.Has("DelveUpgrades")) return;
                var positions = MapSulphite.Positions(player.Layout, player.Seed);
                var encounter = new SulphiteEncounter
                {
                    AreaLevel = areaLevel, Owner = opened?.Owner ?? CharacterName(player), Mods = mods,
                    Deposits = positions.Select((p, i) => new Deposit(MineralFirstId + (uint)i, i, p.X, p.Y, (player.Seed + i) % 3 == 0 ? MapSulphite.Chest : MapSulphite.Vein)).ToArray(),
                };
                state.Encounter = encounter;
                foreach (var deposit in encounter.Deposits)
                {
                    await ShowDeposit(module, modPlayer, encounter, deposit, replace: true);
                    if (player.Instance.HasState(MinedKey(deposit.Index))) encounter.Depleted.Add(deposit.Id);
                    if (MapSulphite.Fumes(encounter.Mods) && !player.Instance.HasState(MinedKey(deposit.Index))
                        && player.Instance.SetState($"delve.sulphite.guards.{deposit.Index}"))
                        await SulphiteGuards(player, encounter, deposit);
                }
                if (encounter.Deposits.Length > 0) await MoveNiko(module, modPlayer, encounter, encounter.Deposits[0]);
                await modPlayer.Send(DelveData(WorldOf(modPlayer)), "Map sulphite and equipment");
                ps.Joined = true;
                module.Log($"Sulphite: {encounter.Deposits.Length} deposits in {player.Layout.Id}/{player.Seed}, owner {encounter.Owner}, level {encounter.AreaLevel}, quantity {encounter.Mods.Quantity}%, owner bonus {encounter.Mods.Get(Stat.MapOwnerSulphiteGainedPlusPct)}%");
                return;
            }
            if (state.Encounter is { } existing && !ps.Joined)
            {
                // Late joiner / party member: deposits and Wild Niko are already in AreaObjectRecords, so only attach
                // this player's walk blockers, NPC map entry, and delve resource data without recreating world objects.
                var world = WorldOf(modPlayer);
                foreach (var deposit in existing.Deposits)
                    world.SetBlocker(deposit.Id, deposit.X, deposit.Y, 4);
                if (existing.NikoAt >= 0 && existing.NikoAt < existing.Deposits.Length)
                {
                    var dep = existing.Deposits[existing.NikoAt];
                    string type = "Metadata/NPC/League/Delve/DelveMinerWild" + (dep.Type == MapSulphite.Chest ? "Chest" : "Vein");
                    if (Npcs.Find(type) is { Supported: true } npc)
                    {
                        var at = player.Layout.Near((dep.X + 9, dep.Y));
                        lock (player.Npcs) player.Npcs[WildNikoId] = new SpawnedNpc(npc, at.X, at.Y);
                        world.SetBlocker(WildNikoId, at.X, at.Y, 2);
                    }
                }
                await modPlayer.Send(DelveData(world), "Map sulphite and equipment");
                ps.Joined = true;
            }
        }
        finally { state.Gate.Release(); }
    }

    static async Task ShowDeposit(IModuleContext module, IPlayer modPlayer, SulphiteEncounter encounter, Deposit deposit, bool replace)
    {
        var player = PlayerOf(modPlayer); var world = WorldOf(modPlayer);
        if (replace) await RemoveSceneObject(modPlayer, deposit.Id);
        bool mined = player.Instance.HasState(MinedKey(deposit.Index));
        await global::Game.SendWorldCreate(modPlayer, deposit.Id, InstanceEntities.CreateObject(deposit.Id, InstanceEntities.ObjectTypeHash(deposit.Type),
            MapSulphite.Blob(deposit.X, deposit.Y, player.Layout.GroundHeight(deposit.X, deposit.Y), mined, MapSulphite.Fumes(encounter.Mods))),
            $"Sulphite deposit {deposit.Index + 1} ({deposit.X}, {deposit.Y}), mined={mined}");
        await global::Game.SendWorldRecord(modPlayer, deposit.Id, InstanceProtocol.EnterWakeZone(deposit.Id));
        player.SetObjectBlocker(deposit.Id, deposit.X, deposit.Y, 4, QuestObjectTypes.Find(deposit.Type)?.ActorOrChest == true);
    }

    static async Task MoveNiko(IModuleContext module, IPlayer modPlayer, SulphiteEncounter encounter, Deposit deposit)
    {
        if (encounter.NikoAt == deposit.Index) return;
        var player = PlayerOf(modPlayer); var world = WorldOf(modPlayer);
        string type = "Metadata/NPC/League/Delve/DelveMinerWild" + (deposit.Type == MapSulphite.Chest ? "Chest" : "Vein");
        if (Npcs.Find(type) is not { Supported: true } npc) return;
        var at = player.Layout.Near((deposit.X + 9, deposit.Y));
        float rotation = MathF.Atan2(deposit.X - at.X, -(deposit.Y - at.Y));
        await RemoveSceneObject(modPlayer, WildNikoId);
        world.RemoveBlocker(WildNikoId);
        // Let Positioned follow the client's ground, as town NPCs do; a fixed Z pins Wild Niko above uneven terrain.
        var obj = new NpcObject(at.X, at.Y, npc.Components!, rotation, npc.StateMachine, npc.TransitionState, npc.Model);
        await global::Game.SendWorldCreate(modPlayer, WildNikoId, InstanceEntities.CreateObject(WildNikoId, InstanceEntities.ObjectTypeHash(type), obj.Blob()), "Niko at sulphite deposit");
        await global::Game.SendWorldRecord(modPlayer, WildNikoId, InstanceProtocol.EnterWakeZone(WildNikoId));
        lock (player.Npcs) player.Npcs[WildNikoId] = new SpawnedNpc(npc, at.X, at.Y);
        player.SetObjectBlocker(WildNikoId, at.X, at.Y, 2, npc.Components!.Contains("Actor"));
        encounter.NikoAt = deposit.Index;
    }

    async Task<bool> MineSulphite(IModuleContext module, Player player, uint objectId, double requestedAt)
    {
        if (player.Instance is null || !_sulphite.TryGetValue(player.Instance, out var state)
            || state.Encounter is not { } encounter
            || encounter.Deposits.FirstOrDefault(d => d.Id == objectId) is not { } deposit) return false;
        if (player.PlayerLife == 0 || player.LeavingArea || player.Closed || !module.Lifetime.IsActive) return true;
        var world = WorldOf(PlayerOf(player));
        if (!await world.WalkUpTo(deposit.X, deposit.Y, 4, requestedAt)) return true;
        var recipients = InGame.Keys.Where(p => ReferenceEquals(p.Instance, player.Instance) && p.Connection.PlayerSent && !p.LeavingArea && !p.Closed && p.PlayerLife > 0
            && LeagueFor(p).Modules.Contains(module.Manifest.Id) && MapSulphite.Distance(p.Walk.Position(AreaTime(p)), (deposit.X, deposit.Y)) <= 150).ToList();
        if (!recipients.Contains(player)) recipients.Add(player);
        List<(Player Player, ushort Before, ushort After)> awards = [];
        // Serialize the claim even when uses arrive outside the normal instance message loop.
        await state.Gate.WaitAsync();
        try
        {
            if (!module.Lifetime.IsActive || player.Closed || player.LeavingArea || player.PlayerLife == 0 || player.Instance.HasState(MinedKey(deposit.Index))) return true;
            Saves.Atomically(() =>
            {
                foreach (var recipient in recipients)
                {
                    var recipientWorld = WorldOf(PlayerOf(recipient));
                    int gain = MapSulphite.Yield(encounter.AreaLevel, encounter.Mods,
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
            player.Instance.SetState(MinedKey(deposit.Index));
            if (MapSulphite.Fumes(encounter.Mods))
                foreach (var (id, monster) in player.Area.Monsters)
                    if (monster.Hostile && monster.Life > 0 && !monster.Dead
                        && MapSulphite.Distance(monster.Walk.Position(AreaTime(player)), (deposit.X, deposit.Y)) <= MapSulphite.FumesRadius)
                        MapSulphite.Enrage(player.Instance, id, monster);
        }
        finally { state.Gate.Release(); }
        foreach (var (recipient, before, after) in awards)
        {
            var p = PlayerOf(recipient);
            await p.Send(InstanceDelve.SulphiteChanged(before, after), $"Sulphite mined: {before} -> {after} (deposit {deposit.Index + 1})");
            await SetQuestFlags(recipient, ["NikoHaveMinedSulphite"]);
        }
        await global::Game.SendWorldRecord(PlayerOf(player), deposit.Id, InstanceEntities.StateMachineUpdate(deposit.Id, 5, MapSulphite.States(true, MapSulphite.Fumes(encounter.Mods))), "Mine sulphite deposit");
        await MoveNiko(module, PlayerOf(player), encounter, deposit);
        await global::Game.SendWorldRecord(PlayerOf(player), WildNikoId, InstanceProtocol.AnimationUpdate(WildNikoId, 0, deposit.Type == MapSulphite.Chest ? 7u : 2u, 1), "Niko mines the sulphite");
        return true;
    }

    static async Task SulphiteGuards(Player player, SulphiteEncounter encounter, Deposit deposit)
    {
        // Authored mine inhabitants, not ordinary packs from the surrounding map.
        string[] varieties = ["Metadata/Monsters/Spiders/DelveSpiderPacks", "Metadata/Monsters/Spiders/DelveSpiderPacksMedium"];
        for (int i = 0; i < 8 && !player.Closed && !player.LeavingArea; i++)
            if (Monsters.Find(varieties[(i + deposit.Index) % varieties.Length]) is { Supported: true } variety)
            {
                double angle = i * Math.Tau / 8;
                var at = player.Layout.Near((deposit.X + (int)(22 * Math.Cos(angle)), deposit.Y + (int)(22 * Math.Sin(angle))));
                await SpawnMonster(player, variety, (byte)encounter.AreaLevel, GameServer.Core.Protocol.Instance.MonsterRarity.Normal, at.X, at.Y);
            }
    }
}
