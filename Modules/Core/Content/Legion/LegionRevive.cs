using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Legion;

/// <summary>
/// The client's <c>LegionRevive</c> action (every Legion monster has it, R13): how a Domain of Timeless Conflict soldier lies
/// as a crystal ghost, stands up and fights again. A ghost is an inert monster (scripted, no body, no hits, no rewards, no life
/// bar) created running the action; the server moves it with S->C 0x2c8 and the client runs the timers and the looks itself:
/// state 4 lies ready (effect pack LegionEndlessActive), 3 falls and waits its cooldown (LegionEndlessInactive, then 4 by
/// itself), 5 vanishes for 0.8 s and stands up (LegionEndlessReviving, then the client's own state 1), 1 is the revived glow
/// and the beam (LegionEndlessRevived, MiscAnimated 3377), then 2. Once it stands, the ghost is swapped for an ordinary
/// fighting monster of the same variety where it stands (a scripted spawn cannot fight).
/// </summary>
static class LegionRevive
{
    /// <summary>ActionTypes 1007 LegionRevive: HASH16 (the 0x2c8 header's action type) and its basic skill id (HASH16 &amp; 0x3fff).</summary>
    public const ushort ActionType = 0xd59a, ActionSkill = 0x159a;
    /// <summary>The action's states (client 0x1413b0fa0): revived (stands, glows), fallen (cooldown), ready (lying), reviving (vanish).</summary>
    public const int Revived = 1, Fallen = 3, Ready = 4, Reviving = 5;
    /// <summary>The client's own times: state 5's vanish (0x141c4aa70), DoRevive's stand-up (0x141c4a8d0), StartReviveTimer's fall
    /// (0x141c4a600, 3.0) and its pose range, 0.6..0.8 of the death clip.</summary>
    public const float VanishSeconds = 0.8f, StandSeconds = 1.0f, FallSeconds = 3.0f;
    /// <summary>GUESS: the death clip's length. The server has no monster death clip index or length (monster_takehit lists only
    /// their names), so the fall's pose is 0.7 of this; a shorter clip ends lying at its last frame.</summary>
    const float DeathClipSecondsGuess = 1.4f;
    /// <summary>The pose the fall stops at and the speed it starts with, as StartReviveTimer computes them (+0x324, +0x328 = pose / 3).</summary>
    public const float PoseSeconds = 0.7f * DeathClipSecondsGuess, FallSpeed = PoseSeconds / FallSeconds;

    /// <summary>A ghost lies, stands up, fights; it does not take hits or show a bar (virtual_is_hidden_monster also takes the hover away
    /// on a friendly encased mini, EssenceEncased.cs; GUESS that it does on a hostile one).</summary>
    static readonly IReadOnlyList<(uint, int)> GhostStats = [(Stat.HideMiniLifeBar, 1), (Stat.VirtualIsHiddenMonster, 1)];

    static int sequences;
    /// <summary>The sequence of the next LegionRevive action (the 0x2c8 updates must carry it): a counter, never 0.</summary>
    public static ushort NextSequence() => (ushort)((uint)Interlocked.Increment(ref sequences) % 0xffff + 1);

    /// <summary>The action as a create carries it (reader 0x1413b0920): an animation block (clip -1: none), then
    /// <c>i32 state, u8 falling (+0x318), f32 pose (+0x324), f32 fall speed (+0x328), f32 fall time (+0x320), f32 timer (+0x334),
    /// u8 revive (+0x330), u8 leave (+0x331)</c>. Created ready with revive 1 and timer 0, the client poses it at once with its own
    /// death clip near its end (state 4's FrameMove, 0x141c4a8f0 plays Animation 6 Death at 0.95..1.0 of the clip): lying.</summary>
    public static ActorAction Action(ushort sequence, int state, float timer, bool revive) =>
        new(ActionSkill, sequence)
        {
            Animation = new AnimationControllerState(-1, 0f),
            Payload = new BlobWriter().I32(state).U8(0).F32(0).F32(0).F32(0).F32(timer).U8((byte)(revive ? 1 : 0)).U8(0).ToArray(),
        };

