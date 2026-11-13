using GameServer.Core.Protocol.Instance;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

// Ancestral Tattoo of Bloodlines: 2% increased Recovery Rate of Life, Mana and Energy Shield per tribe with an
// allocated tattoo. The counting pass emits the three recovery-rate stats; this test drives them through the real
// CharacterPlayerStats path and the life, mana and Energy Shield consumers.
public sealed class AncestralBloodlinesTests
{
    [Fact]
    public void Bloodlines_recovery_rates_reach_life_mana_and_energy_shield_consumers()
    {
        var conn = new ClientState(new(1, "bloodlines", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "AncestralBloodlines", PlayerLife = 1000,
            Pools = new(100, 1000, 1000, int.MaxValue, 0),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        // Nine distinct tribes plus the Bloodlines tattoo itself (tribe 11, excluded from the count).
        string[] tribes =
        [
            "Tattoo of the Ngamahu Firewalker", "Tattoo of the Kitava Blood Drinker", "Tattoo of the Tukohama Shaman",
            "Tattoo of the Rongokurai Brute", "Tattoo of the Tasalio Bladedancer", "Tattoo of the Arohongui Moonwarden",
            "Tattoo of the Ramako Archer", "Tattoo of the Hinekora Deathwarden", "Tattoo of the Tawhoa Naturalist",
            "Tattoo of the Valako Stormrider",
        ];
        // The tree does not need these nodes: the counting pass only needs the allocated set and the tattoo map.
        for (int i = 0; i < tribes.Length; i++)
        {
            ushort node = (ushort)(60000 + i);
            conn.Passives.Add(node);
            conn.Tattoos[node] = i < 9 ? tribes[i] : "Ancestral Tattoo of Bloodlines";
        }
        conn.TattooRevision++;
        var stats = Game.CharacterPlayerStats(conn);
        int Get(string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
        Assert.Equal(18, Get("life_recovery_rate_+%"));
        Assert.Equal(18, Get("mana_recovery_rate_+%"));
        Assert.Equal(18, Get("energy_shield_recovery_rate_+%"));
        // Life regeneration scales by the Life recovery rate.
        Assert.Equal(1.18, PanelStats.LifeRegenerationMultiplier(Get), 9);
        // Mana regeneration and Energy Shield recharge scale by their own recovery rate; without each stat the same
        // pool is 1/1.18 of it.
        double mana = PanelStats.ManaPerMinute(Get, 1000);
        int WithoutManaRecovery(string name) => name == "mana_recovery_rate_+%" ? 0 : Get(name);
        Assert.Equal(1.18, mana / PanelStats.ManaPerMinute(WithoutManaRecovery, 1000), 9);
        double recharge = EnergyShield.RechargePerMinute(1000, Get);
        int WithoutEsRecovery(string name) => name == "energy_shield_recovery_rate_+%" ? 0 : Get(name);
        Assert.Equal(1.18, recharge / EnergyShield.RechargePerMinute(1000, WithoutEsRecovery), 9);
    }
}
