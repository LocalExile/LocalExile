using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using League = GameServer.Core.Accounts.League;
namespace GameServer.Gateway.Tests;
public class DamageTakenTriggerTests
{
    [Fact]
    public void Cwdt_is_trigger_only_and_native_level_limit_preserves_manual_high_level_spell()
    {
        WithCharacter(player =>
        {
            Equip(player, "SupportGemCastOnDamageTaken");
            var gem = Game.GemSkillEffects(player).Single(g => g.Name == "Fireball");
            Assert.Contains("trigger", Game.GemSkillInstanceDisabled(player, gem.Effect, gem.Id) ?? "");
            player.Clock.Start();
            int action = player.ActionCount; bool sideEffects = false;
            Assert.False(Game.TryBeginSkillCooldownUse(player, gem.Id, gem.Effect, null, false,
                () => sideEffects = true, out _));
            Assert.False(sideEffects); Assert.Equal(action, player.ActionCount);
            var support = Game.SupportedSkill(player, gem.Id).Supports.Single();
            Assert.Empty(Game.Supports.Resolve(gem.Effect, [support.Gem], Game.SkillsFor(player), activeLevel: 20));
            Assert.NotEmpty(Game.Supports.Resolve(gem.Effect, [support.Gem with { Level = 20 }], Game.SkillsFor(player), activeLevel: 20));
            foreach (string triggerId in new[] { "SupportCastOnCritTriggered", "SupportCastWhileChannellingTriggered" })
            {
                var effect = Game.Supports.Effects.Single(e => e.Id == triggerId);
                var applied = Game.Supports.Resolve(gem.Effect, [new(effect.Row, 1, 0, 1)], Game.SkillsFor(player));
                Assert.Contains("trigger", Game.TriggerOnlyReason(Game.SupportedSkill(player, gem.Id) with { Supports = applied }) ?? "");
            }
            Item Levelled(string name, int level)
            {
                var item = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!);
                return item with { GemExperience = Program.Items.Gem(Program.Items.ByHash(item.TypeHash)!)!.ExperienceFor(level) };
            }
            var host = Game.ItemsOf(player).Items(PlayerInventories.Weapon1).Single();
            Game.ItemsOf(player).Change(PlayerInventories.Weapon1, host.Id, item => item with
            { Sockets = [new(SocketColour.White, Levelled("SkillGemImmortalCall", 3)),
                new(SocketColour.White, Levelled("SupportGemCastOnDamageTaken", 1)),
                new(SocketColour.White, Levelled("SupportGemAdditionalLevel", 2))], Links = [3] });
            var call = Game.GemSkillEffects(player).Single();
            Assert.Equal(4, Game.SupportedSkill(player, call.Id).Level);
            Assert.Contains("damage is taken", Game.TriggerOnlyReason(Game.SupportedSkill(player, call.Id))!);
            // Bonus levels change the spell, not its gem's required player level. A real level-up does change it.
            Game.ItemsOf(player).Change(PlayerInventories.Weapon1, host.Id, item => item with
            { Sockets = [item.Sockets[0] with { Gem = Levelled("SkillGemImmortalCall", 4) }, .. item.Sockets.Skip(1)] });
            Assert.Null(Game.TriggerOnlyReason(Game.SupportedSkill(player, call.Id)));
        });
    }
    static void VerifyNonProjectile(Player player, Game.ProjectileSession session)
    {
        foreach (var item in Game.ItemsOf(player).Items(PlayerInventories.Weapon1).ToArray())
            Game.ItemsOf(player).Remove(PlayerInventories.Weapon1, item.Id);
        Equip(player, "SupportGemCastOnDamageTaken", "SkillGemIceNova");
        var nova = Game.GemSkillEffects(player).Single(g => g.Name == "Ice Nova");
        Assert.True(Game.TriggerPayloadAvailable(player, Game.SupportedSkill(player, nova.Id)));
        Assert.Null(Game.GemSkillInstanceDisabled(player, nova.Effect, nova.Id, triggered: true));
        var origin = player.Walk.Position(0);
        var victim = new SpawnedMonster("trigger-target", new(origin.X + 1, origin.Y, 0), 0, 0, 100000)
            { Hostile = true };
        var friendly = new SpawnedMonster("friendly", new(origin.X, origin.Y, 0), 0, 0, 100000) { Hostile = false };
        player.Area.Monsters[0x800abcd] = victim; player.Area.Monsters[0x800abce] = friendly;
        int action = player.ActionCount;
        Game.ReleaseTriggeredNonProjectile(player, session, nova.Id, (1000, 1000), () => false).GetAwaiter().GetResult();
        Assert.Equal(100000u, victim.Life);
        Game.ReleaseTriggeredNonProjectile(player, session, nova.Id, (1000, 1000), () => true).GetAwaiter().GetResult();
        Assert.True(victim.Life < 100000); // Self-centred nova ignores the remote aim.
        Assert.Equal(100000u, friendly.Life);
        Assert.Equal(action, player.ActionCount);
        foreach (var item in Game.ItemsOf(player).Items(PlayerInventories.Weapon1).ToArray())
            Game.ItemsOf(player).Remove(PlayerInventories.Weapon1, item.Id);
        Equip(player, "SupportGemCastOnDamageTaken", "SkillGemFlammability");
        var curse = Game.GemSkillEffects(player).Single(g => g.Name == "Flammability");
        Assert.True(Game.TriggerPayloadAvailable(player, Game.SupportedSkill(player, curse.Id)));
        Game.ReleaseTriggeredNonProjectile(player, session, curse.Id, origin, () => true).GetAwaiter().GetResult();
        Assert.Single(victim.Curses.All);
        Assert.Empty(friendly.Curses.All);
        Assert.True(victim.Effects[Stat.BaseFireDamageResistancePct] < 0);
        Assert.Equal(action, player.ActionCount);
    }
    static void VerifyAwakenedCriticalTrigger()
    {
        WithCharacter(player =>
        {
            player.Clock.Start(); EquipLinkedSpells(player, awakened: true);
            var attack = Game.GemSkillEffects(player).Single(s => s.Name == "Kinetic Blast");
            var spell = Game.GemSkillEffects(player).Single(s => s.Name == "Fireball");
            Assert.Equal(10, Game.SupportedSkill(player, spell.Id).Stats[Stat.BaseSpellCooldownSpeedPlusPct]);
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            using var writer = new System.Net.Sockets.TcpClient(); writer.Connect((System.Net.IPEndPoint)listener.LocalEndpoint);
            using var reader = listener.AcceptTcpClient();
            player.Connection.Session = new("coc-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false));
            var session = new Game.ProjectileSession(player);
            try
            {
                var source = Game.CaptureCriticalTriggerSource(player, attack.Id);
                Assert.NotNull(source);
                Assert.True(Game.ReleaseCriticalTrigger(player, session, source, true, true, (20, 0), 10));
                Assert.True(Game.ReleaseCriticalTrigger(player, session, source, true, true, (20, 0), 10));
                Assert.False(Game.ReleaseCriticalTrigger(player, session, source, true, true, (20, 0), 10.135));
                Assert.True(Game.ReleaseCriticalTrigger(player, session, source, true, true, (20, 0), 10.137));
            }
            finally { session.Invalidate(); player.Connection.WorldOutput.Close(); }
        });
    }
    // Cast on Melee Kill: only a hostile kill by the linked melee attack releases the next linked spell.
    [Fact]
    public void Cast_on_melee_kill_releases_linked_spells_only_on_hostile_melee_kills()
    {
        WithCharacter(player =>
        {
            player.Clock.Start();
            var names = new[] { "SkillGemCleave", "SkillGemFireball", "SkillGemFreezingPulse", "SupportGemCastOnKill" };
            var robe = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
            { Sockets = [.. names.Select(n => new ItemSocket(SocketColour.White,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + n)!)))], Links = [4] };
            Game.ItemsOf(player).Add(PlayerInventories.BodyArmour, 0, 0, robe);
            Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
            var attack = Game.GemSkillEffects(player).Single(s => s.Name == "Cleave");
            var fireball = Game.GemSkillEffects(player).Single(s => s.Name == "Fireball");
            var pulse = Game.GemSkillEffects(player).Single(s => s.Name == "Freezing Pulse");
            Assert.Null(Game.CaptureCriticalTriggerSource(player, attack.Id));
            var source = Assert.IsType<Game.LinkedTriggerSource>(Game.CaptureMeleeKillTriggerSource(player, attack.Id));
            Assert.Equal(new[] { fireball.Id, pulse.Id }, source.Spells.Select(s => s.Skill));
            Assert.Contains("melee attack's kill", Game.TriggerOnlyReason(Game.SupportedSkill(player, fireball.Id))!);
            Assert.Null(Game.TriggerOnlyReason(Game.SupportedSkill(player, attack.Id)));
            Assert.Equal(1.2, GemSupportDamage.Multipliers(GameServer.Core.World.DamageTypeHistory.Fire,
                [(Stat.SupportCastOnMeleeKillSpellDamagePlusPctFinal, 20)], new HashSet<int> { 1 }).GeneralOrPhysical, 9);
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            using var writer = new System.Net.Sockets.TcpClient(); writer.Connect((System.Net.IPEndPoint)listener.LocalEndpoint);
            using var reader = listener.AcceptTcpClient();
            player.Connection.Session = new("comk-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false));
            var session = new Game.ProjectileSession(player);
            try
            {
                int action = player.ActionCount; uint mana = player.PlayerMana;
                Assert.False(Game.ReleaseMeleeKillTrigger(player, session, source, false, true, (20, 0), 10));
                Assert.False(Game.ReleaseMeleeKillTrigger(player, session, source, true, false, (20, 0), 10));
                Assert.Equal(mana, player.PlayerMana);
                Assert.True(Game.ReleaseMeleeKillTrigger(player, session, source, true, true, (20, 0), 10));
                session.AdvanceTo(0);
                var first = Assert.IsType<ProjectileOffense>(Assert.Single(session.Runtime.Projectiles).Use.Payload);
                Assert.Equal(fireball.Id, first.SourceSkill); Assert.False(first.ClientPredicted);
                Assert.True(player.PlayerMana < mana);
                Assert.True(Game.ReleaseMeleeKillTrigger(player, session, source, true, true, (20, 0), 10));
                session.AdvanceTo(0);
                Assert.Contains(session.Runtime.Projectiles, p => ((ProjectileOffense)p.Use.Payload!).SourceSkill == pulse.Id);
                Assert.Equal(action, player.ActionCount);
            }
            finally { session.Invalidate(); player.Connection.WorldOutput.Close(); }
        });
    }
    static void EquipLinkedSpells(Player player, bool channel = false, bool awakened = false)
    {
        var names = channel
            ? new[] { "SkillGemCyclone", "SkillGemFireball", "SkillGemFreezingPulse", "SupportGemCastWhileChannelling" }
            : new[] { "SkillGemKineticBlast", "SkillGemFireball", "SkillGemFreezingPulse", awakened ? "SupportGemCastOnCritPlus" : "SupportGemCastOnCrit" };
        var robe = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
        { Sockets = [.. names.Select(n => new ItemSocket(SocketColour.White,
            ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + n)!)))], Links = [4] };
        Game.ItemsOf(player).Add(PlayerInventories.BodyArmour, 0, 0, robe);
        Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find(channel ? "Rusted Sword" : "Driftwood Wand")!));
    }
    static void Equip(Player player, string support, string active = "SkillGemFireball")
    {
        var fireball = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + active)!);
        var trigger = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + support)!);
        var wand = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
        { Sockets = [new(SocketColour.White, fireball), new(SocketColour.White, trigger)], Links = [2] };
        Game.ItemsOf(player).Add(PlayerInventories.Weapon1, 0, 0, wand);
    }
    sealed class ZeroCritical : Random { public override int Next(int maxValue) => 0; }
    static void WithCharacter(Action<Player> run)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cwdt-{Guid.NewGuid():N}.db");
        var old = Program.GameLogicOrNull; var oldLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game"); Game.CombatLifetime = lifetime; Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var player = new Player(new(1, "cwdt", 1000, 1000, 100, 100, null, []), 1, 1)
        {
          CriticalRandom = new ZeroCritical(),
          Character = { Name = "Cwdt" + Guid.NewGuid().ToString("N") },
          PlayerLife = 1000,
          PlayerMana = 1000,
          Instance = new(),
          Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
          LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"]))
      };
        using var waived = TestRequirements.Waive(player);
        player.Clock.Stop();
        try { run(player); }
        finally { PlayerInventories.Forget(player.Character.Name!); lifetime.Stop(_ => { }); Game.CombatLifetime = oldLifetime; Program.SetGameLogic(old!); TestEnvironment.ReleaseSaves(path); }
    }
}
