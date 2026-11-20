using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using League = GameServer.Core.Accounts.League;
namespace GameServer.Gateway.Tests;
public class DamageTakenTriggerTests
{
    [Fact]
    public void Cwdt_is_trigger_only_and_native_level_limit_preserves_manual_high_level_spell()
    {
        WithCharacter(conn =>
        {
            Equip(conn, "SupportGemCastOnDamageTaken");
            var gem = Game.GemSkillEffects(conn).Single(g => g.Name == "Fireball");
            Assert.Contains("trigger", Game.GemSkillInstanceDisabled(conn, gem.Effect, gem.Id) ?? "");
            conn.Clock.Start();
            int action = conn.ActionCount; bool sideEffects = false;
            Assert.False(Game.TryBeginSkillCooldownUse(conn, gem.Id, gem.Effect, null, false,
                () => sideEffects = true, out _));
            Assert.False(sideEffects); Assert.Equal(action, conn.ActionCount);
            var support = Game.SupportedSkill(conn, gem.Id).Supports.Single();
            Assert.Empty(Game.Supports.Resolve(gem.Effect, [support.Gem], Game.SkillsFor(conn), activeLevel: 20));
            Assert.NotEmpty(Game.Supports.Resolve(gem.Effect, [support.Gem with { Level = 20 }], Game.SkillsFor(conn), activeLevel: 20));
            foreach (string triggerId in new[] { "SupportCastOnCritTriggered", "SupportCastWhileChannellingTriggered" })
            {
                var effect = Game.Supports.Effects.Single(e => e.Id == triggerId);
                var applied = Game.Supports.Resolve(gem.Effect, [new(effect.Row, 1, 0, 1)], Game.SkillsFor(conn));
                Assert.Contains("trigger", Game.TriggerOnlyReason(Game.SupportedSkill(conn, gem.Id) with { Supports = applied }) ?? "");
            }
            Item Levelled(string name, int level)
            {
                var item = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!);
                return item with { GemExperience = Program.Items.Gem(Program.Items.ByHash(item.TypeHash)!)!.ExperienceFor(level) };
            }
            var host = Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single();
            Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, host.Id, item => item with
            { Sockets = [new(SocketColour.White, Levelled("SkillGemImmortalCall", 3)),
                new(SocketColour.White, Levelled("SupportGemCastOnDamageTaken", 1)),
                new(SocketColour.White, Levelled("SupportGemAdditionalLevel", 2))], Links = [3] });
            var call = Game.GemSkillEffects(conn).Single();
            Assert.Equal(4, Game.SupportedSkill(conn, call.Id).Level);
            Assert.Contains("damage is taken", Game.TriggerOnlyReason(Game.SupportedSkill(conn, call.Id))!);
            // Bonus levels change the spell, not its gem's required player level. A real level-up does change it.
            Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, host.Id, item => item with
            { Sockets = [item.Sockets[0] with { Gem = Levelled("SkillGemImmortalCall", 4) }, .. item.Sockets.Skip(1)] });
            Assert.Null(Game.TriggerOnlyReason(Game.SupportedSkill(conn, call.Id)));
        });
    }
    static void VerifyNonProjectile(Player conn, Game.ProjectileSession session)
    {
        foreach (var item in Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).ToArray())
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, item.Id);
        Equip(conn, "SupportGemCastOnDamageTaken", "SkillGemIceNova");
        var nova = Game.GemSkillEffects(conn).Single(g => g.Name == "Ice Nova");
        Assert.True(Game.TriggerPayloadAvailable(conn, Game.SupportedSkill(conn, nova.Id)));
        Assert.Null(Game.GemSkillInstanceDisabled(conn, nova.Effect, nova.Id, triggered: true));
        var origin = conn.Walk.Position(0);
        var victim = new SpawnedMonster("trigger-target", new(origin.X + 1, origin.Y, 0), 0, 0, 100000)
            { Hostile = true };
        var friendly = new SpawnedMonster("friendly", new(origin.X, origin.Y, 0), 0, 0, 100000) { Hostile = false };
        conn.Monsters[0x800abcd] = victim; conn.Monsters[0x800abce] = friendly;
        int action = conn.ActionCount;
        Game.ReleaseTriggeredNonProjectile(conn, session, nova.Id, (1000, 1000), () => false).GetAwaiter().GetResult();
        Assert.Equal(100000u, victim.Life);
        Game.ReleaseTriggeredNonProjectile(conn, session, nova.Id, (1000, 1000), () => true).GetAwaiter().GetResult();
        Assert.True(victim.Life < 100000); // Self-centred nova ignores the remote aim.
        Assert.Equal(100000u, friendly.Life);
        Assert.Equal(action, conn.ActionCount);
        foreach (var item in Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).ToArray())
            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, item.Id);
        Equip(conn, "SupportGemCastOnDamageTaken", "SkillGemFlammability");
        var curse = Game.GemSkillEffects(conn).Single(g => g.Name == "Flammability");
        Assert.True(Game.TriggerPayloadAvailable(conn, Game.SupportedSkill(conn, curse.Id)));
        Game.ReleaseTriggeredNonProjectile(conn, session, curse.Id, origin, () => true).GetAwaiter().GetResult();
        Assert.Single(victim.Curses.All);
        Assert.Empty(friendly.Curses.All);
        Assert.True(victim.Effects[Stat.BaseFireDamageResistancePct] < 0);
        Assert.Equal(action, conn.ActionCount);
    }
    static void VerifyAwakenedCriticalTrigger()
    {
        WithCharacter(conn =>
        {
            conn.Clock.Start(); EquipLinkedSpells(conn, awakened: true);
            var attack = Game.GemSkillEffects(conn).Single(s => s.Name == "Kinetic Blast");
            var spell = Game.GemSkillEffects(conn).Single(s => s.Name == "Fireball");
            Assert.Equal(10, Game.SupportedSkill(conn, spell.Id).Stats[Stat.BaseSpellCooldownSpeedPlusPct]);
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            using var writer = new System.Net.Sockets.TcpClient(); writer.Connect((System.Net.IPEndPoint)listener.LocalEndpoint);
            using var reader = listener.AcceptTcpClient();
            var session = new Game.ProjectileSession(conn);
            try
            {
                var source = Game.CaptureCriticalTriggerSource(conn, attack.Id);
                Assert.NotNull(source);
                Assert.True(Game.ReleaseCriticalTrigger(conn, session, source, true, true, (20, 0), 10));
                Assert.True(Game.ReleaseCriticalTrigger(conn, session, source, true, true, (20, 0), 10));
                Assert.False(Game.ReleaseCriticalTrigger(conn, session, source, true, true, (20, 0), 10.135));
                Assert.True(Game.ReleaseCriticalTrigger(conn, session, source, true, true, (20, 0), 10.137));
            }
            finally { session.Invalidate(); }
        });
    }
    static void EquipLinkedSpells(Player conn, bool channel = false, bool awakened = false)
    {
        var names = channel
            ? new[] { "SkillGemCyclone", "SkillGemFireball", "SkillGemFreezingPulse", "SupportGemCastWhileChannelling" }
            : new[] { "SkillGemKineticBlast", "SkillGemFireball", "SkillGemFreezingPulse", awakened ? "SupportGemCastOnCritPlus" : "SupportGemCastOnCrit" };
        var robe = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { Sockets = [.. names.Select(n => new ItemSocket(SocketColour.White,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + n)!)))], Links = [4] };
        Game.ItemsOf(conn).Add(PlayerInventories.BodyArmour, 0, 0, robe);
        Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find(channel ? "Rusted Sword" : "Driftwood Wand")!));
    }
    static void Equip(Player conn, string support, string active = "SkillGemFireball")
    {
        var fireball = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + active)!);
        var trigger = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + support)!);
        var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
        { Sockets = [new(SocketColour.White, fireball), new(SocketColour.White, trigger)], Links = [2] };
        Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, wand);
    }
    sealed class ZeroCritical : Random { public override int Next(int maxValue) => 0; }
    static void WithCharacter(Action<Player> run)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cwdt-{Guid.NewGuid():N}.db");
        var old = Program.GameLogicOrNull; var oldLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game"); Game.CombatLifetime = lifetime; Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new Player(new(1, "cwdt", 1000, 1000, 100, 100, null, []), 1, 1)
        { CriticalRandom = new ZeroCritical(), Character = "Cwdt" + Guid.NewGuid().ToString("N"), PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
          Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
          LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])) };
        TestRequirements.Waive(conn);
        conn.Clock.Stop();
        try { run(conn); }
        finally { PlayerInventories.Forget(conn.Character!); lifetime.Stop(_ => { }); Game.CombatLifetime = oldLifetime; Program.SetGameLogic(old!); TestEnvironment.ReleaseSaves(path); }
    }
}
