using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class MonsterLifeDataTests
{
    [Theory]
    [InlineData(true, false, false, 0u, true)]
    [InlineData(true, false, false, 1u, false)]
    [InlineData(true, false, false, 100u, false)]
    [InlineData(false, false, false, 0u, false)]
    [InlineData(true, false, true, 0u, false)]
    [InlineData(null, null, null, 0u, false)]
    [InlineData(true, false, null, 0u, false)]
    public void Ordinary_source_requires_known_usable_no_veto_and_zero_life(
        bool? usable, bool? sinking, bool? veto, uint life, bool expected) =>
        Assert.Equal(expected, new MonsterLifeData(usable, sinking, veto).IsUsableAsCorpse(life));

    [Fact]
    public void Owned_sources_remain_ineligible_even_with_the_alive_exception()
    {
        var data = new MonsterLifeData(true, false, false);
        Assert.False(data.IsUsableAsCorpse(0, owned: true));
        Assert.False(data.IsUsableAsCorpse(1, owned: true, isUsableAsCorpseWhenAlive: true));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, null, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void Spectre_corpses_allow_no_skill_context_or_a_minion_skill_without_the_veto(
        bool spectre, bool? minion, bool cannotUse, bool expected)
    {
        var data = new MonsterLifeData(true, false, false);
        Assert.Equal(expected, data.IsUsableAsCorpse(0, isSpectreCorpse: spectre,
            skillIsMinionSkill: minion, cannotUseSpectreCorpses: cannotUse));
        // The native early return precedes the stat-9525 fallback, even on an otherwise living-usable target.
        if (!expected)
            Assert.False(data.IsUsableAsCorpse(1, isSpectreCorpse: spectre, skillIsMinionSkill: minion,
                cannotUseSpectreCorpses: cannotUse, isUsableAsCorpseWhenAlive: true));
    }

    [Fact]
    public void Explicit_alive_exception_is_a_fallback_even_when_the_ordinary_path_is_disabled()
    {
        Assert.True(new MonsterLifeData(true, false, false).IsUsableAsCorpse(1, isUsableAsCorpseWhenAlive: true));
        Assert.True(new MonsterLifeData(false, false, true).IsUsableAsCorpse(10, isUsableAsCorpseWhenAlive: true));
        Assert.True(new MonsterLifeData(false, false, true).IsUsableAsCorpse(0, isUsableAsCorpseWhenAlive: true));
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 3)]
    public void Life_flags_preserve_both_bits_without_turning_a_living_monster_into_a_corpse(
        bool? usable, bool? sinking, byte expected)
    {
        var baseline = new MonsterObject(10, 20, 100, MonsterObject.BaseComponents);
        var data = new MonsterLifeData(usable, sinking, false);
        var bytes = (baseline with { LifeData = data }).Blob();
        // flags/base 2 + Positioned 18 + Stats 10 + magic 4 + Buffs 2 + three 18-byte pools = 90.
        Assert.Equal(expected, bytes[90]);
        Assert.Equal(baseline.Blob().Length, bytes.Length);
        bytes[90] = 0;
        Assert.Equal(baseline.Blob(), bytes);
        Assert.False(data.IsUsableAsCorpse(100));
    }
}
