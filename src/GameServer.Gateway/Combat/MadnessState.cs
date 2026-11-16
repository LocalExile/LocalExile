// Only primitive/runtime data survives replacement of the Game module; no scheduled module callbacks are retained.
public sealed class MadnessState
{
    public object? Owner;
    public WornGrantedSkills.Grant? Source;
    public ushort Skill, Buff, PresentedBuff;
    public bool Active;
    public double NextTouch, ReadyAt;
    public double ExpiryRate = 1;
    public readonly int[] Touches = new int[4];
    public readonly double[] Until = new double[4];
    public readonly ushort[] TouchBuffs = new ushort[4], PresentedTouches = new ushort[4];
    public long Revision;
    public Random Random = Random.Shared;
    public readonly Queue<(uint Id, SpawnedMonster Corpse, MadnessExplosion Receipt)> Explosions = new();

    public void Clear()
    {
        Active = false;
        Buff = 0;
        NextTouch = 0;
        Array.Clear(Touches);
        Array.Clear(Until);
        Array.Clear(TouchBuffs);
        Revision++;
    }
}

public sealed record MadnessExplosion(object Owner, ClientState Killer, IReadOnlyDictionary<uint, int> Player,
    GameServer.Core.World.DamagePacket Raw, double Radius, (int X, int Y) At);

public sealed partial class ClientState
{
    public readonly MadnessState Madness = new();
}
