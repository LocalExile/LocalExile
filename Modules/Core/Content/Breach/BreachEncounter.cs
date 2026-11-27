using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using static GameServer.Modules.Breach.BreachTables;

namespace GameServer.Modules.Breach;

/// <summary>
/// Single host entry point registering all Breach world encounter subsystems (<see cref="BreachEncounter"/>,
/// <see cref="BreachHive"/>, <see cref="BreachFortress"/>, and <see cref="BreachBosses"/>) so <c>BreachModule.cs</c>
/// stays minimal and merge-safe.
/// </summary>
public sealed class BreachWorldHost
{
    public BreachEncounter Unstable { get; } = new();
    public BreachHive Hives { get; } = new();
    public BreachFortress Fortresses { get; } = new();
    public BreachBosses Bosses { get; } = new();

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    public void Clear()
    {
        Unstable.Clear();
        Hives.Clear();
        Fortresses.Clear();
        Bosses.Clear();
    }

    public bool IsActivePause(Player player) =>
        Unstable.IsActivePause(player) || Hives.IsActivePause(player) || Fortresses.IsActivePause(player);

    public async Task<bool> OnActivateEncounterSkill(Player player, uint targetOrIndex) =>
        await Hives.OnActivateEncounterSkill(player, targetOrIndex)
        || await Fortresses.OnActivateEncounterSkill(player, targetOrIndex);

    public void Register(IModuleContext module)
    {
        string id = module.Manifest.Id;
        module.Lifetime.Own(new Cleanup(Clear));
        module.Lifetime.Own(RegisterDeliriumPauseSource(IsActivePause));
        GameEvents.MapOpening.Add(id, BreachAtlas.OnMapOpening);
        GameEvents.FeatureFilled.Add(id, (_, row) => row == SkyHiveFeatureRow);
        GameEvents.AreaPopulated.Add(id, async area =>
        {
            if (await Unstable.Populate(area) is { } why)
                module.Log($"Breach: no unstable breach in {area.Player.Layout.Id} ({why})");
            await Hives.Populate(area);
        });
        GameEvents.AreaGenerated.Add(id, async player =>
        {
            await Fortresses.AreaGenerated(player);
            await Bosses.AreaGenerated(player);
        });
        GameEvents.EnterMessages.Add(id, player =>
            Unstable.EnterMessages(player)
                .Concat(Hives.EnterMessages(player))
                .Concat(Fortresses.EnterMessages(player)));
        GameEvents.PlayerEntered.Add(id, async player =>
        {
            await Unstable.Enter(player);
            await Hives.Enter(player);
            await Fortresses.Enter(player);
            await Bosses.Enter(player);
        });
        GameEvents.AreaTick.Add(id, start =>
        {
            string areaId = start.Layout.Id;
            if (areaId == BossAreaId) return (player, dt) => Bosses.Step(player, dt);
            if (areaId == FortressAreaId) return (player, dt) => Fortresses.Step(player, dt);
            if (!MapDevices.IsMapArea(start.Layout.Hash)) return null;
            return async (player, dt) =>
            {
                await Unstable.Step(player);
                await Hives.Step(player, dt);
                await Fortresses.Step(player, dt);
                await Bosses.StepWild(player, dt);
            };
        });
        GameEvents.MonsterKilled.Add(id, async kill =>
        {
            await Unstable.OnMonsterKilled(kill);
            await Hives.OnMonsterKilled(kill);
            await Fortresses.OnMonsterKilled(kill);
            await Bosses.OnMonsterKilled(kill);
        });
        GameEvents.ObjectUse.Add(id, async (player, use, at) =>
            await Unstable.OnUse(player, use, at)
            || await Hives.OnUse(player, use, at)
            || await Fortresses.OnUse(player, use, at)
            || await Bosses.OnUse(player, use, at));
        GameEvents.Drops.Add(id, drop =>
        {
            Unstable.OnDrops(drop);
            Bosses.OnDrops(drop);
        });
        GameEvents.Experience.Add(id, gain =>
        {
            Unstable.OnExperience(gain);
        });
        // C->S 0x02ca for Rulesets#165 (EstablishedBrequelEncounterRules): BrequelActivateEncounterSkill
        // (payload: u8 0x00, u32 LE skillObjectEntityId — sent both by clicking the floating widget and hotkey #44).
        if (EstablishedRulesetRow is { } estRow)
        {
            Client.OnRuleset(id, estRow, async (channel, payload) =>
            {
                if (!BreachRulesets.TryReadActivateSkillAction(payload.Span, out uint entityId)) return false;
                return await OnActivateEncounterSkill(channel.Player, entityId);
            });
        }
    }
}

/// <summary>
/// An area instance's Unstable Breaches: kill-driven progress, collapse timer extension, BrequelStabiliser &amp;
/// Grasping Coffer (<c>BrequelChestBase</c>) spawns, Hiveblood + Wombgift rewards, enraged rares, Wild Breach Boss
/// (Vruun, Marshal of Xesht), and full Atlas + Breach Scarab (<c>ScarabBreachNew2/3/4/6</c>) support.
/// </summary>
public sealed class BreachEncounter
{
    const string StateKey = "Breach.Encounter";
    const uint RollSalt = 0x42726561;

    /// <summary>Kill score required to reach 100% stabilisation (BreachRulesets.Progress(965) ~= 100).</summary>
    const double StabiliseScore = 965.0;

    /// <summary>One Clasped Hand / Grasping Coffer spawned inside an Unstable Breach.</summary>
    public sealed class BreachChest
    {
        public uint Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public float Rotation { get; set; }
        public string Model { get; set; } = BrequelChestModels[0];
        public bool IsLarge { get; set; }
        public bool Opened { get; set; }
        public bool Created { get; set; }
    }

