using System.Net.Sockets;
using System.Threading.Channels;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

partial class Program
{
    internal static readonly AsyncLocal<Func<bool>?> ProjectileOutputGuard = new();

}
