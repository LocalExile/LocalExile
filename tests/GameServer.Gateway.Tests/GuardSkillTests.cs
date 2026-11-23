using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class GuardSkillTests
{
    [Fact]
    public async Task Casting_immortal_call_applies_its_buff_and_holds_the_shared_guard_cooldown()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Immortal Call", 1));
        var gem = Game.GemSkillEffects(f.Player).Single(g => Game.SkillsFor(f.Player).EffectId(g.Effect) == "ImmortalCall");
        var charges = Game.ChargesOf(f.Player);
        var limits = charges.Limits(ChargeType.Endurance);
        charges.State.Set(ChargeType.Endurance, limits.Minimum + 3, 0, limits);
        double now = Game.AreaTime(f.Player);
        uint mana = f.Player.PlayerMana;
        await Game.UseInstantSkill(f.Player, gem.Id, 0, f.Player.Walk.Position(now));
        double castAt = Game.AreaTime(f.Player);
        Assert.Equal(limits.Minimum, charges.Effective(ChargeType.Endurance));
        Assert.True(f.Player.PlayerMana < mana);
        Assert.False(Game.TriggerGuardReady(f.Player, castAt));
        // While the buff runs the bar entry is the skill's own cooldown at a zero recovery rate, which is the official
        // client's frozen entry (measured 3.00 / 3.00 / rate 0): the ring does not move and the icon stays dark.
        var stored = Game.SingleUseState(f.Player, gem.Effect, gem.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.Held);
        Assert.Equal(3000, stored.TotalBaseMilliseconds);
        Assert.Equal(3000, (int)stored.RemainingBaseMilliseconds);
        stored.Refresh(castAt + 1, stored.Rate);
        Assert.Equal(3000, (int)stored.RemainingBaseMilliseconds);   // held: its clock does not run
        var held = Game.GuardCooldownEntry(f.Player, gem.Id, gem.Effect);
        Assert.Equal(InstanceEntities.PlayerSingleUseCooldowns(f.Player.Id,
            [new SingleUseCooldownUpdate(gem.Id, 3f, 3f, 0f, 1)]), held);
        // The pre-entry published before the instant action is the same frozen rate-0 entry, built from the skill's base
        // cooldown without touching the state, so the client skips the press prediction it would start from a use (#1127).
        Assert.Equal(held, Game.GuardCooldownPreEntry(f.Player, gem.Id, gem.Effect));
        // The record's own cooldown base is the gem's 3 s (the bar dims the icon against it), like the official client.
        Assert.Equal(3000u, Game.PlayerSkills(f.Player).Single(s => s.Id == gem.Id).CooldownMs);
        // Level 1 with three charges: 25% less Physical and Elemental Damage, then 15% less Physical per charge.
        Assert.Equal((412, 750, 1000), Game.TriggeredGuardHit(f.Player, 1000, 1000, 1000, castAt));
        // The buff is 1 s + 20% per charge, and the 3 s cooldown does not recover during it.
        double ended = double.NaN, ready = double.NaN;
        for (double t = castAt; t < castAt + 8; t += 0.01)
        {
            if (double.IsNaN(ended) && Game.TriggeredGuardHit(f.Player, 1000, 0, 0, t).Physical >= 1000) ended = t;
            if (double.IsNaN(ready) && Game.TriggerGuardReady(f.Player, t)) { ready = t; break; }
        }
        Assert.InRange(ended - castAt, 1.2, 1.8);
        Assert.InRange(ready - ended, 2.9, 3.1);
        // The buff is over: the held entry became the skill's own 3 s, counting at the character's recovery rate from
        // the moment the buff ended, and it expires when the guard becomes ready (TriggerGuardReady) again.
        Assert.False(stored.Held);
        Assert.Equal(3000, stored.TotalBaseMilliseconds);
        Assert.False(stored.FullyRecovered);
        stored.Refresh(ended + 1.5 / stored.Rate, stored.Rate);
        Assert.InRange(stored.RemainingBaseMilliseconds, 1490, 1510);
        stored.Refresh(ended + 3.5 / stored.Rate, stored.Rate);
        Assert.True(stored.FullyRecovered);
        Assert.Null(Game.GuardCooldownEntry(f.Player, gem.Id, gem.Effect));
        // A second cast is refused by the shared guard cooldown before any cost, so nothing is spent.
        mana = f.Player.PlayerMana;
        await Game.UseInstantSkill(f.Player, gem.Id, 0, f.Player.Walk.Position(now));
        Assert.Equal(mana, f.Player.PlayerMana);
        Assert.Equal(limits.Minimum, charges.Effective(ChargeType.Endurance));

        // Arcane Cloak has the Guard type but no guard buff here: a cast must not start one (with Steelskin's icon).
        using var cloak = new MinionUtilityTests.Fixture();
        cloak.Equip(("Arcane Cloak", 1));
        var arcane = Game.GemSkillEffects(cloak.Player).Single(g => Game.SkillsFor(cloak.Player).EffectId(g.Effect) == "ArcaneCloak");
        Assert.True(Game.TryBeginSkillCooldownUse(cloak.Player, arcane.Id, arcane.Effect, Game.CooldownForSkill(cloak.Player, arcane.Id),
            false, () => cloak.Player.ActionCount++, out var ticket));
        await Game.CastGemSkill(cloak.Player, arcane.Id, (100, 100), (100, 100),
            cooldownUse: ticket);
        Assert.True(Game.TriggerGuardReady(cloak.Player, Game.AreaTime(cloak.Player)));
    }

    // Issue #1127: a guard's stat-only start rebuilt the skill bar (0x53), which re-creates every slot and resets its
    // cooldown entry to the skill's own recovery rate, replaying the bar's cooldown animation. Only a real skill change
    // (a skill added or removed) rebuilds the bar; a pure stat refresh keeps the slots and their entries.
    [Fact]
    public async Task A_stat_only_skill_refresh_does_not_rebuild_the_skill_bar()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Player.Connection.LockstepClient = false;
        f.Equip(("Immortal Call", 1), ("Steelskin", 1));
        CharacterSaves.SetSkillBar(f.Player.Character.Name!, 2, "ImmortalCall");
        CharacterSaves.SetSkillBar(f.Player.Character.Name!, 3, "Steelskin");
        var packets = new List<byte[]>();
        var outgoing = Program.Outgoing;
        Program.Outgoing = (player, bytes) => { if (ReferenceEquals(player, f.Player)) packets.Add(bytes.ToArray()); };
        bool Bar(byte[] p) => p.Length > 2 && p[0] == 0 && p[1] == 0x53;
        try
        {
            // The login refresh establishes the client's skill ids (and binds the saved bar).
            await Game.SendCharacterStats(f.Player, PlayerStatChange.Build);
            packets.Clear();
            // A guard or self buff starting/ending, or an aura's stats recalculated, adds and removes no skill: the bar
            // must not go out again, so the slots keep their cooldown entries (the guard's held freeze is not reset).
            await Game.SendCharacterStats(f.Player, PlayerStatChange.Sources);
            Assert.DoesNotContain(packets, Bar);
            packets.Clear();
            // A skill leaving the equipment is a real change: a bound slot may have emptied, so the saved bar goes out.
            f.Equip(("Immortal Call", 1));
            await Game.SendCharacterStats(f.Player, PlayerStatChange.Build);
            Assert.Contains(packets, Bar);
        }
        finally { Program.Outgoing = outgoing; }
    }

    // Issue #1127 (movement): a stat refresh re-sends the skill list, and the client's skill replacement refreshes an
    // existing zero-rate cooldown entry (0x141ef1573), so a guard's held entry started counting again while its buff ran.
    // The held rate-0 entry must go out again behind the skill list so the bar stays frozen.
    [Fact]
    public async Task A_skill_refresh_during_a_held_guard_republishes_its_frozen_entry()
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Player.Connection.LockstepClient = false;
        f.Equip(("Immortal Call", 1));
        var gem = Game.GemSkillEffects(f.Player).Single(g => Game.SkillsFor(f.Player).EffectId(g.Effect) == "ImmortalCall");
        var charges = Game.ChargesOf(f.Player);
        var limits = charges.Limits(ChargeType.Endurance);
        charges.State.Set(ChargeType.Endurance, limits.Minimum + 3, 0, limits);
        double now = Game.AreaTime(f.Player);
        await Game.UseInstantSkill(f.Player, gem.Id, 0, f.Player.Walk.Position(now));
        Assert.True(Game.SingleUseState(f.Player, gem.Effect, gem.Id)!.Held);
        var frozen = InstanceEntities.PlayerSingleUseCooldowns(f.Player.Id,
            [new SingleUseCooldownUpdate(gem.Id, 3f, 3f, 0f, 1)]);
        // Drain the guard's start output (stats send and held entry) so those cannot pass for the refresh's own entry.
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Game.ProjectilesFor(f.Player).Enqueue(() => { drained.TrySetResult(); return Task.CompletedTask; });
        await drained.Task;
        var packets = new List<byte[]>();
        var outgoing = Program.Outgoing;
        Program.Outgoing = (player, bytes) => { if (ReferenceEquals(player, f.Player)) packets.Add(bytes.ToArray()); };
        try
        {
            await Game.SendCharacterStats(f.Player, PlayerStatChange.Sources);
            Assert.Contains(packets, p => p.SequenceEqual(frozen));
            Assert.True(Game.SingleUseState(f.Player, gem.Effect, gem.Id)!.Held);
        }
        finally { Program.Outgoing = outgoing; }
    }
}
