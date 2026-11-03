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
}
