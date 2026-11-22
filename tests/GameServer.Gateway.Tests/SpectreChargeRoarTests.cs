namespace GameServer.Gateway.Tests;

public sealed class SpectreChargeRoarTests
{
    [Theory]
    [InlineData("MonkeyChiefBloodParasite", 1607, ChargeType.Power)]
    [InlineData("MonkeyChiefBloodEnrage", 1598, ChargeType.Frenzy)]
    public async Task Chieftain_uses_roar_at_enemies_and_grants_timed_charges_to_nearby_minions(string variety, int effect, ChargeType type)
    {
        using var f = new MinionUtilityTests.Fixture();
        f.Equip(("Raise Spectre", 20));
        ushort skill = Game.GemSkillEffects(f.Conn).Single().Id;
        var definition = Game.SummonData.Find("RaiseSpectre")!;
        const string path = "Metadata/Monsters/BloodChieftain/";
        uint spectre = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, skill, definition, (100, 100),
            restoredVariety: path + variety);
        uint other = await Game.SummonFighter("test", f.Stream, f.Ciphers, f.Conn, skill, definition, (185, 100),
            restoredVariety: path + variety);
        var caster = f.Conn.Summons.Fighters[spectre];
        var distant = f.Conn.Summons.Fighters[other];
        f.Conn.Walk.Teleport(100, 100, 0);
        f.Conn.Monsters[920] = new("enemy", new(103, 100, 0), 100, 3, 1000) { Hostile = true };

        Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, caster, 1);
        var cast = Assert.IsType<GolemState.Action>(caster.Pending);
        Assert.Equal(effect, cast.Ability.Effect);
        Assert.Equal(6, cast.Ability.Cooldown);
        var melee = caster.Abilities.Single(a => a.Id == "MeleeAtAnimationSpeed");
        Game.GolemAbilityContactLocked("test", f.Stream, f.Ciphers, f.Conn, caster, cast, 2);
        Assert.Equal(1, caster.CombatCharges!.Held(type));
        Assert.Null(distant.CombatCharges);
        Assert.Contains(f.Conn.Minions.OutputQueue, output => output.WorldId == caster.Id && output.Packet.Length > 0);
        var attack = Game.GolemAbilities(f.Conn, caster.Monster.Variety!, caster.Monster.Level, caster, caster.Player, 2)
            .Single(a => a.Id == melee.Id);
        if (type == ChargeType.Frenzy)
        {
            Assert.Equal(melee.Attack.Damage.MaxTotal * 1.04, attack.Attack.Damage.MaxTotal, 6);
            Assert.Equal(melee.Attack.Seconds / 1.04, attack.Attack.Seconds, 6);
        }
        else
        {
            var stats = Game.GolemModifiers(f.Conn, caster, caster.Player, 2);
            Assert.Equal(caster.Monster.Mods.Effects["critical_strike_chance_+%"] + 50, stats.Effects["critical_strike_chance_+%"]);
            Assert.Equal(500, Game.GolemCriticalChance(melee, caster.Monster.Mods));
            Assert.Equal(750, Game.GolemCriticalChance(attack, stats));
        }

        Game.GolemAbilityContactLocked("test", f.Stream, f.Ciphers, f.Conn, caster, cast, 8);
        Assert.Equal(2, caster.CombatCharges.Held(type));
        Assert.Equal(18, caster.CombatCharges.ExpiresAt(type));
        Game.GolemStepLocked("test", f.Stream, f.Ciphers, f.Conn, caster, 18);
        Assert.Null(caster.CombatCharges);
    }
}
