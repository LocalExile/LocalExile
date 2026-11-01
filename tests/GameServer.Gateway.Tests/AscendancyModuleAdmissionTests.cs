using GameServer.Core.Accounts;
using GameServer.Storage;

public sealed class AscendancyModuleAdmissionTests
{
    [Fact]
    public void Optional_family_requires_its_live_league_owner_and_preserves_progress_across_retirement()
    {
        using var save = new SaveFixture();
        var identity = new AscendancyIdentity(AscendancyFamily.Phrecia, "Gladiator", "3.29.3b");
        var normal = identity with { Family = AscendancyFamily.Normal };
        var progress = new AscendancyProgress(identity, [19], ["normal"]);
        var admitted = Connection("admitted", "PhreciaAscendancies");
        admitted.MainAscendancy = progress;
        const string original = "{\"MainAscendancy\":{\"Selection\":{\"Family\":\"Phrecia\",\"Id\":\"Gladiator\",\"Revision\":\"3.29.3b\",\"future\":7},\"Nodes\":[19],\"Awards\":[\"normal\"]},\"Gold\":83,\"future\":{\"keep\":true}}";
        Saves.Put(SaveDatabase.Table.Characters, "admitted", original);
        Assert.Throws<AscendancyRuleException>(() => AscendancyPolicies.RequirePlayable(admitted, identity));
        Assert.True(AscendancyPolicies.Available(admitted, normal));
        Assert.Equal(original, Saves.Get(SaveDatabase.Table.Characters, "admitted"));

        // A stale ordinary area's admission cannot authorize an award to the latest durable optional selection.
        Assert.Throws<PassiveProgressConflictException>(() => CharacterSaves.ChangeMainAscendancy("admitted", latest =>
        {
            AscendancyPolicies.RequireCurrentSelection(normal, latest.Selection);
            return latest with { Awards = [.. latest.EarnedAwards, "cruel"] };
        }));
        Assert.Equal(original, Saves.Get(SaveDatabase.Table.Characters, "admitted"));
        AscendancyPolicies.RequireCurrentSelection(identity with { Unknown = [] }, identity);

        var owner = new ModuleLifetime("PhreciaAscendancies");
        var policy = new Policy(identity);
        using var registration = AscendancyPolicies.Register(owner, policy);
        var excluded = Connection("excluded");
        Assert.False(AscendancyPolicies.Available(excluded, identity));
        Assert.Throws<AscendancyRuleException>(() => AscendancyPolicies.RequirePlayable(excluded, identity));
        Assert.Throws<AscendancyRuleException>(() => AscendancyPolicies.RequirePlayable(admitted, identity with { Revision = "future" }));
        policy.RejectGraph = true;
        Assert.Throws<AscendancyRuleException>(() => AscendancyPolicies.RequirePlayable(admitted, identity));
        Assert.Equal(original, Saves.Get(SaveDatabase.Table.Characters, "admitted"));
        policy.RejectGraph = false;
        AscendancyPolicies.RequirePlayable(admitted, identity);
        AscendancyPolicies.SelectionChanged(admitted);
        Assert.Equal(1, AscendancyPolicies.AreaStats(admitted, identity)![21146]);
        var node = new AscendancyCatalog.Node(19, "source", "source", [], false, 0, false, false, [(123u, 7)]);
        var definition = new AscendancyCatalog.Definition(identity, "test", 1, 0, 1, 0,
            new Dictionary<ushort, AscendancyCatalog.Node> { [19] = node });
        Assert.Equal(node.Stats, AscendancyPolicies.NodeStats(admitted, definition, 19, node.Stats));
        // The source resolver supplies the current data mode; an ordinary HardMode node must not use catalog normal stats.
        IReadOnlyList<(uint Key, int Value)> hardMode = [(456u, 3)];
        Assert.Same(hardMode, AscendancyPolicies.NodeStats(excluded, definition with { Identity = normal }, 19, hardMode));
        Assert.Same(hardMode, AscendancyPolicies.NodeStats(admitted, definition, 19, hardMode));
        Saves.Atomically(() => AscendancyPolicies.RequireAvailable(admitted, identity));

        // Merely validating a normal candidate must not detach a failed/abandoned selection change.
        AscendancyPolicies.RequirePlayable(admitted, normal);
        var failedChoice = Connection("failed-optional-choice", "PhreciaAscendancies");
        failedChoice.MainAscendancy = new(normal, [41], ["normal"]);
        // A verified candidate can still fail later selection rules or the save. No runtime choice was committed.
        AscendancyPolicies.RequirePlayable(failedChoice, identity);
        using (AscendancyPolicies.Enter(failedChoice, identity)) { }
        var changedToNormal = Connection("normal-control", "PhreciaAscendancies");
        changedToNormal.MainAscendancy = progress;
        AscendancyPolicies.RequirePlayable(changedToNormal, identity);
        AscendancyPolicies.SelectionChanged(changedToNormal);
        changedToNormal.MainAscendancy = new(normal);
        AscendancyPolicies.SelectionChanged(changedToNormal);
        owner.Stop(ex => throw ex);
        Assert.True(admitted.Closed);
        Assert.False(excluded.Closed);
        Assert.False(changedToNormal.Closed);
        Assert.False(failedChoice.Closed);
        Assert.Equal(normal, failedChoice.MainAscendancy.Selection);
        Assert.False(AscendancyPolicies.Available(admitted, identity));
        Assert.Throws<AscendancyRuleException>(() => AscendancyPolicies.AreaStats(admitted, identity));
        Assert.Equal(progress, admitted.MainAscendancy);
        Assert.Equal(original, Saves.Get(SaveDatabase.Table.Characters, "admitted"));

        var replacement = new ModuleLifetime("PhreciaAscendancies");
        using var current = AscendancyPolicies.Register(replacement, new Policy(identity));
        var reentered = Connection("reentered", "PhreciaAscendancies");
        reentered.MainAscendancy = progress;
        AscendancyPolicies.RequirePlayable(reentered, identity);
        AscendancyPolicies.SelectionChanged(reentered);
        registration.Dispose(); // An old handle cannot unregister or close the replacement generation.
        Assert.True(AscendancyPolicies.Available(reentered, identity));
        Assert.False(reentered.Closed);
        Assert.Null(AscendancyPolicies.AreaStats(changedToNormal, normal));
        Assert.Equal(original, Saves.Get(SaveDatabase.Table.Characters, "admitted"));
        replacement.Stop(ex => throw ex);
    }

