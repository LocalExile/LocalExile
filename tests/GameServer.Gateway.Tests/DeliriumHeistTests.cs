using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using GameServer.Modules.Delirium;
using Xunit;

namespace GameServer.Gateway.Tests;

public class DeliriumHeistTests
{
    [Fact]
    public void Delirium_mirror_ruleset_encoding_progress_and_fog_math()
    {
        AfflictionRuleset.LoadGameData();
        Assert.Equal((byte)102, AfflictionRuleset.EncounterRow);
        Assert.Equal((byte)103, AfflictionRuleset.Row);

        var region = new AfflictionRuleset.FogRegion(0, 0, 200, 200, 50, 60, 0f);
        var tile = new AfflictionRuleset.RewardTile(Type: 0, Current: 12, Max: 18, Count: 2);
        byte[] snap = AfflictionRuleset.EncounterSnapshot(
            active: true, ended: false, outerElapsed: 3.5f, innerElapsed: 0f,
            innerDelay: float.PositiveInfinity, pauseTimer: 0f, paused: false,
            mirrorX: 50, mirrorY: 60, mirrorEntityId: 0x42, [region], [tile])!;

        // S->C 0x02c9 header: u16 BE 0x02c9, u8 row (102), compact-uint payload length (75 = 33 + 28 + 1 + 13).
        Assert.Equal(InstanceRulesets.DataId, BinaryPrimitives.ReadUInt16BigEndian(snap.AsSpan(0, 2)));
        Assert.Equal(102, snap[2]);
        Assert.Equal(75, snap[3]);
        Assert.Equal(0, snap[4]); // mode 0
        Assert.Equal(1, snap[5]); // active
        Assert.Equal(0, snap[6]); // ended
        Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(snap.AsSpan(7, 4)));
        Assert.Equal(float.PositiveInfinity, BinaryPrimitives.ReadSingleLittleEndian(snap.AsSpan(15, 4)));
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(snap.AsSpan(24, 4)));
        Assert.Equal(60, BinaryPrimitives.ReadInt32LittleEndian(snap.AsSpan(28, 4)));
        Assert.Equal(0x42u, BinaryPrimitives.ReadUInt32LittleEndian(snap.AsSpan(32, 4)));
        Assert.Equal(1, snap[36]); // 1 region
        Assert.Equal(1, snap[65]); // 1 reward tile
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(snap.AsSpan(75, 4))); // count

        byte[] pause = AfflictionRuleset.EncounterPause(4.5f, paused: true)!;
        Assert.Equal(102, pause[2]);
        Assert.Equal(6, pause[3]);
        Assert.Equal(2, pause[4]); // mode 2
        Assert.Equal(4.5f, BinaryPrimitives.ReadSingleLittleEndian(pause.AsSpan(5, 4)));
        Assert.Equal(1, pause[9]);

        // MirrorState kill progression, reward tier cycling, tile unlock, and fog containment.
        var state = new DeliriumMirrorEncounter.MirrorState
        {
            Stage = DeliriumMirrorEncounter.MirrorStage.Active,
            MirrorX = 50,
            MirrorY = 60,
            OuterElapsed = 5f,
            InnerElapsed = 0f,
            DepthPercent = 40,
            Rewards = [0],
        };
        Assert.True(state.IsCellInFog(70, 60));
        Assert.False(state.IsCellInFog(400, 400));
        for (int i = 0; i < 20; i++) state.RecordKill(MonsterRarity.Rare, progressBonusPct: 0);
        Assert.True(state.RewardCounts[0] >= 3);
        Assert.True(state.Rewards.Length >= 2);
        Assert.True(state.Splinters > 0);
    }

    [Fact]
    public void Heist_module_registers_planning_packets_and_cleans_up_on_unload()
    {
        string moduleDir = Path.GetFullPath("Modules/Core/Content/Heist");
        Assert.True(File.Exists(Path.Combine(moduleDir, "bin", "heist.dll")), $"Expected built Heist module in {moduleDir}");

        // Load Heist (+ its Game dependency) via ModHost and verify its packet subscriptions register cleanly.
        using (var host = new ModHost("Modules", _ => { }, modules: true) { Wanted = () => new HashSet<string>(["Game", "Heist"]) })
        {
            host.Start(watch: false);
            Assert.True(host.IsLoaded("Heist"));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.SignContractId));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.RevealBlueprintId));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.ConfirmBlueprintId));
            Assert.True(GameEvents.WantsPacket(InstanceHeist.SetBlueprintCrewId));
        }
        Assert.False(GameEvents.WantsPacket(InstanceHeist.SignContractId));
        Assert.False(GameEvents.WantsPacket(InstanceHeist.RevealBlueprintId));
        Assert.False(GameEvents.WantsPacket(InstanceHeist.ConfirmBlueprintId));
        Assert.False(GameEvents.WantsPacket(InstanceHeist.SetBlueprintCrewId));

        // Verify client.db Heist tables match the protocol and Stat.* assumptions.
        var npcs = ClientTables.Table("HeistNPCs").Take(InstanceHeist.RogueCount).ToArray();
        Assert.Equal(InstanceHeist.RogueCount, npcs.Length);
        Assert.All(npcs, r => Assert.False(string.IsNullOrEmpty(r.Text("AOFile"))));
        Assert.Equal(3, ClientTables.Table("HeistRevealingNPCs").Count);
        var jobs = ClientTables.Table("HeistJobs");
        Assert.Equal(9, jobs.Count);
        for (int j = 0; j < 9; j++)
            Assert.Equal((int)(Stat.HeistJobLockpickingLevelPlus + j) - 1, jobs[j].IntOrNull("Level_StatsKey"));
    }
}