    /// <summary>One BrequelStabiliser spawned inside an Unstable Breach that boosts progress when activated.</summary>
    public sealed class Stabiliser
    {
        public uint Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool Activated { get; set; }
        public bool Created { get; set; }
    }

    /// <summary>One Unstable Breach of the area.</summary>
    public sealed class Breach
    {
        public byte Index { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public uint Initiator { get; set; }
        public int State { get; set; }
        public double Since { get; set; }
        public double? TouchedAt { get; set; }
        public int Ramp { get; set; }
        public int Line { get; set; } = -1;
        public int Spawned { get; set; }
        public int Packs { get; set; }
        public List<uint> Monsters { get; set; } = [];
        public HashSet<uint> CreditedDeaths { get; set; } = [];
        public double KillScore { get; set; }
        public int Kills { get; set; }
        public double BonusSeconds { get; set; }
        public int OpenedOrder { get; set; }
        public float LastProgress { get; set; }
        public float LastRadius { get; set; }
        public float LastValue { get; set; }
        public bool BossSpawned { get; set; }
        public bool CompletionAwarded { get; set; }
        public List<BreachChest> Chests { get; set; } = [];
        public List<Stabiliser> Stabilisers { get; set; } = [];
        public bool Gone { get; set; }
        [JsonIgnore] public bool Open => State is Expanding or Enraged;
    }

    /// <summary>The instance's Unstable Breaches.</summary>
    public sealed class Encounter
    {
        public List<Breach> Breaches { get; set; } = [];
        public int BreachesOpened { get; set; }
        public bool EnvironmentOn { get; set; }
        [JsonIgnore] internal SemaphoreSlim Gate { get; } = new(1, 1);
        [JsonIgnore] internal Dictionary<byte, bool[]?> Reach { get; } = [];
        [JsonIgnore] internal BreachAtlas? Atlas;
        [JsonIgnore] internal HashSet<uint> AllMonsterIds { get; } = [];
    }

    readonly ConditionalWeakTable<InstanceState, Encounter> encounters = new();
    readonly ConcurrentDictionary<(string Area, byte Level), AreaInfo?> areaInfos = new();
    readonly JsonSerializerOptions serialization = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    static readonly ConditionalWeakTable<MonsterCatalog.Variety, MonsterCatalog.Variety> Transitioned = new();

    public void Clear()
    {
        encounters.Clear();
        areaInfos.Clear();
    }

    static bool Enrage => SystemSettings.Default.Get("breach_enrage") != 0;

    public Encounter? Of(InstanceState instance)
    {
        if (encounters.TryGetValue(instance, out var known)) return known;
        if (!instance.ContentState.TryGetValue(StateKey, out var text)) return null;
        return encounters.GetValue(instance, _ =>
        {
            var restored = JsonSerializer.Deserialize<Encounter>(text, serialization) ?? new();
            foreach (var b in restored.Breaches)
                foreach (uint id in b.Monsters) restored.AllMonsterIds.Add(id);
            return restored;
        });
    }

    public bool IsActivePause(Player player)
    {
        if (player.Instance is not { } instance || !encounters.TryGetValue(instance, out var encounter)) return false;
        for (int i = 0; i < encounter.Breaches.Count; i++)
        {
            var b = encounter.Breaches[i];
            if (!b.Gone && b.Open) return true;
        }
        return false;
    }

    void Save(InstanceState instance, Encounter encounter) =>
        instance.ContentState[StateKey] = JsonSerializer.Serialize(encounter, serialization);

    static BreachAtlas AtlasOf(Player player, Encounter encounter) => encounter.Atlas ??= BreachAtlas.From(player);

    public async Task<string?> Populate(AreaPopulated area)
    {
        var player = area.Player;
        if (player.Instance is not { } instance) return "no instance";
        if (Of(instance) is not null) return "the instance has its Breaches already";
        if (RulesetRow is not { } row || Initiator is not { } initiator) return "the client data has no Breach ruleset or Initiator";
        var (components, states, _) = initiator;
        var layout = player.Layout;
        if (layout.Id is FortressAreaId or BossAreaId or MonasteryAreaId) return "Breach sub-area";
        if (!MapDevices.IsMapArea(layout.Hash)) return "not a map";
        if (layout.Walkable is null) return "no walk map";
        var (count, roll) = Count(area);
        if (count <= 0) return $"the roll gave none ({roll})";
        var rng = new Random(unchecked(area.Seed ^ (int)RollSalt));
        var atlas = BreachAtlas.From(player);
        var taken = area.Taken.ToList();
        var encounter = new Encounter { Atlas = atlas };
        double now = AreaTime(player);
        for (int i = 0; i < Math.Min(count, 32); i++)
        {
            if (AreaPopulation.EncounterSpot(area, taken, rng) is not { } site) break;
            taken.Add(site);
            var breach = new Breach
            {
                Index = (byte)i,
                X = site.X,
                Y = site.Y,
                Initiator = instance.World.TakeObjectId(),
                Since = now,
                Line = Inactive,
            };
            PlanBreachObjects(layout, instance, breach, atlas, rng);
            encounter.Breaches.Add(breach);
        }
        if (encounter.Breaches.Count == 0) return "no open ground for a Breach";
        encounters.AddOrUpdate(instance, encounter);
        Save(instance, encounter);
        var members = Members(player);
        if (!members.Contains(player)) members.Insert(0, player);
        await Broadcast(members, InstanceProtocol.AddRuleset(row), $"0x001f ruleset ExpandingBrequelEncounterRules (row {row})");
        uint type = InstanceEntities.ObjectTypeHash(InitiatorType);
        foreach (var breach in encounter.Breaches)
        {
            var blob = new QuestObject(breach.X, breach.Y, components) { StateMachine = states.Set("encounter_state", Inactive) }.Blob();
            await SendWorldCreate(player, breach.Initiator, () => InstanceEntities.CreateObject(breach.Initiator, type, blob),
                $"0x02bb Breach {breach.Index} Initiator 0x{breach.Initiator:x} at ({breach.X}, {breach.Y})", wake: true, awaitBirth: false);
            await Broadcast(members, LineFor(row, breach, breach.Line, atlas), $"0x02c9 Breach {breach.Index} ruleset idle");
        }
        Console.WriteLine($"[breach] {layout.Id}: {roll} gave {count}; placed {encounter.Breaches.Count}: " +
            string.Join(", ", encounter.Breaches.Select(b => $"({b.X}, {b.Y})")));
        return null;
    }

