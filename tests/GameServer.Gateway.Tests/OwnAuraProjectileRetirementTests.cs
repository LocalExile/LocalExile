namespace GameServer.Gateway.Tests;

public partial class OwnAuraGatewayTests
{
    [Fact]
    public async Task Queued_own_aura_refresh_cannot_restore_or_publish_after_the_projectile_owner_retires()
    {
        using var f = new Fixture("Haste");
        CharacterSaves.SetAuraActive(f.Conn.Character!, "Haste", true);
        ushort next = f.Conn.NextBuff;
        uint mana = f.Conn.PlayerMana;
        await f.Gate.WaitAsync();
        Task pending = f.Refresh("SendOwnAuraChanges");
        try
        {
            Assert.False(pending.IsCompleted);
            lock (f.Conn.Monsters) f.Conn.RageRetired = true;
        }
        finally { f.Gate.Release(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(await f.Drain());
        Assert.Empty(f.Conn.Auras);
        Assert.Empty(f.Conn.PresentedAuras);
        Assert.Equal(next, f.Conn.NextBuff);
        Assert.Equal(mana, f.Conn.PlayerMana);
        Assert.Equal(new[] { "Haste" }, CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras);
    }

    [Fact]
    public void Reconciliation_cannot_restore_remembered_auras_on_a_retired_projectile_owner()
    {
        using var f = new Fixture("Haste");
        CharacterSaves.SetAuraActive(f.Conn.Character!, "Haste", true);
        ushort next = f.Conn.NextBuff;
        uint mana = f.Conn.PlayerMana;
        f.Conn.RageRetired = true;

        lock (f.Conn.Monsters) Call("ReconcileOwnAuras", f.Conn, false);

        Assert.Empty(f.Conn.Auras);
        Assert.Empty(f.Conn.PresentedAuras);
        Assert.Equal(next, f.Conn.NextBuff);
        Assert.Equal(mana, f.Conn.PlayerMana);
        Assert.Equal(new[] { "Haste" }, CharacterSaves.Load(f.Conn.Character!)!.ActiveAuras);
    }
}
