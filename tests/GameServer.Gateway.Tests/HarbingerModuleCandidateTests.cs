using GameServer.Core.Protocol.Instance;
using GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

namespace GameServer.Gateway.Tests;

public sealed class HarbingerModuleCandidateTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
    static HarbingerCompanionCatalog.Companion Source(ushort node = 20105, bool focus = false) =>
        new(node, focus ? HarbingerCompanionCatalog.Kind.Focus : HarbingerCompanionCatalog.Kind.Arcane,
            1, 1, 12, 2, 1, new(332, "minion", "minion", "minion", null, MonsterObject.BaseComponents),
            3, focus, new Dictionary<uint, int> { [99] = 100 },
            [new(7, "buff", 1, 700, 4000, 8000, true, 895, "test", 0,
                new Dictionary<uint, int>(), new Dictionary<uint, int> { [100] = 20 })]);

    static HarbingerSession.Frame Frame(bool channel = false) => new(1, true, false, false, true,
        [20105, 18859, 60867], 1, channel ? new(7, 2, 3) : null);

    [Fact]
    public void Module_roster_preserves_recipient_boundaries_cooldowns_and_lease_retirement()
    {
        var roster = new HarbingerRoster();
        var lease = Guid.NewGuid();
        roster.Attach(lease, Now);
        Assert.True(roster.Activate(lease, Source(), 100, Now));
        Assert.Single(Assert.Single(roster.Step(lease, Now.AddSeconds(4), true, false)).Change.Started);
        roster.SetRecipientRate(lease, Now.AddSeconds(5), .5);
        roster.Step(lease, Now.AddSeconds(6), false, false);
        var buff = Assert.Single(roster.Buffs(lease, Now.AddSeconds(6)));
        Assert.Equal(3.2, buff.RemainingBase, 8);
        Assert.Equal(12.4, buff.Buff.EndsAt, 8);
        // Recipient slowing never slows the minion's eight BASE second active interval/cooldown policy.
        Assert.Empty(Assert.Single(roster.Step(lease, Now.AddSeconds(11.9), true, false)).Change.Started);
        Assert.Single(Assert.Single(roster.Step(lease, Now.AddSeconds(12), true, false)).Change.Started);
        roster.Pause(lease, Now.AddSeconds(12), true);
        roster.SetRecipientRate(lease, Now.AddSeconds(20), .25);
        Assert.Equal(12, roster.Time(lease, Now.AddSeconds(30)), 8);
        roster.Release(lease, true, Now.AddSeconds(30));
        var next = Guid.NewGuid();
        roster.Attach(next, Now.AddSeconds(30));
        roster.Clear(lease); // Old-area cleanup cannot retire the new owner.
        roster.SetRecipientRate(lease, Now.AddSeconds(100), 2);
        Assert.Single(roster.Read(next, [20105], Now.AddSeconds(30)));
        roster.Read(next, [], Now.AddSeconds(30));
        Assert.Empty(roster.Buffs(next, Now.AddSeconds(30)));
        Assert.Empty(roster.Step(next, Now.AddSeconds(31), true, true));

        // A late slow rate cannot resurrect an unobserved completion that already expired.
        roster = new(); lease = Guid.NewGuid(); roster.Attach(lease, Now);
        roster.SetRecipientRate(lease, Now, 2);
        roster.Activate(lease, Source(), 100, Now);
        roster.Step(lease, Now.AddSeconds(4), true, false);
        roster.SetRecipientRate(lease, Now.AddSeconds(7), .25);
        Assert.Empty(roster.Buffs(lease, Now.AddSeconds(7)));
        roster = new(); lease = Guid.NewGuid(); roster.Attach(lease, Now);
        roster.Activate(lease, Source(), 100, Now);
        roster.Step(lease, Now.AddSeconds(4), true, false);
        Assert.Equal(3.7, Assert.Single(roster.Buffs(lease, Now.AddSeconds(5))).RemainingBase, 8);
        Assert.Equal(.25, HarbingerRecipientClock.RecipientRate(1, _ => -100));
        Assert.Equal(1, HarbingerRecipientClock.RecipientRate(3, n => n.Contains("only_buff") ? -50 : 0));
        Assert.Equal(1, HarbingerRecipientClock.RecipientRate(2,
            n => n == "buff_time_passed_+%_other_than_temporal_chains" ? -40 : 0, temporalChains: true));
        var catalog = new HarbingerCompanionCatalog();
        Assert.Null(catalog.Error);
        var companions = catalog.All.ToArray();
        Assert.Equal(3, companions.Length);
        Assert.Equal(new ushort[] { 20105, 60867, 18859 }, companions.Select(c => c.Node));
        Assert.Equal(new[] { 12, 13, 14 }, companions.Select(c => c.AlternateMinion));
        Assert.Equal(new[] { 2, 1, 1 }, companions.Select(c => c.Buffs.Count));
        Assert.True(companions.Single(c => c.Type == HarbingerCompanionCatalog.Kind.Focus).RequiresChannelling);
    }

    [Fact]
    public async Task Feature_adapter_requires_paid_current_channel_and_revalidates_native_publication()
    {
        var roster = new HarbingerRoster(); var lease = Guid.NewGuid();
        var session = new HarbingerSession(roster, lease, Now);
        var frame = Frame(); var source = Source(18859, focus: true);
        Assert.False(session.Activate(source, frame, Now, () => false, _ => 99));
        Assert.True(session.Activate(source, frame, Now, () => true, _ => 99));
        var member = Assert.Single(session.Observe(frame, Now, _ => false).Members);
        Assert.True(session.Bind(member.Id, 123));
        Assert.Empty(Assert.Single(session.Observe(frame, Now.AddSeconds(4), _ => true).Changes).Change.Started);
        frame = Frame(channel: true);
        Assert.Empty(Assert.Single(session.Observe(frame, Now.AddSeconds(4), _ => false).Changes).Change.Started);
        Assert.Single(Assert.Single(session.Observe(frame, Now.AddSeconds(4), r => r.Generation == 3).Changes).Change.Started);
        // An already received Focus buff survives ending the paid channel.
        frame = Frame();
        var token = Assert.Single(session.Observe(frame, Now.AddSeconds(5), _ => false).Buffs);
        Assert.Equal(20, session.RecipientStats(frame, Now.AddSeconds(5))[100]);
        byte[]? Packet(HarbingerSession.Frame f, DateTimeOffset at, bool actor = true) =>
            session.BuffPacket(token, 1, f, at, 9, 10, _ => actor, () => [42], () => (f, at));
        Assert.NotNull(Packet(frame, Now.AddSeconds(5)));
        Assert.Null(Packet(frame with { Epoch = 2 }, Now.AddSeconds(5)));
        Assert.Null(Packet(frame with { Paused = true }, Now.AddSeconds(5)));
        Assert.Null(Packet(frame with { Alive = false }, Now.AddSeconds(5)));
        Assert.Null(Packet(frame, Now.AddSeconds(5), actor: false));
        Assert.Null(Packet(frame with { Nodes = [] }, Now.AddSeconds(5)));
        Assert.Equal(new byte[] { 42 }, session.BuffPacket(token, 1, frame, Now.AddSeconds(5), 9, 10,
            _ => true, () => [42], () => (frame, Now.AddSeconds(9))));
        Assert.Null(Packet(frame, Now.AddSeconds(9)));
        Assert.Equal(new uint[] { 123 }, session.Retire(true, Now.AddSeconds(9)));
        Assert.Empty(session.Retire(false, Now.AddSeconds(10)));
        Assert.Null(Packet(frame, Now.AddSeconds(10)));
        Assert.Equal(80, HarbingerDefences.Taken(100, _ => -20));
        Assert.Equal(160u, HarbingerDefences.Maximum(100, 100, 100, 100, 10, 20));
        Assert.Equal(1.1, HarbingerActionSpeed.From(n => n == "action_speed_-%" ? -10 : 0).Factor, 8);
        VerifyCurseReplacementAndSuppression();
        await WhispererRuntimeTests.CombinedHarbingerResourceTransactions();
        await WhispererRuntimeTests.CombinedHarbingerGameplay();
    }

    static void VerifyCurseReplacementAndSuppression()
    {
        var curses = new HarbingerCurses();
        var target = new HarbingerCurses.Target(1, new object(), false, false, false, true);
        var source = new HarbingerTemporalChains.Source(4, -20, -10, -40);
        ushort instance = 1;
        Assert.Single(curses.Apply([target], 7, 0, source, _ => 0, () => instance++, source));
        Assert.NotNull(curses.Self);
        Assert.Equal((-20, -40), curses.SelfStats(1, true, _ => 0));
        Assert.Equal((0, 0), curses.SelfStats(2, true, n => n == "unaffected_by_curses" ? 1 : 0));
        Assert.Equal((-20, -40), curses.SelfStats(3, true, _ => 0));
        Assert.Equal((0, 0), curses.SelfStats(4, true, _ => 0));
        Assert.Null(curses.Active(target, 4)); // Deadline check works before the expiry ticker.
        var immune = target with { Immune = true };
        Assert.Empty(curses.Apply([immune], 7, 5, source, _ => 1, () => instance++, source));
        Assert.DoesNotContain(curses.Reconcile([target], [], null, null, 5, _ => 0, () => instance++),
            c => c.Current is not null);
        var added = Assert.Single(curses.Reconcile([target], [target], 7, source, 5, _ => 0, () => instance++));
        Assert.True(added.Current!.Aura);
        Assert.Empty(curses.Reconcile([target], [target], 7, source, 6, _ => 0, () => instance++));
        var changed = curses.Reconcile([target], [target], 7, source with { NormalSlow = -30 }, 7,
            _ => 0, () => instance++);
        Assert.Equal(2, changed.Length); // Source change removes and replaces, identical snapshots do neither.
        var replacement = target with { Identity = new object() };
        Assert.Empty(curses.Reconcile([replacement], [], null, null, 8, _ => 0, () => instance++));
        Assert.Null(curses.Active(replacement, 8)); // Never send an old removal to a reused native object id.
        curses.Clear();
        Assert.Null(curses.Self);
    }
}