    static void PlanBreachObjects(AreaLayout layout, InstanceState instance, Breach breach, BreachAtlas atlas, Random rng)
    {
        // Plan 1-2 BrequelStabiliser objects near the breach perimeter (revealed when the breach opens).
        int stabCount = rng.Next(1, 3);
        for (int s = 0; s < stabCount; s++)
        {
            double angle = rng.NextDouble() * Math.Tau;
            double dist = 28 + rng.NextDouble() * 34;
            var cell = layout.Near(((int)Math.Round(breach.X + Math.Cos(angle) * dist),
                (int)Math.Round(breach.Y + Math.Sin(angle) * dist)));
            breach.Stabilisers.Add(new Stabiliser { Id = instance.World.TakeObjectId(), X = cell.X, Y = cell.Y });
        }

        if (atlas.NoChests) return;
        // Plan Grasping Coffers / Clasped Hands (2-4 base + Atlas additional chests/large chests).
        int smallCount = rng.Next(2, 5) + atlas.AdditionalChests;
        int largeCount = (atlas.HasLargeChest || rng.Next(100) < 25 + atlas.LargeChestChancePct ? 1 : 0) + atlas.AdditionalLargeChests;
        if (atlas.HandsAreSmall) { smallCount += largeCount; largeCount = 0; }
        int totalChests = Math.Min(12, smallCount + largeCount);
        for (int c = 0; c < totalChests; c++)
        {
            bool large = c < largeCount;
            double angle = (c + rng.NextDouble() * 0.6) * Math.Tau / Math.Max(1, totalChests);
            double dist = 22 + rng.NextDouble() * 52;
            var cell = layout.Near(((int)Math.Round(breach.X + Math.Cos(angle) * dist),
                (int)Math.Round(breach.Y + Math.Sin(angle) * dist)));
            breach.Chests.Add(new BreachChest
            {
                Id = instance.World.TakeObjectId(),
                X = cell.X,
                Y = cell.Y,
                Rotation = (float)(rng.NextDouble() * Math.Tau),
                Model = BrequelChestModels[large ? BrequelChestModels.Length - 1 : rng.Next(BrequelChestModels.Length)],
                IsLarge = large,
            });
        }
    }

    (int Count, string Roll) Count(AreaPopulated area)
    {
        var player = area.Player;
        if (LeagueChances.Current.LeagueOf(LeagueChances.Breach) is { } league && LeagueChances.Forced(league.Id) is int forced)
            return (LeagueChances.Current.Count(player.Layout.Id, LeagueChances.Breach, new Random(unchecked(area.Seed ^ (int)RollSalt))),
                $"Game_LEAGUE_CHANCES {forced}%");
        var info = areaInfos.GetOrAdd((player.Layout.Id, AreaLevel(player)), static k =>
        {
            try { return AreaInfo.Load(GamePack.Default.TryRead, k.Area, k.Level); }
            catch (Exception e) when (e is ArgumentException or IOException or KeyNotFoundException) { return null; }
        });
        if (info is null) return (0, "area row not read");
        var stats = player.Instance.KeptAreaStats?.ToDictionary(s => (int)s.Key, s => s.Value) ?? [];
        return (UnstableBreaches(info with { Stats = stats }, new LevelRandom(unchecked((uint)area.Seed ^ RollSalt), false)), "the area's roll");
    }

    public static int UnstableBreaches(AreaInfo area, LevelRandom rng)
    {
        if (area.IsTown || area.RandomnessDisabled || area.AreaType is 0x13 or 0x20 or 0xb || area.Hash16 is 0x8f53 or 0x7757
            || area.LeagueChance is null) return 0;
        int S(uint id) => area.Stat((int)id);
        if (S(Stat.IsBlightedMap) != 0 || S(Stat.MapNukeEverything) != 0 || S(Stat.GoAwayOtherLeagues) != 0
            || S(Stat.MapDisableBreachAbyss) != 0 || S(Stat.AtlasKeystoneNoExtraContent) != 0
            || (S(Stat.MapDisableLeagues) & (1 << LeagueChances.Breach)) != 0) return 0;
        bool Roll(int chance) => chance > 0 && (chance >= 100 || rng.Next(100u) < (uint)chance);
        int extra = Math.Max(0, S(Stat.MapContainsAdditionalUnstableBreaches));
        bool rolled = ExtraTerrainFeatures.LeagueCount(area, rng, LeagueChances.Breach) != 0;
        if (rolled)
        {
            bool first = S(Stat.MapFirstBrequelEncounter) != 0 && S(Stat.MapOnlyUnstableBreaches) == 0;
            bool onlyHives = !(S(Stat.MapOnlyBreachHives) == 0 || S(Stat.MapOnlyUnstableBreaches) != 0);
            bool hive = S(Stat.MapContainsBreachHive) != 0 || first
                || (extra == 0 && (S(Stat.MapOnlyUnstableBreaches) == 0 || S(Stat.MapOnlyBreachHives) != 0)
                    && (onlyHives || rng.Next(100u) < 50));
            if (hive) return 0;
        }
        int count = (rolled ? 1 : 0) + extra;
        if (extra != 0 || rolled)
        {
            if (Roll(S(Stat.MapBreachPctChanceFor10AdditionalBreach))) count += 10;
            if (Roll(S(Stat.MapBreachPctChanceFor3AdditionalBreach))) count += 3;
            if (Roll(S(Stat.MapBreachPctChanceFor2AdditionalBreach))) count += 2;
            if (Roll(S(Stat.MapBreachPctChanceFor1AdditionalBreach))) count += 1;
        }
        return count;
    }

