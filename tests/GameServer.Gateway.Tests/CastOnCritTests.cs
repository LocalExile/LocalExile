using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class CastOnCritTests
{
    [Fact]
    public void Cast_on_crit_cycles_linked_spells_and_skips_ones_on_cooldown()
    {
        var state = new CastOnCritState();
        (uint, int) link = (7, 0);
        (int, double)[] spells = [(10, 0.15), (20, 0.15)];

        // Successive crits cycle through the link's spells in socket order.
        Assert.Equal(0, state.Pick(link, spells, 0));
        Assert.Equal(1, state.Pick(link, spells, 0.01));
        // Both on cooldown: nothing triggers.
        Assert.Null(state.Pick(link, spells, 0.05));
        // Each spell's own cooldown: the first is ready again at 0.15 s, the second at 0.16 s.
        Assert.Equal(0, state.Pick(link, spells, 0.155));
        Assert.Equal(1, state.Pick(link, spells, 0.2));
        // Copies of one spell share its cooldown, also across another link.
        Assert.Null(state.Pick((8, 1), [(20, 0.15)], 0.25));

        // Two copies in one link: the second copy is skipped while the first cools, and the rotation goes on.
        var copies = new CastOnCritState();
        (int, double)[] withCopies = [(10, 0.15), (10, 0.15), (20, 0.15)];
        Assert.Equal(0, copies.Pick(link, withCopies, 0));
        Assert.Equal(2, copies.Pick(link, withCopies, 0.01));
        Assert.Null(copies.Pick(link, withCopies, 0.02));
        Assert.Equal(0, copies.Pick(link, withCopies, 0.16));

        // Recovery rate: 100% increased halves the 150 ms; -100% never recovers.
        Assert.Equal(0.075, CastOnCritState.CooldownSeconds(150, 100), 9);
        Assert.Equal(double.PositiveInfinity, CastOnCritState.CooldownSeconds(150, -100));
    }

    [Fact]
    public void Cast_on_crit_supports_the_attack_and_the_spell_with_the_data_values()
    {
        var skills = Program.Skills;
        GemSupports.Candidate[] gem =
        [
            new(skills.EffectRow("SupportCastOnCriticalStrike"), 1, 0, 1),
            new(skills.EffectRow("SupportCastOnCritTriggered"), 1, 0, 1),
        ];
        static int Stat(IReadOnlyList<GemSupports.Applied> supports, string name) =>
            Program.Stats.Key(name) is uint key ? supports.Sum(s => s.Stats.Where(v => v.Key == key).Sum(v => v.Value)) : 0;

        var onAttack = Game.Supports.Resolve(skills.EffectRow("DoubleStrike"), gem, skills);
        Assert.Equal(100, Stat(onAttack, CastOnCritState.AttackStat));
        Assert.Equal(0, Stat(onAttack, CastOnCritState.SpellStat));

        var onSpell = Game.Supports.Resolve(skills.EffectRow("Fireball"), gem, skills);
        Assert.Equal(0, Stat(onSpell, CastOnCritState.AttackStat));
        Assert.Equal(1, Stat(onSpell, CastOnCritState.SpellStat));
        Assert.Equal(1, Stat(onSpell, "spell_uncastable_if_triggerable"));
        Assert.Equal(-19, Stat(onSpell, "support_cast_on_crit_spell_damage_+%_final"));
        var trigger = onSpell.Single(s => s.Effect.Id == "SupportCastOnCritTriggered");
        Assert.Equal(150, skills.CooldownMs(trigger.Effect.Row, trigger.Gem.Level));

        // The less spell damage reaches a spell's hit, not an attack's.
        var lessSpell = new[] { ("support_cast_on_crit_spell_damage_+%_final", -19) };
        Assert.Equal(0.81, GemSupportDamage.Multipliers(DamageTypeHistory.Fire, lessSpell, new HashSet<int> { 1 }).GeneralOrPhysical, 9);
        Assert.Equal(1, GemSupportDamage.Multipliers(DamageTypeHistory.Fire, lessSpell, new HashSet<int> { 0 }).GeneralOrPhysical, 9);

        // A bow with Tornado Shot, Cast On Critical Strike, two Fireballs and Ice Nova linked: the candidate list keeps
        // both Fireball copies in socket order, and they share one cooldown in the real pick path.
        string path = Path.Combine(Path.GetTempPath(), $"localexile-castoncrit-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "castoncrit", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "CastOnCritChar", PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            foreach (string name in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                "virtual_intelligence_for_requirements" }) conn.PlayerStats[Program.Stats.Key(name)!.Value] = 1000;
            Item Gem(string id) => ItemCatalog.Create(Program.Items.Find($"Metadata/Items/Gems/{id}")!);
            var bow = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!);
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, bow with
            {
                Sockets =
                [
                    new ItemSocket(SocketColour.White, Gem("SkillGemTornadoShot")),
                    new ItemSocket(SocketColour.White, Gem("SupportGemCastOnCrit")),
                    new ItemSocket(SocketColour.White, Gem("SkillGemFireball")),
                    new ItemSocket(SocketColour.White, Gem("SkillGemFireball")),
                    new ItemSocket(SocketColour.White, Gem("SkillGemIceNova")),
                ],
                Links = [5],
            });
            var attack = Game.GemSkillEffects(conn).Single(g => g.Name == "Tornado Shot");
            var spells = Game.CastOnCritSpells(conn, attack.Id);
            Assert.Equal(["Fireball", "Fireball", "Ice Nova"], spells.Select(s => s.Gem.Name));
            Assert.Equal(spells[0].Gem.Effect, spells[1].Gem.Effect);
            Assert.NotEqual(spells[0].Gem.Id, spells[1].Gem.Id);
            Assert.All(spells, s => Assert.Equal(0.15, s.Cooldown, 9));
            Assert.Equal(spells[0].Gem.Id, Game.PickCastOnCritSpellLocked(conn, attack.Id, spells, 0)?.Gem.Id);
            // The second Fireball shares the first's cooldown, so Ice Nova is next.
            Assert.Equal("Ice Nova", Game.PickCastOnCritSpellLocked(conn, attack.Id, spells, 0.01)?.Gem.Name);
            Assert.Null(Game.PickCastOnCritSpellLocked(conn, attack.Id, spells, 0.02));
            Assert.Equal(spells[0].Gem.Id, Game.PickCastOnCritSpellLocked(conn, attack.Id, spells, 0.16)?.Gem.Id);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
