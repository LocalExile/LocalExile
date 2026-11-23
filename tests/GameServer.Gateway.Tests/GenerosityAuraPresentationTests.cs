using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A Generosity aura does not affect its caster, but it is still running: the source record draws the aura art and the
// received record carries the skill-bar association. Only the self-buff icon is suppressed (display state 0), and both
// records must be removed and restored symmetrically — including when a Game module reload leaves old records behind.
public sealed class GenerosityAuraPresentationTests
{
    static AuraCatalog.Aura Aura(bool affectsSelf) => new(7, Program.Skills.EffectRow("Haste"), "Haste", "Haste",
        100, 155, 0, 50, Level: 20, SourceBuff: 101, AffectsSelf: affectsSelf);

}