    public IEnumerable<(string Log, byte[] Message)> EnterMessages(Player player) =>
        player.Instance is { } instance && RulesetRow is { } row && Of(instance) is { } encounter && encounter.Breaches.Any(b => !b.Gone)
            ? [($"0x001f ruleset ExpandingBrequelEncounterRules (row {row})", InstanceProtocol.AddRuleset(row))] : [];

    public async Task Enter(Player player)
    {
        if (player.Instance is not { } instance || RulesetRow is not { } row || Of(instance) is not { } encounter
            || encounter.Breaches.All(b => b.Gone)) return;
        await encounter.Gate.WaitAsync();
        try
        {
            var atlas = AtlasOf(player, encounter);
            await Send(player, InstanceProtocol.AddRuleset(row), $"0x001f ruleset ExpandingBrequelEncounterRules (row {row})");
            foreach (var breach in encounter.Breaches.Where(b => !b.Gone && b.Line >= 0))
                await Send(player, LineFor(row, breach, breach.Line, atlas), $"0x02c9 Breach {breach.Index} ruleset (state {breach.State})");
            if (encounter.EnvironmentOn && Override(true) is { } on)
                await Send(player, on, "0x00df Breach environment override");
        }
        finally { encounter.Gate.Release(); }
    }

    public async Task Step(Player runner)
    {
        if (runner.Instance is not { } instance || Of(instance) is not { } encounter || !Current(runner) || runner.Paused
            || !IsMonsterRunner(runner)) return;
        if (!encounter.EnvironmentOn && encounter.Breaches.All(b => b.Gone)) return;
        if (!await encounter.Gate.WaitAsync(0)) return;
        try
        {
            if (await Advance(runner, encounter)) Save(instance, encounter);
        }
        finally { encounter.Gate.Release(); }
    }

    /// <summary>Feeds player kills of Breach wave monsters into progress, timer extension, Hiveblood, and Wombgifts.</summary>
    public async Task OnMonsterKilled(MonsterKill kill)
    {
        var player = kill.Player;
        if (player.Instance is not { } instance || Of(instance) is not { } encounter) return;
        if (!encounter.AllMonsterIds.Contains(kill.Monster.ObjectId)) return;
        await encounter.Gate.WaitAsync();
        try
        {
            var breach = encounter.Breaches.FirstOrDefault(b => !b.Gone && b.Monsters.Contains(kill.Monster.ObjectId));
            if (breach is null || !breach.CreditedDeaths.Add(kill.Monster.ObjectId)) return;
            var atlas = AtlasOf(player, encounter);
            await CreditKill(player, breach, kill.Monster.Rarity, (kill.Monster.X, kill.Monster.Y), atlas);
            Save(instance, encounter);
        }
        finally { encounter.Gate.Release(); }
    }

    async Task CreditKill(Player player, Breach breach, MonsterRarity rarity, (int X, int Y) at, BreachAtlas atlas)
    {
        breach.Kills++;
        // Kill score weight and time extension per rarity (keeps the breach alive while clearing fast).
        double score = rarity switch
        {
            MonsterRarity.Unique => 140.0,
            MonsterRarity.Rare => 48.0,
            MonsterRarity.Magic => 18.0,
            _ => 8.5,
        };
        double extend = rarity switch
        {
            MonsterRarity.Unique => 4.0,
            MonsterRarity.Rare => 1.5,
            MonsterRarity.Magic => 0.45,
            _ => 0.18,
        };
        breach.KillScore += score;
        breach.BonusSeconds = Math.Min(35.0, breach.BonusSeconds + extend);

        // Award Hiveblood on every Breach monster kill (scaled by rarity + Atlas).
        int baseBlood = rarity switch
        {
            MonsterRarity.Unique => 45,
            MonsterRarity.Rare => 16,
            MonsterRarity.Magic => 5,
            _ => 2,
        };
        await GiveHiveblood(player, atlas.ScaleHiveblood(player, baseBlood, enragedBonus: breach.State == Enraged));

        // Wombgift chance on Magic/Rare/Unique kills
        double giftChance = rarity switch
        {
            MonsterRarity.Unique => 0.55,
            MonsterRarity.Rare => 0.18,
            MonsterRarity.Magic => 0.03,
            _ => 0.004,
        } * atlas.WombgiftChanceFactor(enragedBonus: breach.State == Enraged);
        var rng = AreaRng(player);
        if (rng.NextDouble() < Math.Min(0.95, giftChance))
        {
            string baseId = atlas.RollWombgiftBaseId(player, rng);
            if (MakeWombgift(player, baseId, AreaLevel(player), rng) is { } gift)
                await DropGroundItem(player, at, gift, "Unstable Breach kill Wombgift");
        }
    }

