using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>The official server's Soul Eater line: the ServerBeamEffect blob, captured from 3.29.3c (row 0,
/// flags 0x0C97, world endpoints as grid cells times ten, both ends' object ids), as our client reads it.</summary>
public sealed class SoulEaterBeamTests
{
    [Fact]
    public void Soul_beam_blob_is_the_official_packet()
    {
        var blob = new BeamEffectObject(1421, 612, 0x452, 14099.1f, 5936.7f, 0x4B9).Blob();
        Assert.Equal(
            "00008D050000640200000000000000000000000100970C0000085E460040BF455204000000000000664C5C46" +
            "9A85B945B90400000000000000",
            Convert.ToHexString(blob));
    }

    [Fact]
    public void Recovery_flasks_publish_zero_soul_transitions_and_the_real_stack_limit()
    {
        string path = Path.Combine(Path.GetTempPath(), $"soul-eater-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Saves.Open(path);
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            var conn = new ClientState(AreaLayouts.For(AreaLayouts.LioneyesWatch, 1), 1, 1)
            {
                Character = "SoulPresentation", ClassRow = 1, PlayerSent = true, Instance = new(),
            };
            conn.Clock.Stop();
            conn.Pools = Game.PoolsFor(conn, 40);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            uint Key(string name) => Program.Stats.Key(name) ?? throw new InvalidOperationException(name);
            byte[] Present() => Assert.IsType<byte[]>(Game.SoulEaterPresentation(conn, "test", null!, null!));
            void HasStats(byte[] packet)
            {
                var header = InstanceEntities.StatsUpdate(Program.PlayerId, []).AsSpan(0, 12);
                int offset = packet.AsSpan().IndexOf(header);
                Assert.True(offset >= 0, "The presentation must carry a player stat update.");
                int cursor = offset + header.Length;
                uint Compact()
                {
                    byte first = packet[cursor++];
                    int following = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
                    uint value = following == 4 ? 0u : (uint)(first & (0x7f >> following));
                    for (int i = 0; i < following; i++) value = (value << 8) | packet[cursor++];
                    return value;
                }
                uint count = Compact();
                var sent = new Dictionary<uint, uint>();
                for (uint i = 0; i < count; i++) sent[Compact()] = Compact();
                // These native fields are nonnegative; other signed stat values need not be interpreted here.
                Assert.Equal(Game.AnyFlaskEffect(conn) ? 1u : 0u, sent[Key("using_flask")]);
                if (conn.SoulEater.Enabled)
                    Assert.Equal((uint)conn.SoulEater.Limit, sent[Key("virtual_soul_eater_maximum_stacks")]);
            }
            double now = conn.Clock.Elapsed.TotalSeconds;
            conn.PlayerStats[Key(Game.SoulEaterStat)] = 1;
            conn.PlayerStats[Key("soul_eater_maximum_stacks_+")] = 10;
            conn.FlaskEffects.Start(new(0, 1, 0, 10, 0, now, now + 10));
            HasStats(Present());
            Assert.True(conn.SoulEater.SentUsingFlask);
            Assert.NotEqual(0, conn.SoulEater.AuraBuff);
            Assert.Equal(0, conn.SoulEater.SentStacks);
            Assert.Equal(55, conn.SoulEater.Limit);
            Assert.Null(Game.SoulEaterPresentation(conn, "test", null!, null!));

            for (int i = 0; i < 70; i++) conn.SoulEater.Soul(now);
            HasStats(Present());
            Assert.Equal(55, conn.SoulEater.SentStacks);
            conn.PlayerStats[Key("soul_eater_maximum_stacks_+")] = -10;
            HasStats(Present());
            Assert.Equal(35, conn.SoulEater.SentStacks);
            Assert.Equal(35, Game.CharacterPlayerStats(conn)[Key("virtual_soul_eater_maximum_stacks")]);

            uint beam = conn.NextObjectId;
            conn.SoulEater.Souls.Add((7, 10, 10));
            Present();
            Assert.True(conn.TemporarySkillEffects.TryGetValue(beam, out double deadline));
            Assert.True(deadline > now);
            conn.RetirePlayerUpdates();
            Assert.Equal(deadline, conn.TemporarySkillEffects[beam]);

            // An expired source must discard pending visuals, not schedule a beam after its aura is gone.
            conn.SoulEater.Souls.Add((7, 10, 10));
            conn.FlaskEffects.End(_ => true);
            HasStats(Present());
            Assert.Empty(conn.SoulEater.Souls);
            Assert.False(conn.SoulEater.Enabled);
            Assert.False(conn.SoulEater.SentUsingFlask);
            Assert.Equal(0, conn.SoulEater.AuraBuff);
            Assert.Equal(0, conn.SoulEater.Buff);
            Assert.Equal(0, conn.SoulEater.SentStacks);

            // The native flask flag also updates when the character has no Soul Eater source or souls at all.
            conn.PlayerStats.Remove(Key(Game.SoulEaterStat));
            conn.FlaskEffects.Start(new(0, 2, 0, 10, 0, now, now + 10));
            HasStats(Present());
            Assert.True(conn.SoulEater.SentUsingFlask);
            Assert.Equal(0, conn.SoulEater.AuraBuff);
            conn.FlaskEffects.End(_ => true);
            HasStats(Present());
            Assert.False(conn.SoulEater.SentUsingFlask);
            Assert.Null(Game.SoulEaterPresentation(conn, "test", null!, null!));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
