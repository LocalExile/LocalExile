/// <summary>A monster's committed death action in a live instance. The Game rules resolve and present it.
/// The instance holds only Gateway data, so a replacement Game assembly can finish the same action.</summary>
public sealed class MonsterDeathCast(SpawnedMonster source, (int X, int Y) at, MonsterAttack attack,
    byte[] skill, byte[] action, List<MonsterDeathCast.Viewer> viewers, ClientState owner, long generation)
{
    public SpawnedMonster Source { get; } = source;
    public (int X, int Y) At { get; } = at;
    public MonsterAttack Attack { get; } = attack;
    public byte[] Skill { get; } = skill;
    public byte[] Action { get; } = action;
    public List<Viewer> Viewers { get; } = viewers;
    public ClientState Owner = owner;
    public long OwnerGeneration = generation;
    public long Publisher;
    public KillDelivery Dot;
    public bool DotPlayerKill, KillCredited, Resolved, OutputDue;
    public HashSet<ClientState>? Removals;

    public enum KillDelivery { None, RunnerOutput, OwnerQueue, Accepted }

    public sealed class Viewer(ClientState player, bool awake, (int X, int Y)? at, GolemState.Golem[] minions)
    {
        public ClientState Player { get; } = player;
        public bool Awake { get; } = awake;
        public (int X, int Y)? At { get; } = at;
        public GolemState.Golem[] Minions { get; } = minions;
        public readonly List<(uint Id, SpawnedMonster Target, byte[] Packet)> MinionHits = [];
        public readonly List<Weapon> Weapons = [];
        public PlayerHit? Hit;
        public int Epoch = player.IncomingHitEpoch;
        public bool Presented;
    }

    public sealed record Weapon(uint Id, long Epoch, SpawnedMonster Monster, (int X, int Y) At)
    {
        public byte[]? Hit;
    }

    public sealed record PlayerHit(byte[] HitPacket, uint Life, uint Mana, uint ManaBefore, int Damage, int RawDamage,
        bool Blocked, bool Critical, (int Epoch, int Action, double Until, float Seconds)? Recovery,
        RecentEnemyHits.Decision Armour, bool Spirit);
}
