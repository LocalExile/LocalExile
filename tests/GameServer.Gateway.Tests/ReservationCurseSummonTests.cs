using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    [Fact]
    public void LifeReservationIsSeparateCappedAndCursesReplaceWithoutMutatingMods()
    {
        var pools = new CharacterStats.Pools(90, 1200, 800, 2, 0, LifeReservationEfficiencyPct: 100);
        var life = new AuraCatalog.Aura(1, 0, "Vitality", "Vitality", 1, 153, 200, 0, ReservesLife: true);
        var mana = life with { SkillId = 2, ReservesLife = false, ManaFlat = 0, ManaPercent = 5000 };
        Assert.Equal(100, AuraCatalog.RequiredLife(pools, [life, mana]));
        Assert.Equal(400, AuraCatalog.RequiredMana(pools, [life, mana]));
        Assert.Equal(67, AuraCatalog.RequiredLife(pools, [life with { ReservationEfficiencyPct = 100 }]));
        var player = new Player(new(1, "reserve-test", 100, 100, 10, 10, null, []), 1, 1) { Pools = pools, PlayerLife = 1200 };
        player.Auras[1] = life;
        Assert.Equal(1100u, player.PlayerUnreservedLife);
        double carry = 0;
        Assert.Equal(1100u, PetrifiedBlood.RecoverLife(player, 1000, 500, ref carry));
        Assert.Empty(AuraCatalog.Restore(["Vitality"], [life with { ManaFlat = 2400 }], pools));
        var curses = new MonsterCurses();
        var original = new MonsterModEffects(new Dictionary<uint, int> { [Stat.BaseFireDamageResistancePct] = 20 });
        MonsterCurses.Curse Curse(uint caster, string effect, int value, double at) => new(caster, effect, 1, 71, (ushort)at, at, 20, false, false,
            new Dictionary<uint, int> { [Stat.BaseFireDamageResistancePct] = value });
        curses.Put(Curse(1, "Flammability", -36, 1), 1);
        Assert.Equal(-16, original.Combine(curses.Stats).Resistance(DamageType.Fire));
        curses.Put(Curse(2, "Flammability", -20, 2), 1);
        Assert.Equal(-16, original.Combine(curses.Stats).Resistance(DamageType.Fire));
        curses.Put(Curse(1, "ElementalWeakness", -30, 3), 1);
        Assert.DoesNotContain((1u, "Flammability"), curses.All.Keys);
        Assert.Equal(-30, curses[Stat.BaseFireDamageResistancePct]);
        Assert.Single(curses.All);
        curses.Put(Curse(2, "Flammability", -20, 4), 2);
        Assert.Equal(-50, curses[Stat.BaseFireDamageResistancePct]);
        Assert.Equal(20, original.Resistance(DamageType.Fire));
        curses.Expire(20);
        Assert.Empty(curses.All); Assert.Empty(curses.Stats);
        curses.Put(Curse(3, "TemporalChains", -10, 21) with { Aura = true, ExpandedCapacity = 6, Until = 60 }, 1);
        curses.Put(Curse(1, "Flammability", -20, 22) with { Until = 60 }, 1);
        curses.Put(Curse(1, "ElementalWeakness", -30, 23) with { Until = 60 }, 1);
        Assert.Equal(2, curses.All.Count);
        Assert.Contains((3u, "TemporalChains"), curses.All.Keys);
        Assert.Contains((1u, "ElementalWeakness"), curses.All.Keys);
        Assert.DoesNotContain((1u, "Flammability"), curses.All.Keys);
        curses.Clear();
        curses.Put(Curse(1, "Flammability", -20, 22) with { Until = 60 }, 2);
        curses.Put(Curse(1, "ElementalWeakness", -30, 23) with { Until = 60 }, 2);
        curses.Put(Curse(3, "TemporalChains", -10, 24) with { Aura = true, ExpandedCapacity = 6, Until = 60 }, 1);
        Assert.Equal(3, curses.All.Count);
        Assert.Equal(14, CurseCatalog.All.Count); Assert.False(CurseCatalog.Hex("AssassinsMark"));
        var skitter = new GolemState.Golem(1, null!, new(10825, "SummonSkitterbots", "SkitterCold", "", 0, [], false, false, false), 1)
            { Own = Program.Skills.EffectStats(10825, 20).ToDictionary(p => p.Item1, p => p.Item2) };
        var ailments = new MonsterElementalAilments();
        ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, 1, 0, n => Game.SkitterAilmentStat(skitter, n), _ => 0);
        ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, 1, 0, n => Game.SkitterAilmentStat(skitter, n), _ => 0);
        Assert.Equal(11, ailments.Magnitude(MonsterElementalAilments.Kind.Chill, 0));
        Assert.Equal(17, ailments.Magnitude(MonsterElementalAilments.Kind.Shock, 0));
    }

    [Fact]
    public async Task Equipment_reservation_efficiency_updates_reserved_mana_immediately_during_frame_updates()
    {
        using var f = new MinionUtilityTests.Fixture();
        var player = f.Player;
        ushort id = f.Equip(("Determination", 20)).Single();
        var det = Game.SupportedSkill(player, id);
        var updates = new PlayerUpdatesState(f.Lifetime, new FixedSchedule());
        player.PlayerUpdatesState = updates;
        Assert.True(player.PlayerUpdatesWorker.TryStart(player, updates.Rules, null, updates, out updates.Generation));
        await Program.BeginWorldFrames(player, Program.FrameMs / 1000d);
        player.Area.Systems.Add("stats", AreaPhase.Stats, _ => Game.StepPlayerStats(player), source: f.Lifetime);
        Assert.True(await Game.ToggleAura(player, id, det.Effect, "Determination",
            Game.SupportedReservationCost(player, id)!.Value, det.Level, det.Quality));
        player.Clock.Stop();
        await player.Frames.EndPausedFrame(player);
        int reservedWithoutBelt = Game.ReservedMana(player);

        var mod = Program.Items.Mods.ById("V2ManaReservationEfficiencyDeterminationCorrupted")!;
        var belt = ItemCatalog.Create(Program.Items.Find("Leather Belt")!) with
        {
            Mods = [new ItemMod(ModSection.Implicit, mod.Hash, [30])],
        };
        Game.ItemsOf(player).Put(PlayerInventories.Belt, new(310, 0, 0, belt));
        Game.ReconcileOwnAuras(player);
        int reservedWithBelt = Game.ReservedMana(player);
        Assert.True(reservedWithBelt < reservedWithoutBelt);
        Game.StepPlayerStats(player);
        Assert.Equal(reservedWithBelt, Game.ReservedMana(player));

        Game.ItemsOf(player).Remove(PlayerInventories.Belt, 310);
        Game.ReconcileOwnAuras(player);
        Assert.Equal(reservedWithoutBelt, Game.ReservedMana(player));
        Assert.Equal(0, Game.StatSourceValues(player).GetValueOrDefault(Stat.Of("determination_mana_reservation_efficiency_+%")));
    }

    sealed class FixedSchedule : IPlayerUpdateSchedule
    {
        public double Now(Player player) => 1;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0); readonly TcpClient writer = new(); readonly TcpClient reader; readonly Task drain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false); public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync(); writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult(); drain = Task.Run(async () => { try { var b = new byte[65536]; while (await reader.GetStream().ReadAsync(b) > 0) { } } catch (Exception e) when (e is IOException or ObjectDisposedException) { } });
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); }
    }

    sealed class CatClockSchedule : IPlayerUpdateSchedule
    {
        public double At;
        public bool AdvanceOnWait = false;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            if (AdvanceOnWait) At += milliseconds / 1000.0;
            return Task.CompletedTask;
        }
    }

}
