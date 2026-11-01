using System.Collections.Frozen;
using GameServer.Modules.PhreciaAscendancies.Classes.Antiquarian;
using GameServer.Modules.PhreciaAscendancies.Classes.Aristocrat;
using GameServer.Modules.PhreciaAscendancies.Classes.Gambler;
using GameServer.Modules.PhreciaAscendancies.Classes.Behemoth;
using GameServer.Modules.PhreciaAscendancies.Classes.AncestralCommander;
using GameServer.Modules.PhreciaAscendancies.Classes.Paladin;
using GameServer.Modules.PhreciaAscendancies.Classes.Polytheist;
using GameServer.Modules.PhreciaAscendancies.Classes.Surfcaster;

namespace GameServer.Modules.PhreciaAscendancies;

/// <summary>Capability is deliberately narrower than the generic, save-readable all-class catalog.</summary>
internal sealed class PhreciaFamilyPolicy : IAscendancyFamilyPolicy
{
    // A class is registered only after its whole-class consumers are implemented. New classes add their own
    // descriptor directory and one entry here; catalog membership alone never enables a class.
    static readonly PhreciaClass[] ImplementedClasses =
    [
        GamblerClass.Descriptor,
        AntiquarianClass.Descriptor,
        AristocratClass.Descriptor,
        BehemothClass.Descriptor,
        AncestralCommanderClass.Descriptor,
        PaladinClass.Descriptor,
        PolytheistClass.Descriptor,
        SurfcasterClass.Descriptor,
    ];

    static readonly IReadOnlyDictionary<uint, int> GraphSelector =
        new Dictionary<uint, int> { [21146] = 1 }.ToFrozenDictionary();

    public AscendancyFamily Family => AscendancyFamily.Phrecia;

    // Choice listing must not open the archive or start a decoder. Installed bytes are checked only on admission.
    public bool Supports(AscendancyIdentity identity) => Find(identity) is { Enabled: true };

    public void RequirePlayable(AscendancyIdentity identity)
    {
        var implementation = Find(identity)
            ?? throw new AscendancyRuleException(
                $"Ascendancy {identity.Family}/{identity.Id}@{identity.Revision} has no implemented Phrecia class policy; "
                + "see Agents/docs/game/ascendancies.md");
        implementation.RequirePlayable();
    }

    public IReadOnlyDictionary<uint, int>? AreaStats => GraphSelector;

    // The module owns the optional source, while Game continues to consume ordinary stat keys. No graph reads
    // occur during stat rebuilding, and unsupported families, revisions or classes contribute nothing.
    public IReadOnlyList<(uint Key, int Value)> NodeStats(AscendancyCatalog.Definition definition, ushort node,
        IReadOnlyList<(uint Key, int Value)> modeStats) =>
        Supports(definition.Identity) && definition.Nodes.ContainsKey(node) ? modeStats : [];

    static PhreciaClass? Find(AscendancyIdentity identity) =>
        ImplementedClasses.FirstOrDefault(implementation => implementation.Matches(identity));
}
