using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using SaveFiles = GameServer.Core.Saves.SaveFiles;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageRange = GameServer.Core.World.DamageRange;
using DamageOverTime = GameServer.Core.World.DamageOverTime;
using GameServer.Core.Diagnostics;

static partial class PortalsRules
{
    // How close the player walks to the map device before it opens (its size is not known yet).
    internal const double StashReach = 6;

    /// <summary>Each character's open town portal (Portal Scroll or the Portal skill): where it stands (area, instance,
    /// cell) and where it leads (a town's HASH16, or the hideout's). While the server runs.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, OpenPortal> OpenPortals = new(StringComparer.OrdinalIgnoreCase);

    // WorldAreas' own mods (AreaModCatalog); loaded with the first Portal Scroll.
    internal static readonly Lazy<AreaModCatalog> AreaMods = new(() => AreaModCatalog.Load());
    internal const string PortalScroll = "Metadata/Items/Currency/CurrencyPortal";
}
