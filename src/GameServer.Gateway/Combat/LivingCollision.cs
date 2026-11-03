using GameServer.Core.World;

static class LivingCollision
{
    public const int PlayerSize = 1;
    public static int Size(SpawnedMonster m) => m.Variety is { } v ? Math.Clamp(v.ObjectSize - 1, -1, 127) : 2;
    public static bool Blocks(SpawnedMonster m) => !m.Dead && m.Life > 0 && Size(m) >= 0;
}