    /// <summary>0x2c8 payloads (handler 0x1413b0fa0), little-endian, after <c>i32 state</c>.</summary>
    public static byte[] RevivedPayload(float timer) => new BlobWriter().I32(Revived).F32(timer).U8(0).ToArray();
    /// <summary>State 3: <c>u8 falling 1, f32 pose, f32 speed, f32 fall time, f32 timer</c>: the client plays its current clip (the death
    /// clip the ready pose left) from 0 at the speed, slowing to a stop over the fall time, and turns to 4 when the timer ends.</summary>
    public static byte[] FallenPayload(float timer) =>
        new BlobWriter().I32(Fallen).U8(1).F32(PoseSeconds).F32(FallSpeed).F32(FallSeconds).F32(timer).ToArray();
    /// <summary>State 5: <c>f32 timer, u8 revive, u8 leave</c>. With revive 1 the client stands it up at the timer's end (its state 1).</summary>
    public static byte[] RevivingPayload() => new BlobWriter().I32(Reviving).F32(VanishSeconds).U8(1).U8(0).ToArray();

    /// <summary>Writes the ghost's state into its origin, so a client that joins later is created in it (its create carries the action),
    /// as LegionStasis.SetOriginState does. A fallen ghost's origin stays ready (state 4): created in state 3 the ghost would stand
    /// unposed until its cooldown ends, since only state 4 poses it (no death clip is named in the create).</summary>
    public static void SetOrigin(Player player, uint id, ushort sequence, int state, float timer, bool revive)
    {
        var origins = player.Instance.World.MonsterOrigins;
        if (origins.TryGetValue(id, out var origin))
            origins[id] = origin with { Object = origin.Object with { CurrentAction = Action(sequence, state, timer, revive) } };
    }

    /// <summary>Sends a 0x2c8 for the ghost's action to every client in the area.</summary>
    public static Task Send(Player player, uint id, ushort sequence, byte[] payload, string? log = null) =>
        LegionOutput.ToArea(player, p => global::Game.SendActionData(p, id, ActionType, ActionSkill, sequence, 0, payload, log));

    /// <summary>Spawns a ghost lying ready (state 4): no AI, no body, no hits, no rewards, no bar. Returns its id, or 0.</summary>
    public static async Task<uint> SpawnGhost(Player player, MonsterCatalog.Variety variety, byte level, MonsterRarity rarity,
        int x, int y, float facing, ushort sequence)
    {
        uint id = await SpawnMonster(player, variety, level, rarity, x, y,
            currentAction: Action(sequence, Ready, 0, revive: true), nonBlocking: true,
            friendly: false, unrisen: false, alone: false, scripted: true, instanceOwned: true, bulk: true, log: false,
            placement: MonsterPlacement.Established, rotation: facing, extraStats: GhostStats);
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var ghost)) return 0;
        ghost.NoRewards = true; ghost.Attack = null; ghost.Aggro = false; ghost.EntranceGrace = true;
        ghost.BusyUntil = double.PositiveInfinity;
        return id;
    }

    /// <summary>Removes monsters (ghosts, fighters, corpses) from the instance and from every client in the area.</summary>
    public static async Task Remove(Player player, IReadOnlyCollection<uint> ids)
    {
        var gone = new List<uint>(ids.Count);
        foreach (uint id in ids)
            if (player.Area.Monsters.Remove(id, out var m))
            {
                m.Dead = true; m.Life = 0; m.ActionGeneration++;
                player.Instance.World.MonsterOrigins.TryRemove(id, out _);
                gone.Add(id);
            }
        if (gone.Count == 0) return;
        var members = LegionOutput.Members(player).ToList();
        if (members.Count == 0)
            foreach (uint id in gone) LegionOutput.ForgetComponents(player, id);
        else
            await LegionOutput.ToArea(members, async p => { foreach (uint id in gone) await global::Game.SendWorldRemoval(p, id); });
    }
}
