using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    sealed class CurseCastSchedule : IPlayerUpdateSchedule
    {
        public double At = 100;
        public Action? OnWait;
        public double Now(ClientState conn) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            var action = OnWait; OnWait = null; action?.Invoke();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    async Task ManualCurseCastCommitFixture(ClientState conn, ModuleLifetime lifetime, Wire wire, Func<string, ushort> equip)
    {
        var schedule = new CurseCastSchedule();
        var updates = new PlayerUpdatesState(lifetime, schedule);
        var priorContext = Game.AreaUpdateContext.Value; var priorUpdates = conn.PlayerUpdatesState;
        var inventory = Game.ItemsOf(conn); var original = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        Game.AreaUpdateContext.Value = updates; conn.PlayerUpdatesState = updates;
        void NoCastHistory() => Assert.DoesNotContain(conn.RecentEvents.Snapshot(conn.Clock.Elapsed.TotalSeconds),
            key => key.StartsWith("cast_", StringComparison.Ordinal));
        try
        {
            foreach (var (name, token) in new[]
            {
                ("Temporal Chains", "temporal_chains"), ("Elemental Weakness", "elemental_weakness"),
                ("Enfeeble", "enfeeble"), ("Vulnerability", "vulnerability"), ("Punishment", "punishment"),
                ("Flammability", "flammability"), ("Frostbite", "frostbite"), ("Conductivity", "conductivity"), ("Despair", "dispair"),
            })
            {
                ushort skill = equip(name); conn.RecentEvents.Clear(); conn.PlayerMana = (uint)Game.UnreservedMana(conn);
                Assert.True(await Game.CastGemSkill("manual-curse-history", wire.Stream, wire.Ciphers, conn, skill, (100, 100), (100, 100), 0x800abcd), name);
                string key = StatConditions.CastEventKey(token, 10);
                Assert.True(conn.RecentEvents.Happened(key, conn.Clock.Elapsed.TotalSeconds), name);
                Assert.Single(conn.RecentEvents.Snapshot(conn.Clock.Elapsed.TotalSeconds), s => s.StartsWith("cast_", StringComparison.Ordinal));
            }
            ushort flame = equip("Flammability"); conn.RecentEvents.Clear();
            var target = conn.Monsters[0x800abcd]; target.Curses.Clear();
            Assert.True(Game.ApplyCurseLocked(conn, target, flame, schedule.At, false));
            NoCastHistory();
            target.Curses.Clear(); Assert.True(Game.ApplyCurseLocked(conn, target, flame, schedule.At, true));
            NoCastHistory();
            target.Curses.Clear();
            var session = new Game.ProjectileSession("triggered-curse-history", wire.Stream, wire.Ciphers, conn);
            try
            {
                await Game.ReleaseTriggeredNonProjectile(conn, session, flame, (100, 100), () => true);
                Assert.NotEmpty(target.Curses.All);
                NoCastHistory();
            }
            finally { session.Invalidate(); await session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(10)); }
            target.Curses.Clear();
            conn.PlayerMana = (uint)Game.UnreservedMana(conn);
            schedule.OnWait = () => conn.FreezeGeneration++;
            Assert.False(await Game.CastGemSkill("interrupted-curse-history", wire.Stream, wire.Ciphers, conn, flame, (100, 100), (100, 100), 0x800abcd));
            NoCastHistory(); Assert.Empty(target.Curses.All);
            ushort vulnerability = equip("Vulnerability"); conn.PlayerMana = (uint)Game.UnreservedMana(conn);
            schedule.OnWait = () => inventory.Remove(PlayerInventories.Weapon1,
                Assert.Single(inventory.Items(PlayerInventories.Weapon1)).Id);
            Assert.False(await Game.CastGemSkill("removed-curse-history", wire.Stream, wire.Ciphers, conn, vulnerability, (100, 100), (100, 100), 0x800abcd));
            NoCastHistory(); Assert.Empty(target.Curses.All);
        }
        finally
        {
            Game.AreaUpdateContext.Value = priorContext; conn.PlayerUpdatesState = priorUpdates;
            foreach (var item in inventory.Items(PlayerInventories.Weapon1)) inventory.Remove(PlayerInventories.Weapon1, item.Id);
            inventory.Add(PlayerInventories.Weapon1, original.X, original.Y, original.Item);
            conn.RecentEvents.Clear(); conn.Monsters[0x800abcd].Curses.Clear();
        }
    }
}
