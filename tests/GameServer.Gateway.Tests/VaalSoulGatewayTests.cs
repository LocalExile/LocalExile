using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class VaalSoulGatewayTests
{
    static object Call(string name, params object?[] args)
    {
        var method = typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        return method.Invoke(null, args.Concat(method.GetParameters().Skip(args.Length)
            .Select(p => p.DefaultValue)).ToArray())!;
    }
    static void Set(ClientState conn, string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;

    internal sealed class Fixture : IDisposable
    {
        public readonly ClientState Conn;
        public readonly PlayerInventories Items;
        readonly uint host;
        uint nextMonster = 42;
        public Fixture(int copies = 1, uint? ticket = null, string? character = null, ushort area = 1, bool noCharacter = false)
        {
            TestSaves.Ensure();
            Conn = new(new AreaLayout(area, "vaal-test", 1000, 1000, 100, 100, null, []),
                ticket ?? (uint)Random.Shared.Next(1, int.MaxValue), 1)
            {
                Character = noCharacter ? null : character ?? $"Vaal{Guid.NewGuid():N}", PlayerLife = 1000, PlayerMana = 1000,
                PlayerSent = true, Instance = new(), Experience = CharacterStats.ExperienceFor(100),
                Pools = new(100, 1000, 1000, int.MaxValue, 0,
                    Strength: 1000, Dexterity: 1000, Intelligence: 1000)
            };
            Items = PlayerInventories.For(Conn.Character ?? $"UnusedVaal{Guid.NewGuid():N}", Program.Items);
            foreach (var item in Items.Items(PlayerInventories.Weapon1).ToArray()) Items.Remove(PlayerInventories.Weapon1, item.Id);
            host = Items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Club")!)).Id;
            Copies(copies);
            Set(Conn, "global_cannot_crit", 1);
            Set(Conn, "attack_minimum_added_chaos_damage", 100);
            Set(Conn, "attack_maximum_added_chaos_damage", 100);
            // These test characters intentionally meet gem requirements through real raw developer replacements.
            foreach (string attribute in new[] { "strength", "dexterity", "intelligence" })
                Set(Conn, $"virtual_{attribute}_for_requirements", 1000);
        }
        public void Copies(int count) => GemLevels(Enumerable.Repeat(1, count).ToArray());
        public void Gems(params string[] names) => Items.Change(PlayerInventories.Weapon1, host, item => item with
        {
            Sockets = names.Select(name => new ItemSocket(SocketColour.White,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!))).ToArray(),
            Links = names.Select(_ => (byte)1).ToArray()
        });
        public void Links(params byte[] groups) => Items.Change(PlayerInventories.Weapon1, host,
            item => item with { Links = groups });
        public void GemLevels(params int[] levels) => Items.Change(PlayerInventories.Weapon1, host, item => item with
        {
            Sockets = levels.Select(level =>
            {
                var type = Program.Items.Find("Metadata/Items/Gems/SkillGemVaalFireball")!;
                return new ItemSocket(SocketColour.White, ItemCatalog.Create(type) with
                    { GemExperience = Program.Items.Gem(type)!.ExperienceFor(level) });
            }).ToArray(),
            Links = levels.Length == 0 ? [] : [(byte)levels.Length]
        });
        public SpawnedMonster Monster(bool corpse = true, int distance = 5, bool hostile = true,
            MonsterRarity rarity = MonsterRarity.Normal)
        {
            var monster = new SpawnedMonster("soul target", new(100 + distance, 100, 10, width: 1000, height: 1000), 0, 2, 1)
            {
                Life = corpse ? 0u : 1u, Dead = corpse, Hostile = hostile, Rarity = rarity,
                Variety = new(0, "soul-test-monster", "soul target", "Metadata/Monsters/Monster", null, null),
            };
            Conn.Monsters.Add(nextMonster++, monster);
            return monster;
        }
        public uint Id(SpawnedMonster monster) => Conn.Monsters.Single(kv => ReferenceEquals(kv.Value, monster)).Key;
        public bool Kill(int distance = 5) { var m = Monster(distance: distance); return Program.CollectVaalSoulOnKill(Conn, Id(m), m); }
        public VaalSoulBudget Budget => Assert.Single(Conn.VaalSouls.Read());
        public void Dispose() { Conn.Closed = true; if (Conn.Character is not null) PlayerInventories.Forget(Conn.Character); }
    }

    // Same encrypted in-memory dispatcher transport as existing Rage/cooldown tests, no game server or client.
    internal sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        readonly ConnectionCiphers receive = new(new byte[64], isClient: true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public NetworkStream Stream => writer.GetStream();
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }
        public async Task<byte[]> Read(int count)
        {
            byte[] bytes = new byte[count]; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await reader.GetStream().ReadExactlyAsync(bytes, timeout.Token); receive.Receive.Process(bytes); return bytes;
        }
        public async Task<byte[]> Through(byte[] expected)
        {
            var bytes = new List<byte>(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            byte[] chunk = new byte[65536];
            while (bytes.ToArray().AsSpan().IndexOf(expected) < 0)
            {
                int count = await reader.GetStream().ReadAsync(chunk, timeout.Token); Assert.True(count > 0);
                receive.Receive.Process(chunk.AsSpan(0, count)); bytes.AddRange(chunk.Take(count));
            }
            return [.. bytes];
        }
        public Task Dispatch(ClientState conn, uint target, ushort skill)
        {
            ushort id = InstanceTransfer.UseSkillOnObjectId;
            var bytes = new LoginWriter().U16(id).U32(target).U16(skill).U16(1).U16(1).ToArray();
            return Program.OnClientMessages("soul-dispatch-test", Stream, Ciphers, [(id, bytes)], [], conn);
        }
        public void Dispose() { writer.Dispose(); reader.Dispose(); listener.Stop(); }
    }

    static async Task<Task> HoldLethalNotifications(Fixture f, Wire wire, SpawnedMonster monster, string path)
    {
        uint id = f.Id(monster);
        if (path == "default")
        {
            Task attack;
            lock (f.Conn.Monsters)
            {
                attack = Program.Attack("soul-held-default", wire.Stream, wire.Ciphers, f.Conn,
                    new(id, SkillCatalog.FirstSkillId, 1, 1), monster, f.Conn.Clock.Elapsed.TotalSeconds);
                // Initial input was accepted unpaused. Freeze contact until its action echo has passed the gate.
                f.Conn.Clock.Stop();
            }
            try
            {
                await wire.Through(InstanceProtocol.ActionStarted(0x1000, 100, 100, 105, 100,
                    SkillCatalog.FirstSkillId, 1, 1, targetObjectId: id));
                await wire.Gate.WaitAsync();
            }
            finally { f.Conn.Clock.Start(); }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                lock (f.Conn.Monsters) if (monster.Dead) break;
                await Task.Delay(5, timeout.Token);
            }
            return attack;
        }
        await wire.Gate.WaitAsync();
        if (path == "shared")
            return (Task)Call("HitMonster", "soul-held-hit", wire.Stream, wire.Ciphers, f.Conn, id, monster,
                10, "soul test", false, false, (ushort)0, null, null, null);
        var rf = new RighteousFireState(1, 2, null, SkillCatalog.FirstSkillId, 6000, 0, 1000);
        f.Conn.RighteousFire = rf;
        return (Task)Call("BurnAround", "soul-held-rf", wire.Stream, wire.Ciphers, f.Conn, rf, 1.0);
    }

    public static IEnumerable<object[]> NotificationChanges()
    {
        foreach (string path in new[] { "shared", "rf", "default" })
            foreach (string change in new[] { "prevention-clear", "equip", "prevention-start", "remove-readd", "death", "transfer" })
                yield return [path, change];
        foreach (string path in new[] { "shared", "rf" })
            foreach (string change in new[] { "move-in", "move-out" }) yield return [path, change];
    }

    [Theory]
    [MemberData(nameof(NotificationChanges))]
    public async Task Lethal_event_owns_souls_before_held_notifications_and_cannot_be_reclassified(string path, string change)
    {
        using var f = new Fixture(change == "equip" ? 0 : 1); using var wire = new Wire();
        if (change == "prevention-clear") Set(f.Conn, "has_soul_gain_prevention", 1);
        Program.RefreshVaalSouls(f.Conn);
        var monster = f.Monster(corpse: false, distance: change == "move-in" ? (int)Program.ExperienceRange + 20 : 5);
        Task notifications = await HoldLethalNotifications(f, wire, monster, path);
        bool processedAtDeath = monster.SoulKillProcessed;
        long atDeath = f.Conn.VaalSouls.Read().Sum(b => b.Current);
        Fixture? destination = null;
        try
        {
            Assert.True(monster.Dead); Assert.Equal(0u, monster.Life); Assert.False(notifications.IsCompleted);
            lock (f.Conn.Monsters)
            {
                if (change == "prevention-clear") Set(f.Conn, "has_soul_gain_prevention", 0);
                if (change == "prevention-start") Set(f.Conn, "has_soul_gain_prevention", 1);
                if (change == "equip") f.Copies(1);
                if (change == "move-in") f.Conn.Walk.Teleport(100 + (int)Program.ExperienceRange + 15, 100,
                    f.Conn.Clock.Elapsed.TotalSeconds);
                if (change == "move-out") f.Conn.Walk.Teleport(500, 500, f.Conn.Clock.Elapsed.TotalSeconds);
                if (change == "remove-readd")
                {
                    f.Copies(0); Program.RefreshVaalSouls(f.Conn); f.Copies(1); Program.RefreshVaalSouls(f.Conn);
                }
                if (change == "death")
                {
                    f.Conn.PlayerLife = 0; f.Conn.PlayerLife = 1000;
                }
                if (change == "transfer")
                {
                    uint ticket = (uint)Random.Shared.Next(1, int.MaxValue);
                    Program.CarryVaalSouls(f.Conn, ticket);
                    destination = new Fixture(ticket: ticket, character: f.Conn.Character);
                    Program.RestoreVaalSouls(destination.Conn); Program.RefreshVaalSouls(destination.Conn);
                }
            }
        }
        finally { wire.Gate.Release(); }
        try
        {
            await notifications.WaitAsync(TimeSpan.FromSeconds(10));
            bool eligible = change is not ("prevention-clear" or "equip" or "move-in");
            uint expected = change is "prevention-start" or "move-out" ? 1u : 0u;
            Assert.Equal((long)expected, f.Conn.VaalSouls.Read().Sum(b => b.Current));
            Assert.True(processedAtDeath);
            Assert.Equal(eligible ? 1L : 0L, atDeath);
            if (destination is not null) Assert.Equal(1u, destination.Budget.Current);
        }
        finally { destination?.Dispose(); }
    }

    [Fact]
    public async Task A_stale_death_notification_cannot_collect_souls_without_a_lethal_mutation()
    {
        using var f = new Fixture(); using var wire = new Wire(); var corpse = f.Monster();
        await (Task)Call("MonsterKilled", "stale-soul-death", wire.Stream, wire.Ciphers, f.Conn, f.Id(corpse), corpse);
        Assert.Equal(0u, f.Budget.Current);
    }

    [Theory]
    [InlineData("SkillGemVaalHaste", 1963, 239, 100, 1)]
    [InlineData("SkillGemVaalGrace", 1994, 247, 100, 1)]
    [InlineData("SkillGemVaalFireball", 3366, 87, 30, 0)]
    public void Socketed_native_aura_type_selects_cost_and_publication_per_instance(string gem, int effect,
        uint row, uint capacity, int aura)
    {
        using var f = new Fixture(); f.Gems(gem);
        Set(f.Conn, VaalSoulCost.AuraMultiplier, 100); Program.RefreshVaalSouls(f.Conn);
        Assert.Equal(new VaalSoulBudget(row, capacity, 0), f.Budget);
        var skills = (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", f.Conn);
        var vaal = skills.Single(s => s.EffectsPerLevelRow == Program.Skills.FindByRow(0, effect, 1)!.EffectsPerLevelRow);
        Assert.Equal(aura, vaal.Stats!.Single(s => s.Key == Program.Stats.Key(VaalSoulCost.Aura)).Value);
    }

    static GrantedSkill[] GemRecords(ClientState conn) => ((IReadOnlyList<GrantedSkill>)Call("PlayerSkills", conn))
        .Where(s => s.Id != SkillCatalog.FirstSkillId).ToArray();
    static int AuraSelector(GrantedSkill skill) => skill.Stats!.Single(s => s.Key == Program.Stats.Key(VaalSoulCost.Aura)).Value;
    // Reflection lets this regression file compile against the original commit during the recorded RED run.
    static bool CatalogAura(int effect) => (bool)typeof(SkillCatalog).GetMethod("IsAura")!
        .Invoke(Program.Skills, [effect, null])!;

    [Fact]
    public void Real_aura_catalog_and_socketed_explicit_selector_replacements_agree_without_refilling()
    {
        Assert.True(CatalogAura(1963)); Assert.True(CatalogAura(1994));
        Assert.False(CatalogAura(3366)); Assert.False(CatalogAura(-1));
        Assert.Equal(new SkillCatalog.VaalDefinition(239, 50, 1), Program.Skills.VaalBudget(1963, 1));
        Assert.Equal(new SkillCatalog.VaalDefinition(247, 50, 1), Program.Skills.VaalBudget(1994, 1));
        using var f = new Fixture(); f.Gems("SkillGemVaalHaste", "SkillGemVaalGrace", "SkillGemVaalFireball");
        Set(f.Conn, VaalSoulCost.AuraMultiplier, 100); Program.RefreshVaalSouls(f.Conn);
        foreach (var budget in f.Conn.VaalSouls.Read())
            Assert.Equal(budget.ActiveSkillsRow == 87 ? 30u : 100u, budget.Capacity);
        for (int i = 0; i < 3; i++) Assert.True(f.Kill());
        Set(f.Conn, VaalSoulCost.Aura, 0); Program.RefreshVaalSouls(f.Conn);
        foreach (var budget in f.Conn.VaalSouls.Read())
        {
            Assert.Equal(budget.ActiveSkillsRow == 87 ? 30u : 50u, budget.Capacity); Assert.Equal(1u, budget.Current);
        }
        Assert.All(GemRecords(f.Conn), s => Assert.Equal(0, AuraSelector(s)));
        Set(f.Conn, VaalSoulCost.Aura, 1); Program.RefreshVaalSouls(f.Conn);
        Assert.Equal(60u, f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 87).Capacity);
        Assert.All(GemRecords(f.Conn), s => Assert.Equal(1, AuraSelector(s))); // Explicit global developer policy.
        f.Conn.PlayerStats.Remove(Program.Stats.Key(VaalSoulCost.Aura)!.Value); Program.RefreshVaalSouls(f.Conn);
        Assert.Equal(30u, f.Conn.VaalSouls.Read().Single(b => b.ActiveSkillsRow == 87).Capacity);
        Assert.All(f.Conn.VaalSouls.Read(), b => Assert.Equal(1u, b.Current));
        var fireball = GemRecords(f.Conn).Single(s => s.EffectsPerLevelRow == 532);
        Assert.Equal(0, AuraSelector(fireball));
        var source = Call("SupportedSkill", f.Conn, fireball.Id);
        var own = (Dictionary<uint, int>)source.GetType().GetProperty("Stats")!.GetValue(source)!;
        // A caster/global source is not this skill's metadata. The actual socketed Fireball must stay non-aura.
        var global = new Dictionary<uint, int> { [Program.Stats.Key(VaalSoulCost.Aura)!.Value] = 1 };
        Assert.Equal(0, (int)Call("VaalSoulInput", f.Conn, global, own, VaalSoulCost.Aura));
    }

    [Fact]
    public void Aura_added_by_an_accepted_support_belongs_to_that_socketed_instance_only()
    {
        using var f = new Fixture();
        f.Gems("SkillGemEnfeeble", "SupportGemBlasphemy", "SkillGemEnfeeble", "SkillGemVaalFireball");
        f.Links(2, 1, 1);
        uint curseRow = Program.Skills.FindByRow(0, Program.Skills.EffectRow("Enfeeble"), 1)!.EffectsPerLevelRow;
        var curses = GemRecords(f.Conn).Where(s => s.EffectsPerLevelRow == curseRow).ToArray();
        Assert.Equal(2, curses.Length);
        var linked = Assert.Single(curses, s => s.Supports.Count == 1);
        var unlinked = Assert.Single(curses, s => s.Supports.Count == 0);
        Assert.Equal(1, AuraSelector(linked)); Assert.Equal(0, AuraSelector(unlinked));
        f.Links(1, 2, 1); var swapped = GemRecords(f.Conn);
        Assert.Equal(0, AuraSelector(swapped.Single(s => s.Id == linked.Id)));
        Assert.Equal(1, AuraSelector(swapped.Single(s => s.Id == unlinked.Id)));
        f.Gems("SkillGemVaalFireball", "SupportGemBlasphemy"); f.Links(2);
        Set(f.Conn, VaalSoulCost.AuraMultiplier, 100); Program.RefreshVaalSouls(f.Conn);
        var vaal = GemRecords(f.Conn).Single(s => s.EffectsPerLevelRow == 532);
        Assert.Empty(vaal.Supports); Assert.Equal(0, AuraSelector(vaal)); Assert.Equal(30u, f.Budget.Capacity);
    }

    [Fact]
    public async Task Accepted_owner_kill_through_real_dispatcher_sends_native_budget_and_does_not_double_credit_corpse()
    {
        using var f = new Fixture(); using var wire = new Wire();
        Program.RefreshVaalSouls(f.Conn);
        Assert.Equal(0u, f.Budget.Current);
        var monster = f.Monster(corpse: false); uint id = f.Id(monster);
        var expected = InstanceEntities.PlayerSkillsChange(0x1000, [], [], [new(87, 30)], [new(87, 1, 30)]);
        await wire.Dispatch(f.Conn, id, SkillCatalog.FirstSkillId);
        await wire.Through(expected);
        Assert.Equal(0u, monster.Life); Assert.True(monster.Dead); Assert.True(monster.SoulKillProcessed);
        Assert.Equal(1u, f.Budget.Current);
        Assert.False(Program.CollectVaalSoulOnKill(f.Conn, id, monster));
        Assert.Null(Program.VaalSoulPresentation(f.Conn));
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("closed")]
    [InlineData("leaving")]
    [InlineData("paused")]
    [InlineData("retired")]
    [InlineData("prevention")]
    [InlineData("negative-prevention")]
    [InlineData("friendly")]
    [InlineData("alive-monster")]
    [InlineData("stale-monster")]
    [InlineData("unknown-monster")]
    [InlineData("no-variety")]
    [InlineData("no-character")]
    [InlineData("no-vaal")]
    public void Ineligible_controls_cannot_award_or_replay_a_corpse(string control)
    {
        using var f = new Fixture(control == "no-vaal" ? 0 : 1, noCharacter: control == "no-character");
        var m = f.Monster(corpse: control != "alive-monster", hostile: control != "friendly"); uint id = f.Id(m);
        switch (control)
        {
            case "dead": f.Conn.PlayerLife = 0; break;
            case "closed": f.Conn.Closed = true; break;
            case "leaving": f.Conn.LeavingArea = true; break;
            case "paused": f.Conn.Clock.Stop(); break;
            case "retired": f.Conn.VaalSoulsRetired = true; break;
            case "prevention": Set(f.Conn, "has_soul_gain_prevention", 1); break;
            case "negative-prevention": Set(f.Conn, "has_soul_gain_prevention", -1); break;
            case "stale-monster": f.Conn.Monsters[id] = f.Monster(); break;
            case "unknown-monster": f.Conn.Monsters.Remove(id); break;
            case "no-variety": m = new("untyped", m.Walk, 0, 2, 1) { Life = 0, Dead = true }; f.Conn.Monsters[id] = m; break;
        }
        Assert.False(Program.CollectVaalSoulOnKill(f.Conn, id, m));
        Assert.All(f.Conn.VaalSouls.Read(), b => Assert.Equal(0u, b.Current));
        if (m.SoulKillProcessed)
        {
            f.Conn.Closed = f.Conn.LeavingArea = f.Conn.VaalSoulsRetired = false; f.Conn.Clock.Start();
            f.Conn.PlayerLife = 1000; Set(f.Conn, "has_soul_gain_prevention", 0);
            Assert.False(Program.CollectVaalSoulOnKill(f.Conn, id, m));
        }
    }

    [Fact]
    public void Range_includes_boundary_and_town_never_collects()
    {
        using var f = new Fixture();
        Assert.True(f.Kill((int)Program.ExperienceRange));
        Assert.False(f.Kill((int)Program.ExperienceRange + 1)); Assert.Equal(1u, f.Budget.Current);
        foreach (var rarity in Enum.GetValues<MonsterRarity>())
        {
            uint before = f.Budget.Current; var m = f.Monster(rarity: rarity);
            Assert.True(Program.CollectVaalSoulOnKill(f.Conn, f.Id(m), m)); Assert.Equal(before + 1, f.Budget.Current);
        }
        ushort town = WaypointCatalog.Areas.FindById("1_1_town")!.Hash16;
        using var inTown = new Fixture(area: town); Assert.False(inTown.Kill());
    }

    [Fact]
    public void Duplicate_gems_share_one_budget_and_last_removal_explicitly_resets_before_readd()
    {
        using var f = new Fixture(2);
        Assert.NotNull(Program.VaalSoulPresentation(f.Conn));
        Assert.True(f.Kill()); Assert.True(f.Kill());
        Program.VaalSoulPresentation(f.Conn); f.Copies(1); Program.RefreshVaalSouls(f.Conn);
        Assert.Equal(new VaalSoulBudget(87, 30, 2), f.Budget);
        Assert.Null(Program.VaalSoulPresentation(f.Conn));
        f.Copies(0);
        Assert.Equal(InstanceEntities.PlayerSkillsChange(0x1000, [], [], [], [new(87, 0, 0)]),
            Program.VaalSoulPresentation(f.Conn)); Assert.Empty(f.Conn.VaalSouls.Read());
        f.Copies(1);
        Assert.Equal(InstanceEntities.PlayerSkillsChange(0x1000, [], [], [new(87, 30)], [new(87, 0, 30)]),
            Program.VaalSoulPresentation(f.Conn)); Assert.Equal(0u, f.Budget.Current);
    }

    [Fact]
    public void Unavailable_gem_does_not_collect_or_disable_a_usable_duplicate_and_reactivation_does_not_refill()
    {
        using var f = new Fixture(); f.GemLevels(1, 20); f.Conn.Pools = f.Conn.Pools with { Level = 1 };
        Assert.True(f.Kill()); Assert.Equal(new VaalSoulBudget(87, 30, 1), f.Budget);
        f.GemLevels(20); Program.RefreshVaalSouls(f.Conn); Assert.Empty(f.Conn.VaalSouls.Read()); Assert.False(f.Kill());
        f.Conn.Pools = f.Conn.Pools with { Level = 100 }; Program.RefreshVaalSouls(f.Conn);
        Assert.Equal(new VaalSoulBudget(87, 30, 0), f.Budget);
    }

    [Fact]
    public void Cost_changes_publish_matching_inputs_seed_increases_clamp_decreases_and_preserve_refreshes()
    {
        using var f = new Fixture();
        Program.VaalSoulPresentation(f.Conn);
        for (int i = 0; i < 30; i++) Assert.True(f.Kill()); Assert.False(f.Kill());
        Program.VaalSoulPresentation(f.Conn);
        Set(f.Conn, VaalSoulCost.NonAuraMultiplier, -50);
        Assert.Equal(InstanceEntities.PlayerSkillsChange(0x1000, [], [], [], [new(87, 15, 15)]),
            Program.VaalSoulPresentation(f.Conn));
        Set(f.Conn, VaalSoulCost.NonAuraMultiplier, 100);
        Program.RefreshVaalSouls(f.Conn); Assert.Equal(new VaalSoulBudget(87, 60, 15), f.Budget);
        for (int i = 0; i < 5; i++) Assert.True(f.Kill()); // Current now exceeds the client's OLD capacity of 15.
        Assert.Equal(InstanceEntities.PlayerSkillsChange(0x1000, [], [], [new(87, 60)], [new(87, 20, 60)]),
            Program.VaalSoulPresentation(f.Conn));
        var skills = (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", f.Conn);
        var vaal = skills.Single(s => s.EffectsPerLevelRow == 532);
        Assert.Equal(100, vaal.Stats!.Single(s => s.Key == Program.Stats.Key(VaalSoulCost.NonAuraMultiplier)).Value);
        Assert.Null(Program.VaalSoulPresentation(f.Conn));
        Set(f.Conn, VaalSoulCost.NoSouls, -1); Program.VaalSoulPresentation(f.Conn);
        Assert.Equal(new VaalSoulBudget(87, 0, 0), f.Budget); Assert.False(f.Kill());
        Set(f.Conn, VaalSoulCost.NoSouls, 0); Program.VaalSoulPresentation(f.Conn);
        Assert.Equal(new VaalSoulBudget(87, 60, 0), f.Budget);
    }

    [Fact]
    public void Creation_carries_once_to_matching_character_relog_starts_empty_and_death_clears()
    {
        using var old = new Fixture(); Assert.True(old.Kill());
        uint ticket = (uint)Random.Shared.Next(1, int.MaxValue);
        old.Conn.LeavingArea = true; Program.CarryVaalSouls(old.Conn, ticket); Program.CarryVaalSouls(old.Conn, ticket + 1);
        Assert.True(old.Conn.VaalSoulsRetired); Assert.Empty(old.Conn.VaalSouls.Read()); Assert.False(old.Kill());
        using var wrong = new Fixture(ticket: ticket); Program.RestoreVaalSouls(wrong.Conn);
        Assert.Empty(wrong.Conn.VaalSouls.Read());
        using var next = new Fixture(ticket: ticket, character: old.Conn.Character);
        var player = (PlayerObject)Call("PlayerForCreation", next.Conn);
        Assert.Equal(new VaalSoulBudget(87, 30, 1), Assert.Single(player.Souls!));
        Program.RestoreVaalSouls(next.Conn); Assert.Equal(1u, next.Budget.Current);
        using var replay = new Fixture(ticket: ticket, character: old.Conn.Character);
        Program.RestoreVaalSouls(replay.Conn); Program.RefreshVaalSouls(replay.Conn); Assert.Equal(0u, replay.Budget.Current);
        using var relog = new Fixture(character: old.Conn.Character);
        Program.RefreshVaalSouls(relog.Conn); Assert.Equal(0u, relog.Budget.Current);
        next.Conn.PlayerLife = 0; Assert.Equal(0u, next.Budget.Current);
    }

    [Theory]
    [InlineData("death")]
    [InlineData("remove")]
    [InlineData("increase")]
    [InlineData("retire")]
    public async Task Queued_soul_publication_recomputes_sources_and_lifecycle_at_send_gate(string change)
    {
        using var f = new Fixture(); using var wire = new Wire(); Assert.True(f.Kill());
        await wire.Gate.WaitAsync();
        var send = Program.SendVaalSouls("queued-soul-test", wire.Stream, wire.Ciphers, f.Conn);
        Assert.False(send.IsCompleted);
        byte[]? expected;
        if (change == "death") f.Conn.PlayerLife = 0;
        if (change == "remove") f.Copies(0);
        if (change == "increase") Set(f.Conn, VaalSoulCost.NonAuraMultiplier, 100);
        if (change == "retire") Program.CarryVaalSouls(f.Conn, (uint)Random.Shared.Next(1, int.MaxValue));
        expected = change is "remove" or "retire" ? null : InstanceEntities.PlayerSkillsChange(0x1000, [], [],
            [new(87, change == "increase" ? 60u : 30u)], [new(87, change == "death" ? 0u : 1u, change == "increase" ? 60u : 30u)]);
        wire.Gate.Release(); await send;
        if (expected is not null) Assert.Equal(expected, await wire.Read(expected.Length));
        else Assert.Null(Program.VaalSoulPresentation(f.Conn));
    }

    [Fact]
    public async Task Removal_observed_before_a_queued_skill_refresh_cannot_reuse_balance_when_readded_before_gate()
    {
        using var f = new Fixture(); using var wire = new Wire();
        Assert.True(f.Kill()); Program.VaalSoulPresentation(f.Conn);
        f.Copies(0); await wire.Gate.WaitAsync();
        var refresh = (Task)Call("SendSkills", "queued-skill-test", wire.Stream, wire.Ciphers, f.Conn);
        Assert.Empty(f.Conn.VaalSouls.Read()); f.Copies(1);
        wire.Gate.Release(); await refresh;
        Assert.Equal(0u, f.Budget.Current);
        // The real skill refresh carries this update after its skill records; the state must also be acknowledged.
        Assert.Equal(new VaalSoulBudget(87, 30, 0), f.Conn.SentVaalSouls[87]);
        Assert.Null(Program.VaalSoulPresentation(f.Conn));
    }

    [Fact]
    public async Task Unsupported_Vaal_cast_stays_refused_with_a_full_budget_and_never_consumes()
    {
        using var f = new Fixture(); using var wire = new Wire();
        for (int i = 0; i < 30; i++) f.Kill();
        var skills = (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", f.Conn);
        var vaal = skills.Single(s => s.EffectsPerLevelRow == 532);
        Assert.True(vaal.VaalResourcesEnabled);
        var target = f.Monster(corpse: false);
        uint mana = f.Conn.PlayerMana;
        await wire.Dispatch(f.Conn, f.Id(target), vaal.Id);
        Assert.Equal(30u, f.Budget.Current); Assert.Equal(1u, target.Life);
        Assert.Equal(mana, f.Conn.PlayerMana);
    }

    [Fact]
    public void Published_cost_inputs_match_resource_resolution_and_explicit_dev_zero_wins_skill_overlay()
    {
        using var f = new Fixture();
        uint multiplier = Program.Stats.Key(VaalSoulCost.NonAuraMultiplier)!.Value;
        uint noSouls = Program.Stats.Key(VaalSoulCost.NoSouls)!.Value;
        Dictionary<uint, int> player = new() { [multiplier] = 50, [noSouls] = 0 };
        Dictionary<uint, int> own = new() { [multiplier] = 25, [noSouls] = 1 };
        Set(f.Conn, VaalSoulCost.NoSouls, 0);
        var published = new Dictionary<uint, int>();
        Call("PublishVaalSoulInputs", f.Conn, player, own, published);
        Assert.Equal(75, published[multiplier]); Assert.Equal(0, published[noSouls]);
        int Resolved(string name) => (int)Call("VaalSoulInput", f.Conn, player, own, name);
        Assert.Equal(52, VaalSoulCost.Required(30, Resolved));
        Assert.Equal(52, VaalSoulCost.Required(30, name => published.GetValueOrDefault(Program.Stats.Key(name)!.Value)));
        Set(f.Conn, VaalSoulCost.NonAuraMultiplier, 0); Call("PublishVaalSoulInputs", f.Conn, player, own, published);
        Assert.Equal(0, published[multiplier]); Assert.Equal(30, VaalSoulCost.Required(30, Resolved));
    }
}
