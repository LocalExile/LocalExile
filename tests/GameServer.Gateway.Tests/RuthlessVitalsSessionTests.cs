using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public sealed class RuthlessVitalsSessionTests
{
    [Fact]
    public void A_closed_old_connection_cannot_replace_the_current_owners_pools()
    {
        using var f = new Character();
        var old = f.Admit();
        var current = f.Admit();
        Set(current, 10, 7);
        Program.PersistRuthlessVitals(current);
        Set(old, 80, 50);
        old.Closed = true; // The normal disconnect path saves after marking its connection closed.

        Program.PersistRuthlessVitals(old);
        Program.ForgetRuthlessVitalsAdmission(old);

        f.AssertPools(10, 7);
        Assert.Equal(current.RuthlessVitalsSessionId, f.Saved.VitalsOwner!.SessionId);
        Set(current, 9, 6);
        Program.PersistRuthlessVitals(current);
        f.AssertPools(9, 6);
    }

    [Fact]
    public void Successful_transfer_retires_source_writes_before_destination_admission()
    {
        using var f = new Character();
        var source = f.Admit();
        Set(source, 25, 12);
        Assert.True(source.TryBeginAreaChange(new(2, 0, false), out _));
        Program.RetireRuthlessVitalsForTransfer(source);
        Assert.True(f.Saved.VitalsOwner!.Retired);
        Set(source, 90, 70);
        Program.PersistRuthlessVitals(source); // An old area's recovery tick while waiting for the client.
        f.AssertPools(25, 12);

        var destination = f.Admit();
        Assert.Equal((25u, 12u), (destination.PlayerLife, destination.PlayerMana));
        Set(destination, 11, 8);
        Program.PersistRuthlessVitals(destination);
        source.Closed = true;
        source.LeavingArea = false; // Ownership, not this flag alone, also protects a late cleanup callback.
        Program.PersistRuthlessVitals(source);
        Program.ForgetRuthlessVitalsAdmission(source);

        f.AssertPools(11, 8);
        var reconnected = f.Admit();
        Assert.Equal((11u, 8u), (reconnected.PlayerLife, reconnected.PlayerMana));
    }

    [Fact]
    public void Normal_logout_still_saves_its_latest_pools_for_reconnect()
    {
        using var f = new Character();
        var source = f.Admit();
        Set(source, 13, 5);
        source.Closed = true;
        Program.PersistRuthlessVitals(source);
        Program.ForgetRuthlessVitalsAdmission(source);

        var reconnected = f.Admit();
        Assert.Equal((13u, 5u), (reconnected.PlayerLife, reconnected.PlayerMana));
    }

    [Fact]
    public async Task A_refused_transfer_resumes_the_same_owner_and_saves_later_changes()
    {
        using var f = new Character();
        var source = f.Admit();
        Set(source, 25, 12);
        var transfer = new PendingTransfer(2, 0, false);
        Assert.True(source.TryBeginAreaChange(transfer, out _));
        await Program.Transfer("vitals-refusal", null!, null!, source, transfer, _ =>
        {
            Assert.True(f.Saved.VitalsOwner!.Retired);
            f.AssertPools(25, 12);
            Set(source, 27, 14);
            Program.PersistRuthlessVitals(source);
            f.AssertPools(25, 12);
            return Task.FromResult(new ControlResponse { Ok = false, Error = "test refusal" });
        });

        Assert.False(source.LeavingArea);
        Assert.False(f.Saved.VitalsOwner!.Retired);
        Set(source, 26, 13);
        Program.PersistRuthlessVitals(source);
        f.AssertPools(26, 13);
    }

    [Fact]
    public async Task A_refused_transfer_cannot_reclaim_a_newer_admitted_owner()
    {
        using var f = new Character();
        var source = f.Admit();
        var transfer = new PendingTransfer(2, 0, false);
        Assert.True(source.TryBeginAreaChange(transfer, out _));
        ClientState? newer = null;
        await Program.Transfer("vitals-superseded-refusal", null!, null!, source, transfer, _ =>
        {
            newer = f.Admit();
            Set(newer, 10, 7);
            Program.PersistRuthlessVitals(newer);
            return Task.FromResult(new ControlResponse { Ok = false, Error = "test refusal" });
        });

        Assert.False(source.LeavingArea);
        Set(source, 80, 50);
        Program.PersistRuthlessVitals(source);
        f.AssertPools(10, 7);
        Assert.Equal(newer!.RuthlessVitalsSessionId, f.Saved.VitalsOwner!.SessionId);
    }

    [Fact]
    public async Task A_superseded_source_cannot_request_another_destination_ticket()
    {
        using var f = new Character();
        var old = f.Admit();
        var current = f.Admit();
        Set(current, 10, 7);
        Program.PersistRuthlessVitals(current);
        var transfer = new PendingTransfer(2, 0, false);
        Assert.True(old.TryBeginAreaChange(transfer, out _));
        bool called = false;

        await Program.Transfer("vitals-old-ticket", null!, null!, old, transfer, _ =>
        {
            called = true;
            return Task.FromResult(new ControlResponse { Ok = false, Error = "must not reach Auth" });
        });

        Assert.False(called);
        f.AssertPools(10, 7);
        Assert.Equal(current.RuthlessVitalsSessionId, f.Saved.VitalsOwner!.SessionId);
    }

    [Fact]
    public void A_superseded_slow_preparation_cannot_claim_the_newer_owners_pools()
    {
        using var f = new Character();
        var slow = f.Connection();
        Program.RegisterRuthlessVitalsAdmission(slow);
        var current = f.Admit();
        Set(current, 10, 7);
        Program.PersistRuthlessVitals(current);

        Assert.Throws<InvalidOperationException>(() => Program.InitializeCharacterVitals(slow));
        Program.ForgetRuthlessVitalsAdmission(slow);
        f.AssertPools(10, 7);
        Assert.Equal(current.RuthlessVitalsSessionId, f.Saved.VitalsOwner!.SessionId);
    }

    [Fact]
    public void A_refused_preparation_changes_no_saved_pools_and_does_not_block_the_next_admission()
    {
        using var f = new Character();
        var refused = f.Connection();
        string before = f.RawSave;
        Program.RegisterRuthlessVitalsAdmission(refused);
        Program.ForgetRuthlessVitalsAdmission(refused); // PrepareCharacterForArea failed before initialization.
        Assert.Equal(before, f.RawSave);
        var accepted = f.Admit();
        Assert.Equal((40u, 20u), (accepted.PlayerLife, accepted.PlayerMana));
    }

    [Fact]
    public void A_deleted_names_old_connection_cannot_write_the_replacement_characters_save()
    {
        using var f = new Character();
        var old = f.Admit();
        f.Store.DeleteCharacter(f.Id);
        f.Id = Guid.NewGuid();
        f.AddAccountCharacter();
        var replacement = f.Admit();
        Set(replacement, 12, 6);
        Program.PersistRuthlessVitals(replacement);
        Set(old, 80, 50);
        Program.PersistRuthlessVitals(old);

        f.AssertPools(12, 6);
        Assert.Equal(f.Id, f.Saved.VitalsOwner!.CharacterId);
        Assert.Equal(replacement.RuthlessVitalsSessionId, f.Saved.VitalsOwner.SessionId);
    }

    [Fact]
    public void A_new_gateway_session_restores_pools_owned_by_an_earlier_server_session()
    {
        using var f = new Character();
        CharacterSaves.Write(f.Name, f.Saved with { Life = 35, Mana = 17,
            VitalsOwner = new(f.Id, Guid.NewGuid(), Retired: true) });
        var admitted = f.Admit();
        Assert.Equal((35u, 17u), (admitted.PlayerLife, admitted.PlayerMana));
        Assert.Equal(admitted.RuthlessVitalsSessionId, f.Saved.VitalsOwner!.SessionId);
        Assert.False(f.Saved.VitalsOwner.Retired);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("retire")]
    [InlineData("resume")]
    [InlineData("admit")]
    public void Failed_resource_writes_retain_the_prior_owner_and_pools_until_retry(string operation)
    {
        using var f = new Character();
        var owner = f.Admit();
        if (operation == "resume") Program.RetireRuthlessVitalsForTransfer(owner);
        if (operation == "admit")
        {
            owner = f.Connection();
            Program.RegisterRuthlessVitalsAdmission(owner);
        }
        string before = f.RawSave;
        Set(owner, 13, 7);
        void Change()
        {
            if (operation == "save") Program.PersistRuthlessVitals(owner);
            else if (operation == "retire") Program.RetireRuthlessVitalsForTransfer(owner);
            else if (operation == "resume") Program.ResumeRuthlessVitalsAfterFailedTransfer(owner);
            else Program.InitializeCharacterVitals(owner);
        }
        try
        {
            Saves.FailWrite = key => key == f.Name;
            Assert.Throws<IOException>(Change);
        }
        finally { Saves.FailWrite = null; }
        Assert.Equal(before, f.RawSave);
        Change();
        Assert.Equal(owner.RuthlessVitalsSessionId, f.Saved.VitalsOwner!.SessionId);
        Assert.Equal(operation == "retire", f.Saved.VitalsOwner.Retired);
    }

    static void Set(ClientState connection, uint life, uint mana) =>
        (connection.PlayerLife, connection.PlayerMana) = (life, mana);

    sealed class Character : IDisposable
    {
        public string Name { get; } = "VitalsSession" + Guid.NewGuid().ToString("N");
        public Guid Id = Guid.NewGuid();
        public SqliteAccountStore Store { get; }
        readonly List<ClientState> connections = [];
        public CharacterSaves.Save Saved => CharacterSaves.Load(Name)!;
        public string RawSave => Saves.Get(SaveDatabase.Table.Characters, Name)!;

        public Character()
        {
            TestSaves.Ensure();
            Store = new(Saves.Db);
            AddAccountCharacter();
            CharacterSaves.Write(Name, new([], Life: 40, Mana: 20));
        }

        public void AddAccountCharacter()
        {
            var now = DateTimeOffset.UtcNow;
            Store.AddCharacter(new(Id, Guid.NewGuid(), Name, CharacterClass.Witch, 10, "Ruthless", "1_1_2", now, now));
        }

        public ClientState Connection()
        {
            var conn = new ClientState(new(ushort.MaxValue, "VitalsSession", 128, 128, 32, 32, null, []), 0, 1)
            {
                Character = Name, CharacterId = Id, ClassRow = 1, League = "Ruthless", Ruthless = true,
                Pools = new(10, 100, 80, 0, 0), PlayerLife = 100, PlayerMana = 80,
            };
            connections.Add(conn);
            return conn;
        }

        public ClientState Admit()
        {
            var conn = Connection();
            Program.RegisterRuthlessVitalsAdmission(conn);
            Program.InitializeCharacterVitals(conn);
            return conn;
        }

        public void AssertPools(uint life, uint mana) => Assert.Equal((life, mana), (Saved.Life, Saved.Mana));

        public void Dispose()
        {
            foreach (var conn in connections) Program.ForgetRuthlessVitalsAdmission(conn);
            PlayerInventories.Forget(Name);
            Store.DeleteCharacter(Id);
        }
    }
}