    /// <summary>Honors Atlas Breach monster drop quantity/rarity/unique chance on Breach wave monsters.</summary>
    public void OnDrops(DropRoll drop)
    {
        if (drop.Player.Instance is not { } instance || Of(instance) is not { } encounter) return;
        if (!encounter.AllMonsterIds.Contains(drop.Monster.ObjectId)) return;
        var atlas = AtlasOf(drop.Player, encounter);
        if (atlas.MonsterQuantityPct != 0)
            drop.Quantity *= Math.Max(0.25, 1.0 + atlas.MonsterQuantityPct / 100.0);
        if (atlas.BrequelMonsterRarityPct != 0)
            drop.Rarity *= Math.Max(0.25, 1.0 + atlas.BrequelMonsterRarityPct / 100.0);
        if (atlas.MonsterUniqueItemDropChancePct != 0)
            drop.Rarity *= Math.Max(0.25, 1.0 + atlas.MonsterUniqueItemDropChancePct / 200.0);
    }

    /// <summary>Honors <c>map_breach_monster_granted_experience_+%</c> on Breach wave monsters.</summary>
    public void OnExperience(ExperienceGain gain)
    {
        if (gain.Player.Instance is not { } instance || Of(instance) is not { } encounter) return;
        if (!encounter.AllMonsterIds.Contains(gain.Monster.ObjectId)) return;
        var atlas = AtlasOf(gain.Player, encounter);
        if (atlas.MonsterGrantedExperiencePct != 0)
            gain.Experience = (uint)Math.Max(1, Math.Round(gain.Experience * Math.Max(0.25, 1.0 + atlas.MonsterGrantedExperiencePct / 100.0)));
    }

