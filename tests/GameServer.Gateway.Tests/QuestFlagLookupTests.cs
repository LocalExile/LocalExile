namespace GameServer.Gateway.Tests;

public class QuestFlagLookupTests
{
    [Fact]
    public void Individual_lookup_matches_published_flags_after_progress_changes()
    {
        var conn = new ClientState(new(0, "lookup", 100, 100, 0, 0, null, []), 1, 1) { Instance = new() };
        var rows = Quests.ParseRows(File.ReadAllText("data/tables/QuestFlags.json"));
        string own = "a6q6-KilledBrineKing";
        conn.QuestFlags.Add(own);
        conn.QuestFlags.Add("unknown-own-flag");
        conn.UnearnedQuestFlags.UnionWith(Quests.EndgameOnly);
        Check();
        conn.Waypoints.Add("1_1_4_1");
        conn.Visited.Add("2_7_2");
        Check();
        conn.Waypoints.Remove("1_1_4_1");
        conn.Waypoints.Add("1_2_1");
        conn.Visited.Remove("2_7_2");
        conn.Visited.Add("1_1_1");
        conn.Instance.SetState(Quests.InstanceFlag("lookup"));
        Check();
        conn.QuestFlags.Remove(own);
        conn.Waypoints.Clear();
        conn.Visited.Clear();
        Check();
        conn.Use(conn.Layout with { Hash = StashCatalog.DelveHub, Id = WaypointCatalog.DelveHub });
        Check();

        void Check()
        {
            var published = Game.PlayerQuestFlags(conn);
            var ownFlags = conn.QuestFlagsSnapshot();
            foreach (string flag in rows.Keys.Concat(["unknown-own-flag", "unknown-missing-flag", Quests.InstanceFlag("lookup")]))
            {
                bool expected = Quests.IsInstanceFlag(flag) ? conn.Instance.HasState(flag)
                    : Quests.ClientHas(flag, ownFlags, published, rows);
                Assert.Equal(expected, Game.ClientHasQuestFlag(conn, flag));
            }
        }
    }
}
