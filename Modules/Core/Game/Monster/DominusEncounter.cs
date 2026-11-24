using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static CampaignBossHelpers;
using static Game;
using static Program;

/// <summary>
/// Act 3 — The Upper Sceptre of God (<c>1_3_18_2</c>) Dominus &amp; Scion Cage controller:
/// <list type="bullet">
/// <item><b>Phase 1 (<c>Pope</c>, High Templar Dominus)</b>: Augments <c>Act3.cs</c>'s Legionnaire → Miscreation → Dominus →
/// <c>Dominusdemon</c> chain with safe Chilling Bond totems (ground cold beam degen without invoking the crashing native
/// bond packet), Lightning Souls waves, and telegraphed Touch of God slams (<c>NoRewards</c> on P1).</item>
/// <item><b>Phase 2 (<c>Dominusdemon</c>, Dominus, Ascendant)</b>: Periodic Blood Rain phase where Dominus raises his
/// Proximity Shield safe bubble (<c>SetEncounterProximityShield</c> + buff 227, without Temporal Bubble slow) while
/// players outside the bubble take escalating Blood Rain Corrupted Blood physical DoT (buff 238, cleared on Blood Rain
/// end, Dominus death, <c>PlayerLeaving</c>, and module unload).</item>
/// <item><b>Upper Sceptre Scion Cage</b>: Created ONCE per <see cref="InstanceState"/> so <c>AreaObjectRecords</c>
/// replays it cleanly to late joiners and re-entrants; opening it sets <c>ScionUnlocked</c> (QF 531, triggering the
/// <c>UnlockScion</c> achievement) and plays Scion's global unlock dialogue audio (NPC row 58, audio 484).</item>
/// </list>
/// </summary>
public static class DominusEncounter
{
    public const string Area = "1_3_18_2";
    public const string DominusP1 = "Metadata/Monsters/Pope/Pope";
    public const string DominusP2 = "Metadata/Monsters/Dominusdemon/Dominusdemon";
    public const string ChillingBondTotem = "Metadata/Monsters/Totems/ChillingBondTotem";
    public const string MiscreationLoop = "Metadata/Monsters/DemonModular/DemonModularTowerLoop";

    public const string ScionNpcType = "Metadata/NPC/Act3/Scion";
    public const string ScionCageType = "Metadata/NPC/Act3/ScionCage";
    public const string ScionUnlockedFlag = "ScionUnlocked";
    const ushort ScionUnlockedAudioRow = 484; // NPCTextAudio 484 ScionUnlocked
    const ushort ScionFallbackNpcRow = 58;    // NPCs row 58 Metadata/NPC/Act3/Scion

    const ushort ProximityShieldBuffRow = 227; // proximity_shield_aura
    const ushort CorruptedBloodBuffRow = 238;  // corrupted_blood
    const int SafeBubbleRadius = 24;

    const string BloodSplashArt = "Metadata/Effects/Spells/monsters_effects/act3/dominus/blood_rains/bloodsplash.ao";
    const string LightningSlamArt = "Metadata/Effects/Spells/monsters_effects/act3/dominus/lightning_slam/lightningslam.ao";
    const string LightningSoulsArt = "Metadata/Effects/Spells/monsters_effects/act3/dominus/lightning_souls/lightning_souls.ao";
    const string LightningExplosionArt = "Metadata/Effects/Spells/monsters_effects/act3/dominus/lightning_souls/lightningexplosion.ao";
    const string BirdcageModel = "Metadata/Terrain/Doodads/RuinedCity/Scepter_of_god/birdcage.ao";

    // The client registers Door in MiscObjects. Its two states play the cage model's
    // opening animation and disable interaction once open.
    const string CageWireType = "Metadata/MiscellaneousObjects/Door";

    static readonly RuleTable<InstanceState, Fight> Fights = new();
    static readonly RuleTable<Player, BloodRainExposure> BloodExposures = new();

    sealed class BloodRainExposure
    {
        public readonly ArenaHazards.Exposure Ground = new();
        public int Stacks;
        public double NextStackAt;
        public bool BuffShown;
    }