    static ClientState Connection(string name, params string[] modules) => new(new(1, "test", 1, 1, 0, 0, null, []), 1, 1)
    {
        Character = name,
        LeagueModule = new(new League("test", "Standard", false, false), modules.ToHashSet(StringComparer.OrdinalIgnoreCase)),
    };

    sealed class Policy(AscendancyIdentity supported) : IAscendancyFamilyPolicy
    {
        public bool RejectGraph;
        public AscendancyFamily Family => AscendancyFamily.Phrecia;
        public bool Supports(AscendancyIdentity identity) => identity == supported;
        public void RequirePlayable(AscendancyIdentity identity)
        {
            if (RejectGraph) throw new AscendancyRuleException("Unverified graph");
        }
        public IReadOnlyDictionary<uint, int>? AreaStats => new Dictionary<uint, int> { [21146] = 1 };
        public IReadOnlyList<(uint Key, int Value)> NodeStats(AscendancyCatalog.Definition definition, ushort node,
            IReadOnlyList<(uint Key, int Value)> modeStats) => modeStats;
    }

    sealed class SaveFixture : IDisposable
    {
        readonly string _path = Path.Combine(Path.GetTempPath(), $"localexile-phrecia-{Guid.NewGuid():N}.db");
        public SaveFixture() => Saves.Open(_path);
        public void Dispose()
        {
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        }
    }
}
