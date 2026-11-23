using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 3 — The Lunaris Temple Level 2 (<c>1_3_14_2</c>) Piety encounter controller:
/// <list type="bullet">
/// <item>Spawns and manages destructible Burning (<c>PietyFireFormPortal</c>, MV#2645) and Chilling
/// (<c>PietyColdFormPortal</c>, MV#2646) portals around Piety's arena markers (<c>PietyTransformMarker</c>).</item>
/// <item>Periodically moves Piety to a living portal and transforms her into Fire Form (<c>piety_fire_form</c> buff 193,
/// melee frenzy + <c>BloodElementalEmerge</c> adds) or Ice Form (<c>piety_ice_form</c> buff 194, chilling ice volleys +
/// <c>DemonModularPietyEmerge</c> adds), then reverts her to normal Lightning form.</item>
/// <item>Players can damage and destroy active portals to cut short or deny transformations; portals respawn on a
/// cooldown while Piety lives, and all portals/adds/buffs clean up on Piety's death.</item>
/// </list>
/// </summary>
public static class PietyLunarisEncounter
{
    public const string Area = "1_3_14_2";
    public const string Boss = "Metadata/Monsters/Axis/PietyBoss";
    public const string FirePortal = "Metadata/Monsters/PietyPortal/PietyFireFormPortal";
    public const string IcePortal = "Metadata/Monsters/PietyPortal/PietyColdFormPortal";
    public const string BloodElemental = "Metadata/Monsters/BloodElemental/BloodElementalEmerge";
    public const string Miscreation = "Metadata/Monsters/DemonModular/DemonModularPietyEmerge";

    const ushort FireFormBuff = 193; // piety_fire_form
    const ushort IceFormBuff = 194;  // piety_ice_form
    const string FormStatsKey = "piety_lunaris_form";

    const string RedTransformArt = "Metadata/Monsters/Axis/Piety/PietyPortal/red_transform.ao";
    const string BlueTransformArt = "Metadata/Monsters/Axis/Piety/PietyPortal/blue_transform.ao";

    static readonly IReadOnlyDictionary<uint, int> FireFormStats = new Dictionary<uint, int>
    {
        [Stat.BaseMovementVelocityPlusPct] = 35,
        [Stat.AttackSpeedPlusPct] = 40,
        [Stat.PhysicalDamagePlusPct] = 35,
    };

    static readonly IReadOnlyDictionary<uint, int> IceFormStats = new Dictionary<uint, int>
    {
        [Stat.BaseCastSpeedPlusPct] = 25,
        [Stat.ColdDamagePlusPct] = 35,
    };

