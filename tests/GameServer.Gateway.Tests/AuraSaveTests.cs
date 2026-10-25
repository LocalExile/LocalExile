namespace GameServer.Gateway.Tests;

public class AuraSaveTests
{
    public AuraSaveTests() => TestSaves.Ensure();

    [Fact]
    public void Aura_toggles_persist_by_effect_without_losing_other_character_data()
    {
        string character = "Auras" + Guid.NewGuid().ToString("N");
        CharacterSaves.Write(character, new CharacterSaves.Save([], SkillBar: new() { [0] = "Grace" }, Experience: 12345));
        CharacterSaves.SetAuraActive(character, "Grace", true);
        CharacterSaves.SetAuraActive(character, "Determination", true);
        CharacterSaves.SetAuraActive(character, "Grace", true);
        var saved = CharacterSaves.Load(character)!;
        Assert.Equal(new[] { "Determination", "Grace" }, saved.ActiveAuras);
        Assert.Equal(12345u, saved.Experience);
        Assert.Equal("Grace", saved.SkillBar![0]);
        CharacterSaves.SetAuraActive(character, "Grace", false);
        Assert.Equal(new[] { "Determination" }, CharacterSaves.Load(character)!.ActiveAuras);
        CharacterSaves.SetAuraActive(character, "Determination", false);
        Assert.Empty(CharacterSaves.Load(character)!.ActiveAuras!);
        Assert.Null(CharacterSaves.Load(character + "other"));
    }

    [Fact]
    public void Restoring_remembers_all_enabled_auras_despite_transient_mana_and_rebuilds_runtime_ids()
    {
        var pools = new CharacterStats.Pools(1, 50, 100, 2, 0);
        var grace = new AuraCatalog.Aura(0x201, 0, "Grace", "Grace", 10, 143, 0, 5000, SourceBuff: 11);
        var determination = grace with { SkillId = 0x202, EffectId = "Determination", Buff = 12, SourceBuff = 13 };
        var anger = grace with { SkillId = 0x203, EffectId = "Anger", Buff = 14, SourceBuff = 15 };
        var restored = AuraCatalog.Restore(["Missing", "Grace", "Grace", "Determination", "Anger"],
            [grace, determination, anger], pools);
        Assert.Equal(new[] { grace, determination, anger }, restored);
        Assert.Empty(AuraCatalog.Restore(["Grace"], [], pools));
        Assert.Empty(AuraCatalog.Restore([], [grace], pools));
        Assert.Empty(AuraCatalog.Restore(["Grace", "Determination", "Anger"], [grace, determination, anger], pools, inTown: true));
        Assert.Equal(100, AuraCatalog.ReservedMana(pools, restored));
        Assert.Equal(6, restored.Sum(a => AuraCatalog.StartPackets(a, 0x1000, 1).Count()));
        Assert.Equal(150, AuraCatalog.RequiredMana(pools, restored));
    }
    [Fact]
    public void Town_suppression_character_switch_and_death_do_not_mix_character_intent()
    {
        string first = "AuraFirst" + Guid.NewGuid().ToString("N"), second = "AuraSecond" + Guid.NewGuid().ToString("N");
        foreach (string aura in new[] { "Vitality", "Grace", "Determination", "Anger", "Hatred", "Clarity", "Precision" })
            CharacterSaves.SetAuraActive(first, aura, true);
        CharacterSaves.SetAuraActive(second, "Discipline", true);
        var saved = CharacterSaves.Load(first)!.ActiveAuras!.ToArray();
        var available = saved.Select((id, i) => new AuraCatalog.Aura((ushort)(0x200 + i), 0, id, id,
            (ushort)(1 + i * 2), AuraCatalog.BuffRow(id), 0, 5000)).ToArray();
        var pools = new CharacterStats.Pools(1, 50, 100, 2, 0);
        Assert.Empty(AuraCatalog.Restore(saved, available, pools, inTown: true));
        Assert.Equal(saved, CharacterSaves.Load(first)!.ActiveAuras);
        Assert.Equal(saved.Length, AuraCatalog.Restore(CharacterSaves.Load(first)!.ActiveAuras!, available, pools).Count);
        CharacterSaves.ClearActiveAuras(first);
        Assert.Empty(CharacterSaves.Load(first)!.ActiveAuras!);
        Assert.Equal(new[] { "Discipline" }, CharacterSaves.Load(second)!.ActiveAuras);
        Assert.Empty(AuraCatalog.Restore(CharacterSaves.Load(first)!.ActiveAuras!, available, pools));
    }

}
