using GameServer.Core;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Anguish's Fiery Rubble and Meteor Firestorm: its display mod carries
/// <c>essence_anguish_spawn_monster_on_death_variation</c> 6 (SummonedSpecificMonstersOnDeath row 5: four 1st-stage
/// rubble that fracture into 2nd → 3rd stages via <c>EssenceDeathEffects.cs</c>), and its monster mod
/// <c>EssenceDaemonMeteorFirestorm</c> (GE 1409) calls down a 3 s meteor Firestorm (1 ball/s, 25% ignite chance) using
/// the shared <see cref="CastStorm"/> loop.</summary>
public static partial class MapEssences
{
    public const string AnguishMeteorMod = "EssenceDaemonMeteorFirestorm";
    public const string AnguishDeathStat = "essence_anguish_spawn_monster_on_death_variation";
    public const string AnguishRubbleFirst = "Metadata/Monsters/MossMonster/EssenceElementalSummonedOnDeath1stStage";
    public const string AnguishRubbleSecond = "Metadata/Monsters/MossMonster/EssenceElementalSummonedOnDeath2ndStage";
    public const string AnguishRubbleThird = "Metadata/Monsters/MossMonster/EssenceElementalSummonedOnDeath3rdStage";
    const string AnguishStormArt = "Metadata/Effects/Spells/firestorm/rig.ao";
    const string AnguishMeteorImpactArt = "Metadata/Effects/Spells/monsters_effects/fire_elemental/meteor/meteorexplosion.ao";

    static Task CastMeteorFirestorm(ClientState conn, SpawnedMonster monster, ClientState target, SpecialSpell spell)
        => CastStorm(conn, monster, target, spell, AnguishMeteorImpactArt, AnguishStormArt, "meteor firestorm");
}