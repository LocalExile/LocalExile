using System.Net;
using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Manual simulation time, real gateway damage and ordered native output; never starts the wall-clock pump.
sealed class ProjectileRuntimeFixture : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly TcpClient writer = new(), reader;
    readonly ConnectionCiphers decrypt = new(new byte[64], true);
    public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
    public NetworkStream Stream => writer.GetStream();
    public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
    bool outputHeld;
    public async Task HoldOutput() { await Gate.WaitAsync(); outputHeld = true; }
    public void ReleaseOutput() { if (outputHeld) { outputHeld = false; Gate.Release(); } }
    public ClientState Conn { get; }
    public Program.ProjectileSession Session { get; }
    public int Available => reader.Available;
    public ProjectileRuntimeFixture(ClientState? conn = null)
    {
        TestSaves.Ensure();
        Conn = conn ?? new(new AreaLayout(1, "projectile-fixture", 300, 300, 10, 10, null, []), 1, 1)
            { PlayerLife = 100, ActionCount = 7 };
        listener.Start(); var accept = listener.AcceptTcpClientAsync();
        writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        reader = accept.GetAwaiter().GetResult();
        Session = new("runtime-fixture", Stream, Ciphers, Conn);
        Conn.ProjectileRuntime = Session;
    }
    public static SpawnedMonster Monster(int x, int y, double speed = 10) =>
        new("target", new(x, y, speed, width: 300, height: 300), 0, 2, 100);
    public ProjectileUse Shot(ProjectileProfile? profile = null, ProjectileOffense? offense = null,
        ProjectileVector? origin = null, ProjectileVector? aim = null) =>
        new(profile ?? new("Metadata/Projectiles/DefaultArrow") { Speed = 10, Range = 100, Radius = 1, Lifetime = ProjectileRuntime.Time(20) },
            new(Program.PlayerId, Conn.ProjectileEpoch), new(Program.PlayerId, Conn.ProjectileEpoch), 1,
            origin ?? new(10, 10), aim ?? new(110, 10))
        { Payload = offense ?? new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 10, 10), true,
            int.MaxValue, 0, 150, "fixture") };
    public void Release(ProjectileUse? shot = null)
    { Session.Schedule(Session.Runtime.Now, () => true, () => shot ?? Shot()); Session.AdvanceTo(Session.Runtime.Now); }
    public void Advance(double seconds) => Session.AdvanceTo(ProjectileRuntime.Time(seconds));
    public async Task<byte[]> Read()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Session.Enqueue(() => { drained.SetResult(); return Task.CompletedTask; });
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] sentinel = new LoginWriter().U16(0x7ffe).U32(0x12345678).ToArray();
        await Program.Send("runtime-fixture", Stream, Ciphers, sentinel.ToArray(), null);
        var result = new List<byte>(); var buffer = new byte[4096];
        while (true)
        {
            int n = await reader.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(n > 0);
            byte[] chunk = buffer[..n]; decrypt.Receive.Process(chunk); result.AddRange(chunk);
            if (result.Count >= sentinel.Length && result.TakeLast(sentinel.Length).SequenceEqual(sentinel))
                return result.Take(result.Count - sentinel.Length).ToArray();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Conn.Closed = true; Session.Invalidate();
        ReleaseOutput();
        await Session.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        reader.Dispose(); writer.Dispose(); listener.Stop();
    }
}

// Records the public runtime protocol; geometry, ordering, continuation and hit admission all remain in Core.
sealed class ProjectileRuntimeProbe : IProjectileWorld
{
    public readonly List<ProjectileActor> Targets = [];
    public readonly List<ProjectileDamage> Attempts = [];
    public readonly List<ProjectileEvent> Events = [];
    public Func<ProjectileDamage, ProjectileHitResult> Accept = _ => ProjectileHitResult.Hit;
    public Func<int, int, bool> Blocked = (_, _) => false;
    public ProjectileRuntime Runtime { get; }
    public ProjectileRuntimeProbe() => Runtime = new(this);
    public void Target(uint id, double x, double y) => Target(id, _ => new(x, y));
    public void Target(uint id, Func<double, ProjectileVector> position) =>
        Targets.Add(new(new(id), 2, at => position(at / (double)ProjectileRuntime.QuantaPerSecond), 0));
    public void Release(ProjectileProfile? profile = null) => Runtime.Release(new(
        profile ?? new("test") { Speed = 10, Range = 100, Radius = 1, Lifetime = ProjectileRuntime.Time(20) },
        new(1000), new(1000), 1, new(10, 10), new(110, 10)));
    public void Advance(double seconds) => Runtime.AdvanceTo(ProjectileRuntime.Time(seconds));
    public IReadOnlyList<ProjectileActor> Actors(long from, long until) => Targets;
    public TerrainContact? TraceTerrain(ProjectileVector from, ProjectileVector to) => ProjectileGeometry.TraceGrid(from, to, Blocked);
    public ProjectileHitResult Hit(ProjectileDamage damage) { Attempts.Add(damage); return Accept(damage); }
    public void Publish(ProjectileEvent value) => Events.Add(value);
}
