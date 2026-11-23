// Only primitive/runtime data survives replacement of the Game module; no scheduled module callbacks are retained.
public sealed class MadnessState
{
    public object? Owner;
    public WornGrantedSkills.Grant? Source;
    public ushort Skill;
    public bool Active;
    public double NextTouch, ReadyAt;
    public double ExpiryRate = 1;
    public readonly int[] Touches = new int[4];
    public readonly double[] Until = new double[4];
    public long Revision;
    public Random Random = Random.Shared;
    public readonly Queue<(uint Id, SpawnedMonster Corpse, MadnessExplosion Receipt)> Explosions = new();

    public void Clear()
    {
        Active = false;
        NextTouch = 0;
        Array.Clear(Touches);
        Array.Clear(Until);
        Revision++;
    }
}

public sealed record MadnessExplosion(object Owner, Player Killer, IReadOnlyDictionary<uint, int> Player,
    GameServer.Core.World.DamagePacket Raw, double Radius, (int X, int Y) At);

public sealed partial class Player
{
    public readonly MadnessState Madness = new();
}
