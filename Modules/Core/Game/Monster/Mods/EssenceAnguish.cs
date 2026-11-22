using GameServer.Core;
using static Game;
using static Program;

/// <summary>Anguish's Fiery Rubble: the wiki's behaviour is a summon <b>on the rare's death</b> — "Summons Fiery Rubble that
/// fractures into smaller variants on death, which is a unique Cinder Elemental". Its display mod carries
/// <c>essence_anguish_spawn_monster_on_death_variation</c> 6, which selects SummonedSpecificMonstersOnDeath row 5: the 1st
/// stage, <b>four</b> of them. Each stage's own death brings the next — row 6 (2nd, ×2) then row 7 (3rd, ×2) — so the swarm
/// fractures down instead of dying once. The shared on-death queue (EssenceDeathEffects) drives it; only the stat, the
/// varieties and the chain are Anguish's.
/// <para>The essence also carries an <c>EssenceDaemonMeteorFirestorm</c> monster mod. It is a firestorm-shaped spell (one
/// ball per second for three seconds), but it is not the behaviour the wiki describes for Anguish and a storm aimed at the
/// player read as nothing like the encounter, so it is left out until it can be checked against retail.</para></summary>
public static partial class MapEssences
{
    public const string AnguishDeathStat = "essence_anguish_spawn_monster_on_death_variation";
    public const string AnguishRubbleFirst = "Metadata/Monsters/MossMonster/EssenceElementalSummonedOnDeath1stStage";
    public const string AnguishRubbleSecond = "Metadata/Monsters/MossMonster/EssenceElementalSummonedOnDeath2ndStage";
    public const string AnguishRubbleThird = "Metadata/Monsters/MossMonster/EssenceElementalSummonedOnDeath3rdStage";
}