    public static async Task OnAreaGenerated(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || player.Closed || player.LeavingArea) return;
        var fight = Fights.GetValue(player.Instance, _ => new Fight(player.Area, player.Seed));
        await fight.Gate.WaitAsync();
        try { await fight.EnsureScionCageCreated(player); }
        finally { fight.Gate.Release(); }
    }

    public static async Task<bool> Use(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Layout.Id != Area || !Fights.TryGetValue(player.Instance, out var fight) || fight.CageId == 0 || use.TargetId != fight.CageId)
            return false;
        var at = fight.CageAt;
        if (!TryBeginObjectWalk(player, requestedAt, at.X, at.Y, 6, out var walk)) return true;
        var (action, now, here, stop, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, at.X, at.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                null, still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return true;
        try
        {
            if (!PlayerWalk.InReach(stop, at, 7, player.Layout.Walkable)) return true;
            await fight.Gate.WaitAsync();
            try { await fight.OpenScionCage(player); }
            finally { fight.Gate.Release(); }
        }
        finally { await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null); }
        return true;
    }

    public static async Task Tick(Player player)
    {
        if (player.Layout.Id != Area || !CurrentGameWork || !player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Paused)
            return;
        if (player.PlayerLife == 0) ClearBloodRain(player);
        var fight = Fights.GetValue(player.Instance, _ => new Fight(player.Area, player.Seed));
        await fight.Gate.WaitAsync();
        try
        {
            await fight.EnsureScionCageCreated(player);
            if (IsMonsterRunner(player))
                await fight.Step(player, AreaTime(player));
        }
        finally { fight.Gate.Release(); }
    }

    public static Task Leaving(Player player)
    {
        if (player.Layout.Id != Area) return Task.CompletedTask;
        ClearBloodRain(player);
        return Task.CompletedTask;
    }

    static void ClearBloodRain(Player player)
    {
        if (!BloodExposures.TryGetValue(player, out var exp)) return;
        exp.Stacks = 0;
        exp.NextStackAt = 0;
        exp.Ground.Last = double.NaN;
        if (exp.BuffShown && !player.Closed)
        {
            exp.BuffShown = false;
            player.Area.Buffs.Clear(player.Id, CorruptedBloodBuffRow, player.Id);
        }
    }

    public static IDisposable CleanupOnUnload() => new Cleanup();
    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights) pair.Value.StopAndClear();
            foreach (var pair in BloodExposures) ClearBloodRain(pair.Key);
        }
    }

    sealed record DelayedHit(double At, (int X, int Y) Spot, double Radius, string BurstAo, DamageType Element, double Scale, uint SourceId, SpawnedMonster Source);

    sealed class Fight
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        readonly GameArea area;
        readonly List<TimedEffect> effects = [];
        readonly List<DelayedHit> hits = [];
        readonly HashSet<uint> totems = [];
        readonly HashSet<uint> p2Adds = [];
        readonly (int X, int Y)? cagePlacement;
        readonly List<(int X, int Y)> towerLoopSpawners;

        public uint CageId;
        public (int X, int Y) CageAt;
        int cageTransitionIdx = -1;
        bool cageCreated, cageOpened;

        bool p1Initialized, p1Cleaned;
        uint p1BossId;
        double nextTotemAt, nextSoulsAt, nextTouchAt;

        bool p2Initialized, p2Cleaned, bloodRainActive;
        uint p2BossId;
        double nextBloodRainAt, bloodRainEndsAt, nextSplashAt, nextAddAt;
        bool stopped;

        public Fight(GameArea area, uint seed)
        {
            this.area = area;
            var placements = LoadAreaPlacements(Area, seed);
            cagePlacement = placements.Of(ScionCageType).FirstOrDefault() is { } c ? (c.X, c.Y) : null;
            towerLoopSpawners = [.. placements.Of("Metadata/Monsters/Demonmodular/TowerSpawners/TowerSpawnerLoop").Select(s => (s.X, s.Y))];
        }

        public void StopAndClear()
        {
            stopped = true;
            if (bloodRainActive && p2BossId != 0 && area.Monsters.TryGetValue(p2BossId, out var b))
            {
                bloodRainActive = false;
                SetEncounterProximityShield(area, b, 0);
                area.Buffs.Clear(p2BossId, ProximityShieldBuffRow, p2BossId);
            }
        }

        public async Task EnsureScionCageCreated(Player player)
        {
            if (cageCreated) return;
            cageCreated = true;
            var info = QuestObjectTypes.Find(CageWireType);
            if (info is null || !QuestObject.CanSerialize(info.Components))
                return;
            cageTransitionIdx = QuestObject.TransitionableIndex(info.Components);
            CageAt = cagePlacement ?? WalkableNear(player, (player.Layout.SpawnX + 12, player.Layout.SpawnY + 8));
            CageId = player.TakeObjectId();
            bool opened = cageOpened || ClientHasQuestFlag(player, ScionUnlockedFlag);
            byte state = (byte)(opened ? 2 : 1);
            var model = QuestObjectTypes.Find(ScionCageType)?.AnimatedModel ?? BirdcageModel;
            var blob = new QuestObject(CageAt.X, CageAt.Y, info.Components, Model: model, TransitionState: state).Blob();
            await SendWorldCreate(player, CageId,
                () => InstanceEntities.CreateObject(CageId, InstanceEntities.ObjectTypeHash(CageWireType), blob),
                $"0x02bb ScionCage 0x{CageId:x} at ({CageAt.X}, {CageAt.Y}), opened={opened}", wake: true, awaitBirth: false);
        }

        public async Task OpenScionCage(Player player)
        {
            cageOpened = true;
            if (cageTransitionIdx >= 0 && CageId != 0)
                await SendWorldRecord(player, CageId, InstanceEntities.TransitionableUpdate(CageId, (byte)cageTransitionIdx, 2),
                    $"0x02bc ScionCage 0x{CageId:x} opened");
            if (!ClientHasQuestFlag(player, ScionUnlockedFlag))
            {
                await SetQuestFlags(player, [ScionUnlockedFlag]);
                ushort scionRow = Npcs.Find(ScionNpcType) is { } npc ? (ushort)npc.Row : ScionFallbackNpcRow;
                await Send(player, InstanceNpcs.TextAudio(0, scionRow, ScionUnlockedAudioRow), "0x01c7 Scion unlocked dialogue");
                await Send(player, InstanceChat.ChatLine("You have freed the Scion! The Scion class is now unlocked."), null);
            }
        }

        public async Task Step(Player runner, double now)
        {
            if (stopped || !CurrentGameWork || runner.Paused) return;
            await ExpireEffects(runner, effects, now);
            await StepDelayedHits(runner, now);

            var p1Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == DominusP1);
            if (p1Entry.Value is { } p1 && Alive(p1))
                await StepPhase1(runner, p1Entry.Key, p1, now);
            else if (p1Initialized && !p1Cleaned)
                await CleanupPhase1(runner);

            var p2Entry = runner.Area.Monsters.FirstOrDefault(p => p.Value.Variety?.Id == DominusP2);
            if (p2Entry.Value is { } p2)
            {
                if (Alive(p2))
                    await StepPhase2(runner, p2Entry.Key, p2, now);
                else if (!p2Cleaned)
                    await CleanupPhase2(runner);
            }
        }

        async Task StepPhase1(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            p1BossId = bossId;
            boss.NoRewards = true;
            var pos = boss.Walk.Position(now);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 115, awakeId: bossId);
            if (target is null) return;

            if (!p1Initialized)
            {
                p1Initialized = true;
                nextTotemAt = now + 5;
                nextSoulsAt = now + 8;
                nextTouchAt = now + 12;
                Console.WriteLine($"[gateway] Dominus P1 controller active for 0x{bossId:x}");
            }

            if (now >= nextTotemAt)
            {
                nextTotemAt = now + 11;
                int livingTotems = totems.Count(id => runner.Area.Monsters.TryGetValue(id, out var t) && Alive(t));
                if (livingTotems < 3)
                {
                    var rng = AreaRng(runner);
                    var spot = RingSpot(runner, target.Walk.Position(now), rng.NextDouble() * Math.Tau, 14);
                    await SpawnAdd(runner, ChillingBondTotem, boss.Level, spot, bossId, totems,
                        ownedCurrent: () => !stopped && !p1Cleaned);
                }
            }

            var totemPositions = totems
                .Select(id => runner.Area.Monsters.TryGetValue(id, out var t) && Alive(t) ? ((int X, int Y)?)t.Walk.Position(now) : null)
                .OfType<(int X, int Y)>().ToArray();
            var players = ActivePlayers(runner);
            if (totemPositions.Length > 0)
            {
                var (min, max) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (20, 30);
                double beamDps = (min + max) * 0.45;
                foreach (var p in players)
                {
                    var exp = BloodExposures.GetValue(p, _ => new());
                    await ArenaHazards.Ground(p, exp.Ground, now, (at, _) =>
                        totemPositions.Any(tp => DistanceToSegment(at, pos, tp) <= 6.0)
                            ? DamagePacket.Of(DamageType.Cold, beamDps, beamDps)
                            : DamagePacket.Empty,
                        () => !stopped && Alive(boss));
                }
            }
            else
            {
                foreach (var p in players)
                    if (BloodExposures.TryGetValue(p, out var exp))
                        exp.Ground.Last = double.NaN;
            }

            if (now >= nextSoulsAt)
            {
                nextSoulsAt = now + 9.5;
                var aim = target.Walk.Position(now);
                var rng = AreaRng(runner);
                for (int i = 0; i < 3; i++)
                {
                    var spot = RingSpot(runner, aim, rng.NextDouble() * Math.Tau, 5 + i * 4);
                    await SpawnTimedEffect(runner, effects, spot, LightningSoulsArt, now + 1.5, scale: 1.0f);
                    hits.Add(new(now + 1.4, spot, 10, LightningExplosionArt, DamageType.Lightning, 1.15, bossId, boss));
                }
            }

            if (now >= nextTouchAt)
            {
                nextTouchAt = now + 14;
                var aim = target.Walk.Position(now);
                await SpawnTimedEffect(runner, effects, aim, LightningSlamArt, now + 2.2, scale: 1.25f);
                hits.Add(new(now + 1.75, aim, 16, LightningSlamArt, DamageType.Lightning, 1.85, bossId, boss));
            }
        }

        async Task CleanupPhase1(Player runner)
        {
            p1Cleaned = true;
            foreach (var p in ActivePlayers(runner))
                if (BloodExposures.TryGetValue(p, out var exp))
                    exp.Ground.Last = double.NaN;
            await RetireMonsters(runner, totems);
            totems.Clear();
            hits.RemoveAll(h => h.SourceId == p1BossId);
        }

        async Task StepPhase2(Player runner, uint bossId, SpawnedMonster boss, double now)
        {
            p2BossId = bossId;
            var pos = boss.Walk.Position(now);
            var players = ActivePlayers(runner);
            var target = NearestLivingTarget(runner, pos, now, maxRange: 130, awakeId: bossId);

            if (!p2Initialized)
            {
                p2Initialized = true;
                nextBloodRainAt = now + 10;
                nextAddAt = now + 9;
                Console.WriteLine($"[gateway] Dominus P2 (Ascendant) controller active for 0x{bossId:x}");
            }

            if (target is null)
            {
                if (bloodRainActive) EndBloodRain(runner, bossId, boss);
                return;
            }

            if (!bloodRainActive && now >= nextBloodRainAt)
            {
                bloodRainActive = true;
                bloodRainEndsAt = now + 8.5;
                nextSplashAt = now;
                SetEncounterProximityShield(runner.Area, boss, SafeBubbleRadius);
                runner.Area.Buffs.Set(bossId, new(ProximityShieldBuffRow, bossId, U68: Buff.Seconds(8.5)));
                Console.WriteLine($"[gateway] Dominus Ascendant 0x{bossId:x}: Blood Rain begins (safe bubble radius {SafeBubbleRadius})");
            }

            if (bloodRainActive)
            {
                if (now >= bloodRainEndsAt)
                {
                    EndBloodRain(runner, bossId, boss);
                    nextBloodRainAt = now + 17;
                }
                else
                {
                    if (now >= nextSplashAt)
                    {
                        nextSplashAt = now + 0.9;
                        var rng = AreaRng(runner);
                        for (int i = 0; i < 3; i++)
                        {
                            var splash = RingSpot(runner, pos, rng.NextDouble() * Math.Tau, SafeBubbleRadius + 8 + rng.NextDouble() * 24);
                            await SpawnTimedEffect(runner, effects, splash, BloodSplashArt, now + 1.2, scale: 1.0f);
                        }
                    }
                    await TickBloodRainDamage(runner, boss, pos, players, now);
                }
            }
            else
            {
                foreach (var p in players)
                    ClearBloodRain(p);
            }

            if (now >= nextAddAt)
            {
                nextAddAt = now + 14;
                int living = p2Adds.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && Alive(m));
                if (living < 4)
                {
                    var rng = AreaRng(runner);
                    var spot = towerLoopSpawners.Count > 0
                        ? towerLoopSpawners[rng.Next(towerLoopSpawners.Count)]
                        : RingSpot(runner, pos, rng.NextDouble() * Math.Tau, 24);
                    await SpawnAdd(runner, MiscreationLoop, boss.Level, spot, bossId, p2Adds,
                        ownedCurrent: () => !stopped && !p2Cleaned);
                }
            }
        }

        async Task TickBloodRainDamage(Player runner, SpawnedMonster boss, (int X, int Y) bossPos, Player[] players, double now)
        {
            var (min, max) = Monsters.BaseDamage(boss.Variety!, boss.Level) ?? (25, 35);
            double perStackDps = (min + max) * 0.22;

            foreach (var p in players)
            {
                if (p.PlayerLife == 0 || p.Paused || InGracePeriod(p))
                {
                    ClearBloodRain(p);
                    continue;
                }
                var exp = BloodExposures.GetValue(p, _ => new());
                var stats = CharacterPlayerStats(p);
                bool immune = stats.GetValueOrDefault(Stat.ImmuneToCorruptedBlood) != 0
                    || stats.GetValueOrDefault(Stat.CannotBeInflictedByCorruptedBlood) != 0
                    || stats.GetValueOrDefault(Stat.BaseCannotBeDamaged) != 0;
                if (immune)
                {
                    ClearBloodRain(p);
                    continue;
                }

                bool outsideBubble = Distance(p.Walk.Position(now), bossPos) > SafeBubbleRadius;
                if (outsideBubble && now >= exp.NextStackAt)
                {
                    exp.NextStackAt = now + 1.0;
                    if (exp.Stacks < 10)
                    {
                        exp.Stacks++;
                        exp.BuffShown = true;
                        p.Area.Buffs.Set(p.Id, new(CorruptedBloodBuffRow, p.Id, Stacks: (ushort)exp.Stacks, U68: Buff.Seconds(3)));
                    }
                }
                else if (!outsideBubble && exp.Stacks > 0)
                {
                    ClearBloodRain(p);
                }

                if (exp.Stacks > 0)
                {
                    double dps = perStackDps * exp.Stacks;
                    await ArenaHazards.Ground(p, exp.Ground, now, (_, _) => DamagePacket.Of(DamageType.Physical, dps, dps),
                        () => !stopped && Alive(boss) && bloodRainActive);
                }
            }
        }

        void EndBloodRain(Player runner, uint bossId, SpawnedMonster boss)
        {
            bloodRainActive = false;
            SetEncounterProximityShield(runner.Area, boss, 0);
            runner.Area.Buffs.Clear(bossId, ProximityShieldBuffRow, bossId);
            foreach (var p in ActivePlayers(runner))
                ClearBloodRain(p);
        }

        async Task CleanupPhase2(Player runner)
        {
            p2Cleaned = true;
            if (p2BossId != 0 && runner.Area.Monsters.TryGetValue(p2BossId, out var b))
                EndBloodRain(runner, p2BossId, b);
            else
                foreach (var p in ActivePlayers(runner)) ClearBloodRain(p);
            await RetireMonsters(runner, p2Adds);
            p2Adds.Clear();
            hits.Clear();
            await ClearAllEffects(runner, effects);
            Console.WriteLine("[gateway] Dominus Ascendant defeated; Blood Rain and adds cleared");
        }

        async Task StepDelayedHits(Player runner, double now)
        {
            if (hits.Count == 0) return;
            var players = ActivePlayers(runner).Where(p => p.PlayerLife > 0 && !p.Paused && !InGracePeriod(p)).ToArray();
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                var h = hits[i];
                if (now < h.At) continue;
                hits.RemoveAt(i);
                if (!Alive(h.Source)) continue;
                await SpawnTimedEffect(runner, effects, h.Spot, h.BurstAo, now + 1.1, scale: 1.2f);
                var atk = ElementalSpellAttack(h.Source, h.Element, h.Scale, h.Radius);
                foreach (var p in players)
                    await ArenaHazards.Hit(p, h.SourceId, h.Source, atk, at => Distance(at, h.Spot) <= h.Radius, () => !stopped);
            }
        }

        static double DistanceToSegment((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double len2 = vx * vx + vy * vy;
            if (len2 <= 1e-6) return Distance(p, a);
            double t = Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0, 1);
            double px = a.X + t * vx, py = a.Y + t * vy;
            double dx = p.X - px, dy = p.Y - py;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
