using System.Net;
using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class BlockLuckAndTriggerOnBlockTests
{
    static void WithTestCharacter(Action<Player> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"block-luck-{Guid.NewGuid():N}.db");
        var oldLogic = Program.GameLogicOrNull;
        var oldLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var player = new Player(new(1, "blockluck", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character =
            {
                Name = "Hero" + Guid.NewGuid().ToString("N")[..8],
                ClassRow = 0,
                Experience = CharacterStats.LevelStarts[80],
            },
            PlayerLife = 3000,
            PlayerMana = 2000,
            Connection = { PlayerSent = true },
            Instance = new(),
            Pools = new(80, 3000, 2000, 0, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        player.Clock.Stop();
        TestRequirements.Waive(player);
        using var reqHook = GameEvents.PlayerStatSources.Add("Game", (p, s) =>
        {
            if (ReferenceEquals(p, player))
            {
                s[Stat.VirtualStrengthForRequirements] = 1000;
                s[Stat.VirtualDexterityForRequirements] = 1000;
                s[Stat.VirtualIntelligenceForRequirements] = 1000;
            }
            return s;
        });
        try
        {
            body(player);
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            lifetime.Stop(_ => { });
            Game.CombatLifetime = oldLifetime;
            Program.SetGameLogic(oldLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Svalinn_and_Gladiator_More_Than_Skill_grant_lucky_block_panel_stats_and_trigger_on_block()
    {
        var items = new ItemCatalog("data");
        var uniques = new UniqueCatalog(items);
        var svalinn = Assert.IsType<Item>(uniques.Create("Svalinn", new Random(17)));

        var fireballBase = items.Find("Metadata/Items/Gems/SkillGemFireball")!;
        var fireball = ItemCatalog.Create(fireballBase);
        svalinn = svalinn with
        {
            Sockets = [new(SocketColour.Blue, fireball), new(SocketColour.Blue, null), new(SocketColour.Red, null)],
            Links = [3],
        };

        WithTestCharacter(player =>
        {
            player.Clock.Start();
            Game.ItemsOf(player).Add(PlayerInventories.Offhand1, 0, 0, svalinn);
            player.MarkStatsDirty(StatInput.Items);
            player.Pools = Game.PoolsFor(player, 80);

            Assert.True(player.Pools.AttackBlockLucky);
            Assert.True(player.Pools.SpellBlockLucky);
            Assert.Equal(65, player.Pools.BlockChanceMax);
            Assert.Equal(65, player.Pools.SpellBlockChanceMax);
            Assert.True(player.Pools.Ward >= 100);

            var panelAttrs = Game.ClassAttributes(player.Character.ClassRow, player.Pools).ToDictionary(p => p.Item1, p => p.Item2);
            Assert.Equal(1, panelAttrs[Stat.AttackBlockLuck]);
            Assert.Equal(1, panelAttrs[Stat.SpellBlockLuck]);

            var skill = Game.GemSkillEffects(player).Single(g => g.Name == "Fireball");
            var supported = Game.SupportedSkill(player, skill.Id);
            Assert.Contains(supported.Supports, s => s.Effect.Id == "SupportTriggerElementalSpellOnBlock");
            Assert.Contains("when you block", Game.TriggerOnlyReason(supported) ?? "", StringComparison.OrdinalIgnoreCase);

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var writer = new TcpClient();
            writer.Connect((IPEndPoint)listener.LocalEndpoint);
            using var _ = listener.AcceptTcpClient();
            var ciphers = new ConnectionCiphers(new byte[64], false);
            player.Connection.Session = new("svalinn-test", writer.GetStream(), ciphers);
            var session = Game.ProjectilesFor(player);
            player.ProjectileRuntimeState = session;
            try
            {
                // First block triggers Fireball immediately; second immediate block respects the 250ms cooldown
                uint manaBefore = player.PlayerMana;
                Game.TriggerOnBlockSpells(player, 0);
                Game.TriggerOnBlockSpells(player, 0);
                session.AdvanceTo(Math.Max(session.Runtime.Now,
                    GameServer.Core.Projectiles.ProjectileRuntime.Time(player.Clock.Elapsed.TotalSeconds + 0.02)));
                Assert.Single(session.Runtime.Projectiles);
                Assert.True(player.PlayerMana < manaBefore);
            }
            finally
            {
                session.Invalidate();
            }
        });

        // Gladiator "More Than Skill": spell_and_attack_block_chance_is_lucky_if_blocked_recently
        bool Exists(string name) => Program.Stats.Key(name) is not null;
        var recentEmpty = new StatConditions.Subject(Recent: new HashSet<string>());
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("spell_and_attack_block_chance_is_lucky_if_blocked_recently", recentEmpty));

        var recentBlocked = new StatConditions.Subject(Recent: new HashSet<string>([StatConditions.Blocked]));
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("spell_and_attack_block_chance_is_lucky_if_blocked_recently", recentBlocked));
        StatConditions.Counted("spell_and_attack_block_chance_is_lucky_if_blocked_recently", 1,
            StatConditions.State.Holds, recentBlocked, Exists, out var targets);
        Assert.Equal(["base_block_luck"], targets);
    }
}