    static readonly RuleTable<InstanceState, Fight> Fights = new();

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (!Fights.TryGetValue(player.Instance, out var fight))
        {
            var found = player.Area.Monsters.FirstOrDefault(p => p.Value.Hostile && p.Value.Variety?.Id == Boss);
            if (found.Value is null) return;
            fight = Fights.GetValue(player.Instance, _ => new Fight(found.Key, found.Value, player.Seed));
        }
        await fight.Gate.WaitAsync();
        try
        {
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    public static Task Leaving(Player player) => Task.CompletedTask;

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.Stop();
        }
    }

    enum Form { Normal, Fire, Ice }

    sealed class PortalSlot((int X, int Y) at, bool isFire)
    {
        public readonly (int X, int Y) At = at;
        public readonly bool IsFire = isFire;
        public uint MonsterId;
        public double RespawnAt;
    }

    sealed class Fight(uint bossId, SpawnedMonster boss, uint seed)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly List<TimedEffect> effects = [];
        readonly List<PortalSlot> portals = [];
        readonly HashSet<uint> adds = [];

        bool initialized, cleaned, stopped, preferFire = true;
        Form form = Form.Normal;
        double formEndsAt, nextTransformAt, nextFormActionAt;

        public void Stop() => stopped = true;

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);

            if (!Alive(boss))
            {
                if (!cleaned) await Cleanup(runner);
                return;
            }

            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 120, awakeId: bossId);
            if (target is null) return;

            if (!initialized)
            {
                initialized = true;
                nextTransformAt = now + 10;
                InitPortalSlots(runner, pos);
                await EnsurePortalsSpawned(runner, now, initial: true);
                Console.WriteLine($"[gateway] Piety (Lunaris) controller active for 0x{bossId:x} with {portals.Count} portal slots");
            }

            // Track destroyed portals and schedule their respawn cooldown.
            foreach (var slot in portals)
            {
                if (slot.MonsterId != 0 && (!runner.Area.Monsters.TryGetValue(slot.MonsterId, out var pm) || !Alive(pm)))
                {
                    slot.MonsterId = 0;
                    slot.RespawnAt = now + 15;
                }
            }

            if (form == Form.Normal)
            {
                await EnsurePortalsSpawned(runner, now, initial: false);
                if (now >= nextTransformAt)
                {
                    var chosen = PickLivingPortal(runner);
                    if (chosen is not null)
                    {
                        await EnterForm(runner, chosen, now);
                    }
                    else
                    {
                        // Players destroyed all portals: transformation denied!
                        nextTransformAt = now + 6;
                    }
                }
                return;
            }

            // Active Fire or Ice form: check if players destroyed all portals of Piety's active element.
            bool activeElementAlive = portals.Any(s => s.IsFire == (form == Form.Fire) && s.MonsterId != 0
                && runner.Area.Monsters.TryGetValue(s.MonsterId, out var pm) && Alive(pm));
            if (!activeElementAlive && formEndsAt > now + 1.5)
                formEndsAt = now + 1.5;

            if (now >= formEndsAt)
            {
                RevertToNormal(runner, now);
                return;
            }

            if (now >= nextFormActionAt)
            {
                if (form == Form.Fire)
                {
                    nextFormActionAt = now + 2.4;
                    var atk = ElementalSpellAttack(boss, DamageType.Fire, 1.15, radius: 13);
                    foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
                        await ArenaHazards.Hit(p, bossId, boss, atk, at => Distance(at, pos) <= 13, () => !stopped && Alive(boss));
                }
                else
                {
                    nextFormActionAt = now + 2.6;
                    var aim = target.Walk.Position(now);
                    var atk = ElementalSpellAttack(boss, DamageType.Cold, 1.10, radius: 12);
                    foreach (var p in ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)))
                        await ArenaHazards.Hit(p, bossId, boss, atk, at => Distance(at, aim) <= 12, () => !stopped && Alive(boss));
                }
            }
        }

        void InitPortalSlots(Player runner, (int X, int Y) center)
        {
            var markers = PlacementsOf(Area, seed, "Metadata/QuestObjects/Piety/PietyTransformMarker");
            if (markers.Count >= 4)
            {
                // Pick 4 well-spaced cardinal markers around the arena (2 fire, 2 ice alternating).
                var ordered = markers.OrderBy(m => Math.Atan2(m.Y - center.Y, m.X - center.X)).ToList();
                int step = Math.Max(1, ordered.Count / 4);
                for (int i = 0; i < 4; i++)
                {
                    var m = ordered[(i * step) % ordered.Count];
                    portals.Add(new(WalkableNear(runner, (m.X, m.Y)), isFire: (i & 1) == 0));
                }
            }
            else
            {
                for (int i = 0; i < 4; i++)
                    portals.Add(new(RingSpot(runner, center, i * (Math.Tau / 4.0), 28), isFire: (i & 1) == 0));
            }
        }

        async Task EnsurePortalsSpawned(Player runner, double now, bool initial)
        {
            foreach (var slot in portals)
            {
                if (slot.MonsterId != 0) continue;
                if (!initial && now < slot.RespawnAt) continue;
                string variety = slot.IsFire ? FirePortal : IcePortal;
                slot.MonsterId = await SpawnAdd(runner, variety, boss.Level, slot.At, bossId,
                    ownedCurrent: () => !stopped && !cleaned && Alive(boss));
            }
        }

        PortalSlot? PickLivingPortal(Player runner)
        {
            bool Living(PortalSlot s) => s.MonsterId != 0 && runner.Area.Monsters.TryGetValue(s.MonsterId, out var pm) && Alive(pm);
            var preferred = portals.FirstOrDefault(s => s.IsFire == preferFire && Living(s));
            var chosen = preferred ?? portals.FirstOrDefault(Living);
            if (chosen is not null) preferFire = !chosen.IsFire;
            return chosen;
        }

        async Task EnterForm(Player runner, PortalSlot portal, double now)
        {
            form = portal.IsFire ? Form.Fire : Form.Ice;
            formEndsAt = now + 11.5;
            nextFormActionAt = now + 1.8;

            // Move Piety to the portal she channels.
            boss.Walk.Teleport(portal.At.X, portal.At.Y, now);
            await SendWorldRecord(runner, bossId, InstanceProtocol.Teleport(bossId, portal.At.X, portal.At.Y), null);

            string art = portal.IsFire ? RedTransformArt : BlueTransformArt;
            await SpawnTimedEffect(runner, effects, portal.At, art, now + 2.2, scale: 1.15f);

            runner.Area.Buffs.Clear(bossId, FireFormBuff, bossId);
            runner.Area.Buffs.Clear(bossId, IceFormBuff, bossId);
            runner.Area.Buffs.Set(bossId, new(portal.IsFire ? FireFormBuff : IceFormBuff, bossId, U68: Buff.Seconds(11.5)));
            boss.SetTemporaryStats(FormStatsKey, portal.IsFire ? FireFormStats : IceFormStats);

            int livingAdds = adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
            var rng = AreaRng(runner);
            string addVariety = portal.IsFire ? BloodElemental : Miscreation;
            for (int i = 0; i < 2 && livingAdds + i < 5; i++)
            {
                var spot = RingSpot(runner, portal.At, rng.NextDouble() * Math.Tau, 10);
                await SpawnAdd(runner, addVariety, boss.Level, spot, bossId, adds,
                    ownedCurrent: () => !stopped && !cleaned && Alive(boss));
            }
            Console.WriteLine($"[gateway] Piety 0x{bossId:x}: entered {form} form at ({portal.At.X}, {portal.At.Y})");
        }

        void RevertToNormal(Player runner, double now)
        {
            form = Form.Normal;
            nextTransformAt = now + 14;
            boss.RemoveTemporaryStats(FormStatsKey);
            runner.Area.Buffs.Clear(bossId, FireFormBuff, bossId);
            runner.Area.Buffs.Clear(bossId, IceFormBuff, bossId);
        }

        async Task Cleanup(Player runner)
        {
            cleaned = true;
            boss.RemoveTemporaryStats(FormStatsKey);
            runner.Area.Buffs.Clear(bossId, FireFormBuff, bossId);
            runner.Area.Buffs.Clear(bossId, IceFormBuff, bossId);
            await RetireMonsters(runner, portals.Select(p => p.MonsterId).Where(id => id != 0));
            foreach (var p in portals) p.MonsterId = 0;
            await RetireMonsters(runner, adds);
            adds.Clear();
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Piety (Lunaris) defeated; portals and adds cleared");
        }
    }
}
