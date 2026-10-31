using System.Diagnostics;
using GameServer.Core.Protocol.Instance;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;


/// <param name="AtSpawn">Arrive at the destination's spawn (Resurrect in Town) instead of next to the exit back.</param>
/// <param name="AtWaypoint">Waypoint travel: arrive next to the destination's waypoint.</param>
/// <summary>A character's open town portal: where it stands and where it leads (OpenTownPortal).</summary>
sealed record OpenPortal(ushort Area, ulong Instance, int X, int Y, ushort LeadsTo);