    /// <summary>Handles clicking Grasping Coffers (<c>BrequelChestBase</c>) and <c>BrequelStabiliser</c> objects inside Unstable Breaches.</summary>
    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is not { } instance || Of(instance) is not { } encounter) return false;
        Breach? ownerBreach = null;
        BreachChest? chest = null;
        Stabiliser? stab = null;
        foreach (var b in encounter.Breaches)
        {
            if (b.Gone) continue;
            if (b.Chests.FirstOrDefault(c => c.Created && c.Id == use.TargetId) is { } foundChest)
            {
                ownerBreach = b; chest = foundChest; break;
            }
            if (b.Stabilisers.FirstOrDefault(s => s.Created && s.Id == use.TargetId) is { } foundStab)
            {
                ownerBreach = b; stab = foundStab; break;
            }
        }
        if (ownerBreach is null) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
        var cell = chest is not null ? (chest.X, chest.Y) : (stab!.X, stab.Y);
        int reach = chest is not null ? 4 : 15;
        if (!TryBeginObjectWalk(player, requestedAt, cell.X, cell.Y, reach, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, cell.X, cell.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), null,
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await encounter.Gate.WaitAsync();
        try
        {
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
            var atlas = AtlasOf(player, encounter);
            var rng = AreaRng(player);
            if (chest is not null && !chest.Opened)
            {
                chest.Opened = true;
                if (Spec(ChestBaseType) is { ChestIndex: >= 0 } spec)
                    await SendWorldRecord(player, chest.Id, InstanceEntities.ComponentUpdate(chest.Id, (byte)spec.ChestIndex, [1, 0, 0]), null);
                await SetObjectStates(player, chest.Id, ChestBaseType, ("open", 1));
                await DropBreachChestRewards(player, (chest.X, chest.Y), AreaLevel(player), atlas, rng, isLarge: chest.IsLarge);
                // map_breach_%_chance_to_spawn_breachlord_on_opening_large_chest
                if (chest.IsLarge && !ownerBreach.BossSpawned && !atlas.NoBosses
                    && atlas.SpawnBossOnLargeChestPct > 0 && rng.Next(100) < atlas.SpawnBossOnLargeChestPct)
                {
                    ownerBreach.BossSpawned = true;
                    await BreachBosses.SpawnWildBoss(player, (chest.X, chest.Y), AreaLevel(player), atlas, rng);
                }
                Save(instance, encounter);
            }
            else if (stab is not null && !stab.Activated && ownerBreach.Open)
            {
                stab.Activated = true;
                ownerBreach.KillScore += 180.0;
                ownerBreach.BonusSeconds = Math.Min(35.0, ownerBreach.BonusSeconds + 5.0);
                await SetObjectStates(player, stab.Id, StabiliserType, ("interaction_state", 1), ("bomb_defence", 2));
                await GiveHiveblood(player, atlas.ScaleHiveblood(player, 25));
                Save(instance, encounter);
            }
        }
        finally
        {
            encounter.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    async Task<bool> Advance(Player runner, Encounter encounter)
    {
        double now = AreaTime(runner);
        var members = Members(runner);
        byte row = RulesetRow!.Value;
        var atlas = AtlasOf(runner, encounter);
        bool changed = false;
        foreach (var breach in encounter.Breaches)
        {
            if (breach.Gone) continue;
            switch (breach.State)
            {
                case Inactive:
                    if (encounter.Breaches.Count(b => !b.Gone && b.Open) >= MaxOpen || !members.Any(m => Touches(m, breach))) break;
                    breach.TouchedAt = now;
                    breach.OpenedOrder = ++encounter.BreachesOpened;
                    if (!encounter.EnvironmentOn && Override(true) is { } on)
                    {
                        await Broadcast(members, on, $"0x00df Breach environment override [{EnvRow} slot 0], {EnvSeconds:0.0} s");
                        encounter.EnvironmentOn = true;
                    }
                    await SetState(runner, breach, Expanding, now);
                    await RevealBreachObjects(runner, breach);
                    changed = true;
                    break;

                case Expanding:
                    // Fallback death sync in case any wave monster died outside standard Kill dispatch.
                    await SyncMissedDeaths(runner, breach, atlas);
                    // Proximity activation of Stabilisers when player walks onto them
                    await CheckStabiliserProximity(runner, members, breach, atlas);
                    await Ramp(members, row, breach, atlas, now);
                    await SpawnWave(runner, encounter, breach, atlas, now);
                    double age = now - breach.TouchedAt!.Value;
                    if (age < ClipActivate) break;
                    double effectiveCollapse = atlas.EffectiveCollapseSeconds + breach.BonusSeconds;
                    bool stabilisedByKills = breach.KillScore >= StabiliseScore;
                    bool rampFinished = breach.Ramp >= BreachRulesets.Steps;
                    if (Enrage && (stabilisedByKills || rampFinished))
                    {
                        await SetState(runner, breach, Enraged, now);
                        await SendLine(members, row, breach, Enraged, atlas, "enraged (stabilised)");
                        await SpawnEnrageReinforcements(runner, encounter, breach, atlas);
                        await AwardCompletion(runner, members, breach, atlas, stabilised: true);
                        changed = true;
                    }
                    else if (age >= effectiveCollapse)
                    {
                        await SetState(runner, breach, Closing, now);
                        await SendLine(members, row, breach, Closing, atlas, "collapsed");
                        await AwardCompletion(runner, members, breach, atlas, stabilised: false);
                        changed = true;
                    }
                    break;

                case Enraged:
                    await SyncMissedDeaths(runner, breach, atlas);
                    await SpawnWave(runner, encounter, breach, atlas, now);
                    if (now - breach.Since < EnragedSeconds + 3.0) break;
                    await SetState(runner, breach, ClosingEnraged, now);
                    await SendLine(members, row, breach, ClosingEnraged, atlas, "closing enraged");
                    changed = true;
                    break;

                default:
                    if (now - breach.Since < RemoveAfter) break;
                    await RemoveWave(runner, encounter, breach);
                    await RemoveStabilisers(runner, breach);
                    await SendWorldRemoval(runner, breach.Initiator, $"0x02bd Breach {breach.Index} Initiator 0x{breach.Initiator:x} removed");
                    breach.Gone = true;
                    changed = true;
                    break;
            }
        }
        if (encounter.EnvironmentOn && !encounter.Breaches.Any(b => !b.Gone && b.Open))
        {
            await Broadcast(members, InstanceEnvironments.Data(), "0x00df Breach environment override cleared");
            encounter.EnvironmentOn = false;
            changed = true;
        }
        return changed;
    }

    async Task RevealBreachObjects(Player runner, Breach breach)
    {
        foreach (var stab in breach.Stabilisers)
        {
            if (stab.Created) continue;
            if (await CreateWorldObject(runner, stab.Id, StabiliserType, stab.X, stab.Y,
                log: $"0x02bb Breach {breach.Index} Stabiliser 0x{stab.Id:x} at ({stab.X}, {stab.Y})",
                states: [("interaction_state", 0), ("bomb_defence", 0)]))
                stab.Created = true;
        }
        foreach (var chest in breach.Chests)
        {
            if (chest.Created) continue;
            if (await CreateWorldObject(runner, chest.Id, ChestBaseType, chest.X, chest.Y, chest.Rotation,
                model: chest.Model, opened: chest.Opened,
                log: $"0x02bb Breach {breach.Index} Clasped Hand 0x{chest.Id:x} at ({chest.X}, {chest.Y})",
                states: [("open", chest.Opened ? 1 : 0)]))
                chest.Created = true;
        }
    }

    async Task CheckStabiliserProximity(Player runner, List<Player> members, Breach breach, BreachAtlas atlas)
    {
        double now = AreaTime(runner);
        foreach (var stab in breach.Stabilisers)
        {
            if (!stab.Created || stab.Activated) continue;
            bool near = members.Any(m => m.PlayerLife > 0 && !InGracePeriod(m)
                && double.Hypot(stab.X - m.Walk.Position(now).X, stab.Y - m.Walk.Position(now).Y) <= 10);
            if (!near) continue;
            stab.Activated = true;
            breach.KillScore += 180.0;
            breach.BonusSeconds = Math.Min(35.0, breach.BonusSeconds + 5.0);
            await SetObjectStates(runner, stab.Id, StabiliserType, ("interaction_state", 1), ("bomb_defence", 2));
            foreach (var m in members)
                await GiveHiveblood(m, atlas.ScaleHiveblood(m, 25));
        }
    }

    async Task SyncMissedDeaths(Player runner, Breach breach, BreachAtlas atlas)
    {
        foreach (uint id in breach.Monsters)
        {
            if (breach.CreditedDeaths.Contains(id)) continue;
            if (runner.Area.Monsters.TryGetValue(id, out var m))
            {
                if (!m.Dead && m.Life > 0) continue;
                breach.CreditedDeaths.Add(id);
                await CreditKill(runner, breach, m.Rarity, m.Walk.Position(AreaTime(runner)), atlas);
            }
        }
    }

    async Task SpawnEnrageReinforcements(Player runner, Encounter encounter, Breach breach, BreachAtlas atlas)
    {
        byte level = AreaLevel(runner);
        var rng = new Random(unchecked((int)(runner.Seed ^ (uint)(breach.Index * 4999) ^ 0x454e5247u)));
        var extra = atlas.BuildMonsterMods(level, isBoss: false, breachesOpened: breach.OpenedOrder, enraged: true);

        // Spawn enraged rare monsters (1 base + map_unstable_breach_enrage_x_additional_rare_monsters + AdditionalRareMonsters)
        int rares = 1 + atlas.EnrageAdditionalRareMonsters + atlas.AdditionalRareMonsters;
        if (BreachTables.Packs.Count > 0)
        {
            for (int i = 0; i < rares; i++)
            {
                var pack = BreachTables.Packs[rng.Next(BreachTables.Packs.Count)];
                if (pack.Entries.Count == 0) continue;
                var entry = pack.Entries[rng.Next(pack.Entries.Count)];
                if (Monsters.Find(entry.Variety.ToString()) is not { Supported: true } variety) continue;
                double angle = i * Math.Tau / Math.Max(1, rares) + rng.NextDouble() * 0.4;
                var at = runner.Layout.Near(((int)Math.Round(breach.X + Math.Cos(angle) * 20),
                    (int)Math.Round(breach.Y + Math.Sin(angle) * 20)));
                uint id = await SpawnMonster(runner, Risen(variety), level, MonsterRarity.Rare, at.X, at.Y,
                    alone: false, extraMods: extra);
                if (id == 0) continue;
                breach.Monsters.Add(id);
                encounter.AllMonsterIds.Add(id);
            }
        }

        // Wild Breach Boss: Vruun, Marshal of Xesht (ScarabBreachNew2 Dreamer / ScarabBreachNew4 Marshal / HasBoss / 15% natural)
        if (!breach.BossSpawned && !atlas.NoBosses
            && (atlas.UnstableBreachSpawnsBoss || atlas.HasBoss || rng.Next(100) < 15))
        {
            breach.BossSpawned = true;
            var bossAt = runner.Layout.Near((breach.X + 8, breach.Y + 8));
            await BreachBosses.SpawnWildBoss(runner, bossAt, level, atlas, rng);
            if (atlas.EffectiveBossDuplicateChancePct > 0 && rng.Next(100) < atlas.EffectiveBossDuplicateChancePct)
            {
                var dupAt = runner.Layout.Near((breach.X - 8, breach.Y - 8));
                await BreachBosses.SpawnWildBoss(runner, dupAt, level, atlas, rng);
            }
        }
    }

    async Task AwardCompletion(Player runner, List<Player> members, Breach breach, BreachAtlas atlas, bool stabilised)
    {
        if (breach.CompletionAwarded) return;
        breach.CompletionAwarded = true;
        byte level = AreaLevel(runner);
        var rng = AreaRng(runner);
        int baseBlood = stabilised ? 75 + Math.Min(100, breach.Kills * 2) : 20 + Math.Min(50, breach.Kills);
        foreach (var member in members)
        {
            await GiveHiveblood(member, atlas.ScaleHiveblood(member, baseBlood, enragedBonus: stabilised));
            if (stabilised)
            {
                // Guaranteed Wombgift on stabilised Unstable Breach, plus chance for a second with Atlas bonuses.
                double bonusChance = Math.Min(1.5, 0.75 * atlas.WombgiftChanceFactor(enragedBonus: true));
                int drops = bonusChance >= 1.0 ? 1 + (rng.NextDouble() < bonusChance - 1.0 ? 1 : 0) : (rng.NextDouble() < bonusChance ? 1 : 0);
                for (int d = 0; d < drops; d++)
                {
                    string giftId = atlas.RollWombgiftBaseId(member, rng);
                    if (MakeWombgift(member, giftId, level, rng) is { } gift)
                        await DropGroundItem(member, (breach.X, breach.Y), gift, "Stabilised Breach Wombgift");
                }
                if (TryRollBodyPart?.Invoke(member, level, rng) is { } bodyPart)
                    await DropGroundItem(member, (breach.X, breach.Y), bodyPart, "Breach quest body part");
            }
        }
        Console.WriteLine($"[breach] Breach {breach.Index} {(stabilised ? "stabilised" : "collapsed")} (kills={breach.Kills}, score={breach.KillScore:0.0})");
    }

    static bool Touches(Player player, Breach breach)
    {
        if (player.PlayerLife == 0 || InGracePeriod(player)) return false;
        var (x, y) = player.Walk.Position(AreaTime(player));
        return double.Hypot(breach.X - x, breach.Y - y) <= TouchRadius;
    }

    static async Task SetState(Player runner, Breach breach, int state, double now)
    {
        breach.State = state;
        breach.Since = now;
        var (_, states, index) = Initiator!.Value;
        await SendWorldRecord(runner, breach.Initiator, InstanceEntities.StateMachineUpdate(breach.Initiator, index, states.Set("encounter_state", state)),
            $"0x02bc Breach {breach.Index} Initiator 0x{breach.Initiator:x} encounter_state {state}");
    }

    static async Task Ramp(List<Player> members, byte row, Breach breach, BreachAtlas atlas, double now)
    {
        if (breach.TouchedAt is not { } touched) return;
        double gap = RulesetGap / atlas.TimeSpeedFactor;
        int due = Math.Min(BreachRulesets.Steps, (int)Math.Floor((now - touched) / gap) + 1);
        if (due <= breach.Ramp) return;
        breach.Ramp = due;
        double growth = (double)(due - 1) / (BreachRulesets.Steps - 1);
        // Combine player kill score with a floor baseline ramp so the ring visibly responds to kills while still advancing.
        double effectiveCount = Math.Max(breach.KillScore, BreachRulesets.CountPerStep * (due - 1) * 0.45);
        var (progress, radius, value) = BreachRulesets.KillDrivenMetrics(growth, effectiveCount, atlas.EffectiveMaxRadius);
        breach.LastProgress = progress;
        breach.LastRadius = radius;
        breach.LastValue = value;
        await SendLine(members, row, breach, Expanding, atlas, $"ramp {due}/{BreachRulesets.Steps} p={progress:0.0}");
    }

    static Task SendLine(List<Player> members, byte row, Breach breach, int state, BreachAtlas atlas, string what)
    {
        breach.Line = state;
        return Broadcast(members, LineFor(row, breach, state, atlas), $"0x02c9 Breach {breach.Index} ruleset {what} at ({breach.X}, {breach.Y})");
    }

    static byte[] LineFor(byte row, Breach breach, int state, BreachAtlas atlas)
    {
        byte[] body = state is Expanding or Enraged && breach.LastRadius > 0
            ? BreachRulesets.Body(breach.Index, breach.X, breach.Y, state, breach.LastProgress, breach.LastRadius, breach.LastValue, atlas.EffectiveMaxRadius)
            : BreachRulesets.Body(breach.Index, breach.X, breach.Y, state, Math.Max(0, breach.Ramp - 1));
        return InstanceRulesets.Data(row, body);
    }

    static byte[]? Override(bool on) => !on ? InstanceEnvironments.Data() : EnvRow is { } env ? InstanceEnvironments.Data((env, EnvSeconds)) : null;

    static async Task SpawnWave(Player runner, Encounter encounter, Breach breach, BreachAtlas atlas, double now)
    {
        if (breach.TouchedAt is not { } touched || BreachTables.Packs.Count == 0) return;
        double age = (now - touched) * atlas.TimeSpeedFactor;
        if (age < 0 || age >= WaveSeconds) return;
        double cascade = atlas.ResonantCascadeFactor(breach.OpenedOrder);
        int due = (int)Math.Floor(ExpectedWaveMembers(age) * cascade);
        if (breach.Spawned >= due) return;
        var layout = runner.Layout;
        if (!encounter.Reach.TryGetValue(breach.Index, out var reach) && layout.Walkable is { } walkable)
            encounter.Reach[breach.Index] = reach = AreaPopulation.Reachable(layout.GridW, layout.GridH, walkable, (breach.X, breach.Y));
        bool Open(int x, int y) => x >= 0 && y >= 0 && x < layout.GridW && y < layout.GridH && layout.WalkableFor(x, y, 1)
            && (reach is null || reach[y * layout.GridW + x]);
        byte level = AreaLevel(runner);
        var extra = atlas.BuildMonsterMods(level, isBoss: false, breachesOpened: breach.OpenedOrder, enraged: breach.State == Enraged);
        int budget = MaxWavePerTick;
        double maxDist = Math.Min(WaveMaxDistance * (atlas.EffectiveMaxRadius / BreachRulesets.FullRadius), 150.0);
        using (HoldMonstersRemaining(runner))
        {
            while (breach.Spawned < due && budget > 0)
            {
                var rng = new Random(unchecked((int)(runner.Seed * 0x9e3779b1u ^ (uint)(breach.Index * 7919) ^ (uint)(breach.Packs * 104729))));
                breach.Packs++;
                var pack = BreachTables.Packs[rng.Next(BreachTables.Packs.Count)];
                var rarity = atlas.RollPackRarity(rng);
                (int X, int Y) centre = (breach.X, breach.Y);
                for (int attempt = 0; attempt < 16; attempt++)
                {
                    double angle = rng.NextDouble() * Math.Tau, radius = rng.NextDouble() * maxDist;
                    (int X, int Y) at = ((int)Math.Round(breach.X + radius * Math.Cos(angle)), (int)Math.Round(breach.Y + radius * Math.Sin(angle)));
                    if (Open(at.X, at.Y)) { centre = at; break; }
                }
                double packFactor = (MapModsOf(runner)?.PackSizeFactor ?? 1.0) * cascade;
                var spawns = AreaPopulation.AtPacks([(pack, centre.X, centre.Y, rarity)], Open,
                    v => Monsters.Find(v.ToString()) is { Supported: true }, rng.Next(), packFactor);
                breach.Spawned += Math.Max(1, spawns.Count);
                budget -= Math.Max(1, spawns.Count);
                foreach (var spawn in spawns)
                {
                    if (Monsters.Find(spawn.Variety.ToString()) is not { Supported: true } variety) continue;
                    uint id = await SpawnMonster(runner, Risen(variety), level, spawn.Rarity, spawn.X, spawn.Y,
                        alone: false, extraMods: extra, bulk: true);
                    if (id == 0) continue;
                    breach.Monsters.Add(id);
                    encounter.AllMonsterIds.Add(id);
                    if (runner.Area.Monsters.TryGetValue(id, out var monster)) monster.SummonerId = breach.Initiator;
                }
            }
        }
        await SendRemainingMonsters(runner);
    }

    internal static MonsterCatalog.Variety Risen(MonsterCatalog.Variety variety) =>
        Transitioned.GetValue(variety, v => v with { TransitionStates = MonsterEmerge.RisenState(v) });

    static async Task RemoveStabilisers(Player runner, Breach breach)
    {
        foreach (var stab in breach.Stabilisers)
        {
            if (!stab.Created) continue;
            await SendWorldRemoval(runner, stab.Id, null);
            stab.Created = false;
        }
    }

    static async Task RemoveWave(Player runner, Encounter encounter, Breach breach)
    {
        var area = runner.Area;
        using (HoldMonstersRemaining(runner))
        {
            foreach (uint id in breach.Monsters)
            {
                encounter.AllMonsterIds.Remove(id);
                bool dormant = false;
                if (!area.Monsters.TryGetValue(id, out var monster))
                {
                    if (!area.DormantMonsters.TryGetValue(id, out monster)) continue;
                    dormant = true;
                }
                if (monster.Dead || monster.Life == 0) continue;
                monster.Life = 0;
                monster.Dead = true;
                monster.Aggro = false;
                monster.ActionGeneration++;
                if (dormant) area.DormantMonsters.Remove(id);
                else area.Monsters.Remove(id);
                runner.Instance.World.MonsterOrigins.TryRemove(id, out _);
                await SendWorldRemoval(runner, id, $"0x02bd Breach {breach.Index} wave monster 0x{id:x} removed");
            }
        }
        breach.Monsters.Clear();
        await SendRemainingMonsters(runner);
    }
}
