namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// Inherited Life type data. Null fields mean unresolved metadata, not the native constructor defaults.
/// This describes source eligibility only; corpse identity, final death and one-use consumption belong to the instance.
/// </summary>
public sealed record MonsterLifeData(bool? CorpseUsable, bool? PreventCorpseSinking, bool? CorpseUseVeto)
{
    /// <summary>Life's trailing wire byte overwrites runtime +0x26a/+0x26b, including on living actors.</summary>
    public byte Flags => (byte)((CorpseUsable == true ? 1 : 0) | (PreventCorpseSinking == true ? 2 : 0));

    /// <summary>
    /// Source/type part of 0x141e6c610. Owned actors remain excluded by server policy.
    /// A staged boss at life 1 is alive. Null skillIsMinionSkill means no skill context (native null argument).
    /// Spectre restrictions precede the explicit stat-9525 fallback.
    /// </summary>
    public bool IsUsableAsCorpse(uint currentLife, bool owned = false, bool isSpectreCorpse = false,
        bool? skillIsMinionSkill = null, bool cannotUseSpectreCorpses = false, bool isUsableAsCorpseWhenAlive = false)
    {
        if (owned) return false;
        if (CorpseUsable == true && CorpseUseVeto == false)
        {
            if (isSpectreCorpse && skillIsMinionSkill is bool minion && (!minion || cannotUseSpectreCorpses)) return false;
            if (currentLife < 1) return true;
        }
        return isUsableAsCorpseWhenAlive;
    }
}
