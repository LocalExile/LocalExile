using GameServer.Core.World;

public static class LivingCollision
{
    public const int PlayerSize = 1;
    public static int Size(SpawnedMonster m) => m.Variety is { } v ? Math.Clamp(v.ObjectSize - 1, -1, 127) : 2;
    // Moving around bodies and presenting a blocking body are separate native properties. An untargetable
    // buried actor still blocks at 141e65ae0; a nonblocking actor must still run its own movement/goal checks.
    public static bool StepsWithBodies(SpawnedMonster m) => !m.Dead && m.Life > 0 && Size(m) >= 0;
    public static bool Blocks(SpawnedMonster m) => StepsWithBodies(m) && m.Variety?.PositionedBlocking != false && !Phases(m);
    /// <summary>A monster whose type phases through objects: no body to the others, and stopped by none of theirs (the
    /// client's player walked straight through Maven's orbs and they through each other, path searches and step checks
    /// traced 2026-11-10). Terrain and stationary objects still stop it.</summary>
    public static bool Phases(SpawnedMonster m) => m.Variety?.Phases == true;
}
