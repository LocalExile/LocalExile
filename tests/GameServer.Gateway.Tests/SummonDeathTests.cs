using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// A summon whose time is up dies where it stands (life 0), and its body is removed 21 frames later, once.
public sealed class SummonDeathTests
{
    sealed class Frames : IPlayerUpdateSchedule
    {
        public double Time;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping) { Time += milliseconds / 1000.0; return Task.CompletedTask; }
    }

    // Raises a zombie at time 1 and lets its time run out at time 2; returns what was sent and when.
    static async Task<(uint Id, SpawnedMonster Zombie, GolemState.Golem Fighter, List<(double At, byte[] Bytes)> Sent)> Expire(
        MinionUtilityTests.Fixture f, Frames frames)
    {
        var player = f.Player;
        f.Equip(("Raise Zombie", 20));
        const uint corpseId = 0x800abcd;
        player.Area.Monsters[corpseId] = new SpawnedMonster("corpse", new(100, 100, 0), 0, 0, 1000)
            { Dead = true, Life = 0, Hostile = true, Variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!, Level = 70, ObjectId = corpseId };
        frames.Time = 1;
        uint id = await f.Summon("RaiseZombie", corpseId);
        var fighter = player.Summons.Fighters[id];
        var sent = new List<(double, byte[])>();
        var previous = Program.Outgoing;
        Program.Outgoing = (_, bytes) => { lock (sent) sent.Add((frames.Time, (byte[])bytes.Clone())); };
        try
        {
            player.Summons.Until[id] = 2;
            frames.Time = 2;
            await Game.SummonsTick(player, frames.Time);
        }
        finally { Program.Outgoing = previous; }
        return (id, fighter.Monster, fighter, sent);
    }

    static (int Message, double At) Find(List<(double At, byte[] Bytes)> sent, byte[] bytes)
    {
        lock (sent)
            for (int i = 0; i < sent.Count; i++)
                if (sent[i].Bytes.AsSpan().IndexOf(bytes) >= 0) return (i, sent[i].At);
        return (-1, 0);
    }

    static int Count(List<(double At, byte[] Bytes)> sent, byte[] bytes)
    {
        lock (sent) return sent.Count(s => s.Bytes.AsSpan().IndexOf(bytes) >= 0);
    }

    [Fact]
    public async Task A_summon_whose_time_is_up_dies_first_is_no_corpse_and_its_body_is_removed_21_frames_later()
    {
        using var f = new MinionUtilityTests.Fixture();
        var frames = new Frames(); var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, frames);
        try
        {
            var sent = new List<(double At, byte[] Bytes)>();
            var (id, zombie, _, afterExpiry) = await Expire(f, frames);
            sent.AddRange(afterExpiry);
            var remove = InstanceProtocol.RemoveObject(id); remove[6] = remove[7] = remove[8] = remove[9] = 0xff;   // as the area writes it
            // Time is up: it left the roster, shows life 0 at once and is no corpse a raise or an offering could use.
            Assert.Empty(f.Player.Summons.Fighters);
            Assert.True(zombie.Dead && zombie.Life == 0 && zombie.CorpseConsumed);
            Assert.True(f.Player.Area.Monsters.ContainsKey(id));
            Assert.Equal(-1, Find(sent, remove).Message);
            // What the tick queued for the owner's client: the life 0 and no removal yet.
            var queue = f.Player.Minions.OutputQueue;
            Assert.Single(queue, o => o.Kind == PlayerMinions.OutputKind.Life && o.TargetId == id && ReferenceEquals(o.Target, zombie));
            Assert.DoesNotContain(queue, o => o.RemovesObject && o.WorldId == id);

            var previous = Program.Outgoing;
            Program.Outgoing = (_, bytes) => { lock (sent) sent.Add((frames.Time, (byte[])bytes.Clone())); };
            try
            {
                frames.Time = 2 + Game.SummonBodySeconds - .05;
                await Game.PlayerMinionsTick(f.Player, frames.Time);
                Assert.Equal(-1, Find(sent, remove).Message);
                frames.Time = 2 + Game.SummonBodySeconds + .05;
                await Game.PlayerMinionsTick(f.Player, frames.Time);
                await Game.PlayerMinionsTick(f.Player, frames.Time + 1);
            }
            finally { Program.Outgoing = previous; }
            var removed = Find(sent, remove);
            Assert.True(removed.Message >= 0);
            Assert.InRange(removed.At - 2, Game.SummonBodySeconds - .15, Game.SummonBodySeconds + .15);
            // Every message about the body that is not its removal came before the removal.
            lock (sent) Assert.DoesNotContain(sent.Skip(removed.Message + 1), s => s.Bytes.AsSpan().IndexOf(BitConverter.GetBytes(id).Reverse().ToArray()) >= 0);
            Assert.Equal(1, Count(sent, remove));
            Assert.False(f.Player.Area.Monsters.ContainsKey(id));
            Assert.False(f.Player.MinionOrigins.ContainsKey(id));
            Assert.Empty(f.Player.Minions.Bodies);
        }
        finally { Game.AreaUpdateContext.Value = outer; }
    }

    [Fact]
    public async Task A_body_that_two_paths_reach_is_removed_once()
    {
        using var f = new MinionUtilityTests.Fixture();
        var frames = new Frames(); var outer = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(Game.CombatLifetime!, frames);
        try
        {
            var (id, zombie, fighter, _) = await Expire(f, frames);
            // A second path reaches the same body while it waits (the roster knows it again for a tick): no second entry.
            f.Player.Summons.Fighters[id] = fighter; f.Player.Summons.Until[id] = 2;
            f.Player.Summons.Definitions[id] = Game.SummonData.Find("RaiseZombie")!;
            await Game.SummonsTick(f.Player, 2.1);
            Assert.Single(f.Player.Minions.Bodies, b => b.Id == id);
            Assert.Empty(f.Player.Summons.Fighters);

            var sent = new List<(double, byte[])>(); var previous = Program.Outgoing;
            Program.Outgoing = (_, bytes) => { lock (sent) sent.Add((frames.Time, (byte[])bytes.Clone())); };
            try
            {
                frames.Time = 3;
                await Game.PlayerMinionsTick(f.Player, frames.Time);
                await Game.PlayerMinionsTick(f.Player, frames.Time + 1);
            }
            finally { Program.Outgoing = previous; }
            var gone = InstanceProtocol.RemoveObject(id); gone[6] = gone[7] = gone[8] = gone[9] = 0xff;
            Assert.Equal(1, Count(sent, gone));
            Assert.False(f.Player.Area.Monsters.ContainsKey(id));
        }
        finally { Game.AreaUpdateContext.Value = outer; }
    }
}
