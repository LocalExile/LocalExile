using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Totems other than Rejuvenation Totem could not be summoned and did nothing when used: the gate asked
/// <c>TotemAuras.ContainsKey(effect)</c>, which held only RejuvenationTotem, and no use was ever converted into a hit.
/// The first fact covers the gate against the real data; the second drives a placed totem's use through the tick.
/// </summary>
public sealed class TotemTests
{
    // The GrantedEffects rows the data has behind the totem skills that were broken (their ActiveSkills ids:
    // shockwave_totem, holy_flame_totem, ancestor_totem_slash, totem_fire_spray, searing_bond). SkillTotemId cannot
    // name them: every ActiveSkills row carries one.
    static readonly string[] TotemEffects =
        ["ShockwaveTotem", "HolyFlameTotem", "AncestorTotemSlash", "SearingBond", "WbFlameTotem"];

    [Fact]
    public void EveryTotemSkillCarriesSummonsTotemAndResolvesToASupportedVariety()
    {
        var skills = new SkillCatalog(); var monsters = new MonsterCatalog();
        var conn = new ClientState(new(1, "totem-catalog", 1000, 1000, 100, 100, null, []), 1, 1);
        int rejuvenation = skills.EffectRow("RejuvenationTotem");
        Assert.True(rejuvenation >= 0);
        // The old gate's only key: TotemAuras held RejuvenationTotem and nothing else.
        Assert.DoesNotContain(rejuvenation, TotemEffects.Select(skills.EffectRow));
        foreach (string effectId in TotemEffects)
        {
            int row = skills.EffectRow(effectId);
            Assert.True(row >= 0, $"missing GrantedEffects row: {effectId}");
            Assert.True(skills.HasType(row, Game.TotemSkillType), $"{effectId} (row {row}) is not {Game.TotemSkillType}");
            var variety = Game.TotemVariety(conn, row);
            Assert.True(variety is not null, $"{effectId} (row {row}) summons no totem variety");
            Assert.True(variety!.Supported, $"{effectId} variety {variety.Id} ({variety.Row}) is not supported");
            Assert.NotNull(monsters.Find(variety.Row.ToString()));
        }
        // Rejuvenation Totem keeps working, so the fix is a widening of the gate rather than a replacement.
        Assert.NotNull(Game.TotemVariety(conn, rejuvenation));
    }

    [Fact]
    public async Task ATotemAimsOnlyAtHostilesInRangeAndItsCapturedStrikeLands()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"localexile-totems-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull; var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        using var wire = new Wire();
        Saves.Open(path); Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "totem-runtime", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "TotemRuntime", Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("totem-test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemShockwaveTotem")!);
            var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
            { Sockets = [new(SocketColour.White, gem)] };
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon);
            var source = Game.GemSkillEffects(conn).Single(g => g.Name == "Shockwave Totem");
            var variety = Game.TotemVariety(conn, source.Effect);
            Assert.NotNull(variety);
            uint? placed = await Game.CreateTotem("test", wire.Stream, wire.Ciphers, conn, source.Id, source.Effect,
                source.Level, variety!, (50, 50));
            Assert.NotNull(placed);
            var totem = conn.Totems.All.Single();
            // The hybrid boundary: this is a dedicated totem skill, not an adopted projectile spell, so it keeps the
            // local contact path (a Strike) and is never ticked by the spell executor (Effect stays -1). The lab reads
            // the effect row it was summoned from through EffectRow, which is the deliberately different fact.
            Assert.True(totem.Effect < 0, "a dedicated totem skill must not be an adopted projectile spell totem");
            Assert.Equal(source.Effect, totem.EffectRow);
            // The captured hit is the half that made every other totem "produce no effect".
            Assert.NotNull(totem.Strike);
            Assert.False(totem.Strike!.Hit.IsZero);

            // A hostile outside TotemRange (60 cells) is never aimed at.
            conn.Monsters[0x08000002] = new SpawnedMonster("far", new(50, 130, 0), 100, 8, 1000);
            Game.TickTotemUses("test", wire.Stream, wire.Ciphers, conn);
            Assert.Equal(0u, totem.Strike!.Target);

            // A hostile inside it is aimed at, and the use reports the granted action the client plays.
            var target = new SpawnedMonster("near", new(55, 50, 0), 100, 8, 100000);
            conn.Monsters[0x08000001] = target;
            Game.TickTotemUses("test", wire.Stream, wire.Ciphers, conn);
            Assert.Equal(0x08000001u, totem.Strike!.Target);
            Assert.Equal((55, 50), totem.Strike.Aim);

            // Reach the contact without waiting on real time: the tick reads the connection clock, so the test moves
            // the use in flight to the past by hand.
            uint before = target.Life;
            totem.Strike.ContactAt = 0;
            Game.TickTotemUses("test", wire.Stream, wire.Ciphers, conn);
            Assert.True(target.Life < before, $"the strike dealt no damage: {target.Life} of {target.MaxLife}");

            // /totems reads the same collection through the same call the command makes, so the listing carries the
            // fields the task needs — a totem the lab can see at all. (The Monsters array of /state cannot list one:
            // a totem never enters ClientState.Monsters.)
            string listing = Game.TotemsListing(conn);
            Assert.Contains("0x", listing);
            Assert.Contains(variety!.Id, listing);
            Assert.Contains($"life {totem.Life}/{totem.MaxLife}", listing);
            Assert.Contains($"at ({totem.At.X},{totem.At.Y})", listing);
            Assert.Contains($"skill {totem.SkillId}", listing);
            Assert.Contains("ShockwaveTotem", listing);
            Assert.EndsWith("1 totem(s).", listing);
        }
        finally
        {
            conn.Closed = true; PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!); TestEnvironment.ReleaseSaves(path);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(); readonly TcpClient reader;
        readonly Task drain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            drain = Task.Run(async () =>
            {
                try { var buffer = new byte[65536]; while (await reader.GetStream().ReadAsync(buffer) > 0) { } }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            });
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); }
    }
}
