using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Modules.M_commands;

namespace GameServer.Gateway.Tests;

public sealed class CharacterPlaytimeTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();
    static Player PlayerOf(string name, InstanceState? instance = null) => new(new(1, "played", 1000, 1000, 100, 100, null, []), 1, 1)
    {
        Instance = instance ?? new InstanceState(),
        Character = { Name = name },
        Connection = { PlayerSent = true },
        LeagueModule = new(new GameServer.Core.Accounts.League("played-test", "Standard", false, false), new HashSet<string>(["Game", "Commands"]))
    };

    [Fact]
    public void Online_time_survives_checkpoints_area_changes_and_module_reload_without_offline_credit()
    {
        TestEnvironment.OpenOwnSaves("played-time");
        CharacterSaves.Write("Played", new([], Experience: 123));
        double now = 100;
        var player = PlayerOf("Played");
        using var first = new CharacterPlaytime(() => now);
        first.Begin(player); first.Begin(player);
        now += 30.25; first.Step(player);
        Assert.Equal(30.25, first.Seconds(player), 5);
        now += 5.5; first.End(player); first.End(player);
        Assert.Equal("35.75", CharacterSaves.ModuleData("Played", CharacterPlaytime.SaveKey));
        now += 10000;
        var next = PlayerOf("Played"); first.Begin(next);
        Assert.Equal(35.75, first.Seconds(next), 5);
        now += 4.25; first.Dispose();
        Assert.Equal("40", CharacterSaves.ModuleData("Played", CharacterPlaytime.SaveKey));
        now += 5000;
        using var replacement = new CharacterPlaytime(() => now);
        replacement.Begin(next); now += 2;
        Assert.Equal(42, replacement.Seconds(next));
        first.Begin(next); first.Step(next); first.Dispose();
        replacement.End(next);
        Assert.Equal("42", CharacterSaves.ModuleData("Played", CharacterPlaytime.SaveKey));
        Assert.Equal(123u, CharacterSaves.Experience("Played"));
        var bot = PlayerOf("Bot"); bot.Controlled = true;
        replacement.Begin(bot); now += 60; replacement.End(bot);
        Assert.Null(CharacterSaves.ModuleData("Bot", CharacterPlaytime.SaveKey));
    }

    [Fact]
    public async Task Native_played_command_is_handled_and_unrelated_generic_commands_fall_through()
    {
        TestEnvironment.OpenOwnSaves("played-command");
        CharacterSaves.Write("PlayedCommand", new([]));
        CharacterSaves.ChangeModuleData("PlayedCommand", CharacterPlaytime.SaveKey, _ => "93784");
        var player = PlayerOf("PlayedCommand");
        var replies = new List<string>();
        player.Commands = new() { Send = (_, _) => Task.CompletedTask, Reply = text => { replies.Add(text); return Task.CompletedTask; } };
        using var context = new Context();
        try
        {
            new CommandsModule().Load(context);
            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, [0, 0x16, 2, 0, 0]));
            Assert.Equal("You have played for 1 day, 2 hours, 3 minutes and 4 seconds.", Assert.Single(replies));
            Assert.False(await GameEvents.AnyPacket(player, InstanceItems.CommandId, [0, 0x16, 9, 0, 0]));
            var commands = player.Commands;
            foreach (var build in GameEvents.CommandsBuilding.For(player)) commands = build(commands, player);
            player.Commands = commands;
            Assert.True(await commands.RunAsync("/played"));
            Assert.Equal(2, replies.Count);
        }
        finally { GameEventOwners.Release("Commands", GameEventOwners.ModuleRank); }
    }

    [Fact]
    public async Task Additional_native_commands_and_chat_presence_work_with_lockstep_probe_guard()
    {
        TestEnvironment.OpenOwnSaves("native-commands-extra");
        var store = new GameServer.Storage.SqliteAccountStore(Saves.Db);
        var account = new Account(Guid.NewGuid(), "cmd-acct", "h", "s", DateTimeOffset.UtcNow);
        store.AddAccount(account);
        var created = DateTimeOffset.UtcNow.AddDays(-2).AddHours(-3);
        store.AddCharacter(new GameServer.Core.Accounts.Character(Guid.NewGuid(), account.Id, "Hero", CharacterClass.Ranger, 85, "Standard", "1_1_1", created, DateTimeOffset.UtcNow));
        store.AddCharacter(new GameServer.Core.Accounts.Character(Guid.NewGuid(), account.Id, "TopExile", CharacterClass.Witch, 100, "Standard", "1_1_1", created, DateTimeOffset.UtcNow));
        store.AddCharacter(new GameServer.Core.Accounts.Character(Guid.NewGuid(), account.Id, "FallenHC", CharacterClass.Marauder, 92, "Standard", "1_1_1", created, DateTimeOffset.UtcNow,
            HardcoreDeath: new HardcoreDeathRecord("Hardcore", created.AddHours(1), false)));
        store.AddCharacter(new GameServer.Core.Accounts.Character(Guid.NewGuid(), account.Id, "AliveHC", CharacterClass.Duelist, 90, "Hardcore", "1_1_1", created, DateTimeOffset.UtcNow));
        CharacterSaves.Write("Hero", new([], Experience: 1234567));
        CharacterSaves.Write("TopExile", new([], Experience: 4250334444u));
        CharacterSaves.Write("FallenHC", new([], Experience: 2200000000u));
        CharacterSaves.Write("AliveHC", new([], Experience: 1900000000u));

        var player = PlayerOf("Hero");
        player.Character.ClassRow = 2;
        player.Character.League = "Standard";
        player.Character.AtlasPassivePoints = 10;
        player.Pools = player.Pools with { Level = 85 };
        var replies = new List<string>();
        player.Commands = new() { Send = (_, _) => Task.CompletedTask, Reply = text => { replies.Add(text); return Task.CompletedTask; } };
        using var context = new Context();
        try
        {
            new CommandsModule().Load(context);
            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, [0, 0x16, InstanceItems.AgeCommand, 0, 0]));
            Assert.Contains("Your character was created 2 days, 3 hours,", replies[^1]);

            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, [0, 0x16, InstanceItems.KillsCommand, 0, 0]));
            Assert.Equal("You have killed 0 monsters.", replies[^1]);

            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, [0, 0x16, InstanceItems.ItemLevelCommand, 0, 0]));
            Assert.Equal("There is no item on the cursor.", replies[^1]);

            var inv = Game.ItemsOf(player);
            var noMods = inv.Add(PlayerInventories.Cursor, 0, 0, new Item(1, ["Base", "Stack"], 0) { ItemLevel = 50 });
            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, [0, 0x16, InstanceItems.ItemLevelCommand, 0, 0]));
            Assert.Equal("Item on cursor doesn't have a level (it needs to be one that can get mods).", replies[^1]);
            inv.Remove(PlayerInventories.Cursor, noMods.Id);
            var withMods = inv.Add(PlayerInventories.Cursor, 0, 0, new Item(2, ["Base", "Mods"], 0) { ItemLevel = 73 });
            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, [0, 0x16, InstanceItems.ItemLevelCommand, 0, 0]));
            Assert.Equal("Item on cursor is level 73.", replies[^1]);
            inv.Remove(PlayerInventories.Cursor, withMods.Id);

            byte[] whoisTop = new GameServer.Core.Protocol.Login.LoginWriter().U16(InstanceItems.CommandId).U8(InstanceItems.WhoisCommand).WStr("TopExile").ToArray();
            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, whoisTop));
            Assert.Equal("TopExile is a level 100 Witch in the Standard league who is not currently online.", replies[^1]);

            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, [0, 0x16, InstanceItems.LadderCommand, 0, 0]));
            Assert.Contains("1. TopExile Level 100 Witch", replies[^1]);
            Assert.Contains("2. FallenHC Level 92 Marauder", replies[^1]);
            Assert.DoesNotContain("(Dead)", replies[^1]);
            Assert.Contains("3. Hero Level 85 Ranger", replies[^1]);

            player.Character.League = "Hardcore";
            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, [0, 0x16, InstanceItems.LadderCommand, 0, 0]));
            Assert.Contains("1. FallenHC (Dead) Level 92 Marauder", replies[^1]);
            Assert.Contains("2. AliveHC Level 90 Duelist", replies[^1]);
            player.Character.League = "Standard";

            var commands = player.Commands;
            foreach (var build in GameEvents.CommandsBuilding.For(player)) commands = build(commands, player);
            Assert.True(await commands.RunAsync("/atlaspassives"));
            Assert.Contains("0/10 Atlas passive skill point(s) allocated", replies[^1]);

            // Non-empty lockstep AFK probe replies must pass through to Messages.cs when a probe is in flight.
            player.Connection.LockstepClient = true;
            player.Connection.LockstepProbeSentAt = 12345;
            byte[] probeAfk = new GameServer.Core.Protocol.Login.LoginWriter().U16(InstanceItems.CommandId).U8(InstanceItems.AfkCommand).WStr("This player is AFK.").ToArray();
            Assert.False(await GameEvents.AnyPacket(player, InstanceItems.CommandId, probeAfk));

            // Independent /autoreply survives AFK auto-cancel on gameplay action packets.
            player.Connection.LockstepProbeSentAt = 0;
            byte[] setAuto = new GameServer.Core.Protocol.Login.LoginWriter().U16(InstanceItems.CommandId).U8(InstanceItems.AutoReplyCommand).WStr("In map").ToArray();
            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, setAuto));
            Assert.Equal("Autoreply set \"In map\"", replies[^1]);
            Assert.True(await GameEvents.AnyPacket(player, InstanceItems.CommandId, probeAfk));
            Assert.Equal("AFK mode is now ON. Autoreply \"This player is AFK.\"", replies[^1]);
            await GameEvents.RunMessagesHandled(player, [InstanceTransfer.UseSkillOnObjectId]);
            var avail = GameEvents.GetChatAvailability(player);
            Assert.True(avail.Afk);
            Assert.Equal("In map", avail.AfkMessage);
        }
        finally
        {
            await GameEvents.RunPlayerLeaving(player, "disconnected");
            GameEventOwners.Release("Commands", GameEventOwners.ModuleRank);
        }
    }

    [Fact]
    public async Task Level_up_broadcasts_character_name_class_and_level_to_all_players_in_area()
    {
        using var runtime = new PartyFollowerTests.Runtime();
        using var wire = new PartyFollowerTests.Wire();
        var sameArea = new InstanceState();
        var otherArea = new InstanceState();

        var leveler = runtime.PlayerOf(sameArea, wire, "Aria");
        leveler.Controlled = false;
        leveler.Character.ClassRow = SkillCatalog.WitchRow;
        leveler.Pools = leveler.Pools with { Level = 2, Life = 50, Mana = 50 };

        var teammate = runtime.PlayerOf(sameArea, wire, "Bram");
        teammate.Controlled = false;
        var outsider = runtime.PlayerOf(otherArea, wire, "Cora");
        outsider.Controlled = false;

        var levelerLines = new List<string>();
        var teammateLines = new List<string>();
        var outsiderLines = new List<string>();
        static string ReadLine(byte[] packet)
        {
            var r = new GameServer.Core.Protocol.Login.LoginReader(packet.AsSpan(2));
            _ = r.WStr(); // sender
            return r.WStr();
        }
        leveler.Connection.Outbox = (b, _) => { if (b.Length >= 2 && b[0] == 0 && b[1] == InstanceChat.ChatLineId) levelerLines.Add(ReadLine(b)); return Task.CompletedTask; };
        teammate.Connection.Outbox = (b, _) => { if (b.Length >= 2 && b[0] == 0 && b[1] == InstanceChat.ChatLineId) teammateLines.Add(ReadLine(b)); return Task.CompletedTask; };
        outsider.Connection.Outbox = (b, _) => { if (b.Length >= 2 && b[0] == 0 && b[1] == InstanceChat.ChatLineId) outsiderLines.Add(ReadLine(b)); return Task.CompletedTask; };

        Program.InGame[leveler] = 0;
        Program.InGame[teammate] = 0;
        Program.InGame[outsider] = 0;
        try
        {
            await Game.PublishLevelChange(leveler, before: 1, level: 2);
            Assert.Contains("Aria (Witch) is now level 2", levelerLines);
            Assert.Contains("Aria (Witch) is now level 2", teammateLines);
            Assert.Empty(outsiderLines);

            // Any published level change announces the new level to the area.
            levelerLines.Clear();
            teammateLines.Clear();
            await Game.PublishLevelChange(leveler, before: 2, level: 1);
            Assert.Contains("Aria (Witch) is now level 1", levelerLines);
            Assert.Contains("Aria (Witch) is now level 1", teammateLines);
            Assert.Empty(outsiderLines);
        }
        finally
        {
            Program.InGame.TryRemove(leveler, out _);
            Program.InGame.TryRemove(teammate, out _);
            Program.InGame.TryRemove(outsider, out _);
        }
    }

    sealed class Context : IModuleContext, IDisposable
    {
        readonly ModuleLifetime lifetime = new("Commands");
        public ModManifest Manifest => new("Commands", "1.0.0", ModKind.Feature, ["Game"], "commands.dll");
        public IModuleLifetime Lifetime => lifetime;
        public string Folder => "";
        public void Log(string message) { }
        public T Settings<T>() => throw new NotSupportedException();
        public void SaveSettings<T>(T settings) => throw new NotSupportedException();
        public void ProvideLeagueRules(Func<LeagueRules> rules) { }
        public void Dispose() => lifetime.Stop(e => throw e);
    }
}
