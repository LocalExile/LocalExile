using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static GameServer.Modules.Survivors.SurvivorsSkills;

namespace GameServer.Modules.Survivors;

static partial class SurvivorsRunRules
{
    internal static IModuleLifetime? Lifetime;
    static readonly ConcurrentDictionary<ClientState, byte> Active = new();
    internal static readonly ConcurrentDictionary<ClientState, SurvivorsCarry> PendingRuns = new();

    internal static string? TravelRefusal(ClientState conn, PendingTransfer transfer) =>
        StateOf(conn).Survivors is not null && !ReferenceEquals(StateOf(conn).StageTransfer, transfer)
            ? "Finish the Survivors stage; the run opens the next area after its boss falls." : null;

    internal static async Task TransferResolved(ClientState conn, PendingTransfer transfer, ControlResponse response)
    {
        if (!PendingRuns.TryRemove(conn, out var carry)) return;
        if (response.Ok)
        {
            foreach (var expired in SurvivorsCarried.Where(p => DateTime.UtcNow - p.Value.At > TimeSpan.FromMinutes(5)))
                SurvivorsCarried.TryRemove(expired.Key, out _);
            SurvivorsCarried[response.Ticket] = carry;
        }
        else
        {
            // Re-use the retained area and run after an explicit Auth refusal, without wiping its inventory.
            SurvivorsCarried[conn.Ticket] = carry with { Stage = Math.Max(0, carry.Stage - 1) };
            var (remote, stream, ciphers) = conn.Wire;
            await ResumeSurvivors(remote, stream, ciphers, conn);
        }
    }

    internal static bool Enabled(ClientState conn) => Lifetime?.IsActive == true
        && LeagueFor(conn).Modules.Contains("SurvivorsRun");

    internal static bool TryClaimAdmission(ClientState conn)
    {
        if (!Enabled(conn)) return false;
        lock (conn.Monsters)
        {
            if (StateOf(conn).Entered) return false;
            StateOf(conn).Entered = true;
            return true;
        }
    }

    internal static async Task EnterArea(ClientState conn)
    {
        if (!TryClaimAdmission(conn) || conn.Closed || conn.LeavingArea) return;
        var (remote, stream, ciphers) = conn.Wire;
        if (InTown(conn) || HideoutCatalog.ByArea(conn.Layout.Hash) is not null)
        {
            if (AreaTransitionCatalog.HashOf(SurvivorsLevels[0]) is { } first)
                await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(first, 0, false), "survivors entry");
            return;
        }
        await ResumeSurvivors(remote, stream, ciphers, conn);
        if (StateOf(conn).Survivors is null) await StartSurvivors(remote, stream, ciphers, conn);
    }

    /// <summary>
    /// A resurrection ends whatever run was going and starts a new one from the very beginning: the opening area, an
    /// empty character and the first-skill choice. The "in town" button's own area change hands the start to the town's
    /// area hook, so no second transfer is queued.
    /// </summary>
    internal static async Task Resurrected(ClientState conn)
    {
        if (!Enabled(conn)) return;
        StateOf(conn).Stage = 0;
        if (conn.PendingTransfer is not null) return;
        if (AreaTransitionCatalog.HashOf(SurvivorsLevels[0]) is { } first)
        {
            var (remote, stream, ciphers) = conn.Wire;
            await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(first, 0, false), "survivors resurrection");
        }
    }

    internal static void StartRunner(ClientState conn, Func<CancellationToken, Task> run)
    {
        var state = StateOf(conn);
        var expected = state.Survivors;
        var ownership = state.Ownership is { } existing && ReferenceEquals(existing.Run, expected)
            ? existing : BindRun(conn, expected!);
        var cancellation = ownership.Cancellation;
        state.Runner = Run();
        async Task Run()
        {
            try { await run(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(state.Cancellation, cancellation))
                {
                    Release(conn, expected);
                    state.Cancellation = null;
                }
                cancellation.Dispose();
            }
        }
    }

    internal static SurvivorsOwnership BindRun(ClientState conn, SurvivorsRun run)
    {
        lock (conn.Monsters)
        {
            var state = StateOf(conn);
            state.Cancellation?.Cancel();
            if (state.Ownership is { } previous)
                (conn.ProjectileRuntimeState as global::Game.ProjectileSession)?.Runtime.Discard(use => use.Payload is ProjectileOffense shot
                    && ReferenceEquals(shot.Owner, previous));
            if (state.SurvivorsPausedByWindow) conn.Clock.Start();
            state.SurvivorsPausedByWindow = false;
            conn.PauseHeld = false;
            state.SurvivorsOffer = null;
            state.Survivors = run;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime!.Stopping, SkillsStopping, RewardsStopping);
            state.Cancellation = cancellation;
            state.Ownership = new(conn, run, cancellation);
            Active[conn] = 0;
            return state.Ownership;
        }
    }

    internal static void Release(ClientState conn, SurvivorsRun? expected = null)
    {
        ushort[] buffs;
        uint[] orbs;
        uint[] zones;
        bool closeWindow, wasPaused;
        lock (conn.Monsters)
        {
            var state = StateOf(conn);
            if (expected is not null && !ReferenceEquals(state.Survivors, expected)) return;
            buffs = [.. state.SurvivorsHeraldBuffs];
            state.SurvivorsHeraldBuffs.Clear();
            state.SurvivorsHeraldSkills.Clear();
            orbs = [.. state.SurvivorsOrbs.Select(orb => orb.Id)];
            state.SurvivorsOrbs.Clear();
            zones = [.. state.SurvivorsGroundZones.Select(zone => zone.Id)];
            state.SurvivorsGroundZones.Clear();
            state.SurvivorsCorpses.Clear();
            closeWindow = state.SurvivorsOffer is not null;
            wasPaused = state.SurvivorsPausedByWindow;
            state.Cancellation?.Cancel();
            var retired = state.Ownership;
            state.Ownership = null;
            if (retired is not null)
                (conn.ProjectileRuntimeState as global::Game.ProjectileSession)?.Runtime.Discard(use => use.Payload is ProjectileOffense shot
                    && ReferenceEquals(shot.Owner, retired));
            state.Survivors = null;
            state.SurvivorsCatalog = null;
            state.SurvivorsSupports = null;
            state.SurvivorsOffer = null;
            state.SurvivorsOfferItems = null;
            state.SurvivorsOfferList = null;
            state.SurvivorsOfferNames = null;
            state.SurvivorsItemIds.Clear();
            if (state.SurvivorsPausedByWindow)
            {
                state.SurvivorsPausedByWindow = false;
                conn.PauseHeld = false;
                conn.Clock.Start();
            }
            Active.TryRemove(conn, out _);
        }
        if (!conn.Closed && !conn.LeavingArea && conn.Wire.Ciphers is not null)
            _ = ClearPresentation();

        async Task ClearPresentation()
        {
            try
            {
                var (remote, stream, ciphers) = conn.Wire;
                if (wasPaused)
                    await Send(remote, stream, ciphers, InstancePause.State(false),
                        "0x01d3 resumed (the survivors run ended)");
                foreach (ushort buff in buffs)
                    await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, buff), "survivors retired aura");
                foreach (uint orb in orbs)
                    await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(orb), "survivors retired xp orb");
                foreach (uint zone in zones)
                    await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(zone), "survivors retired death zone");
                if (closeWindow)
                    await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "survivors retired reward window",
                        still: () => StateOf(conn).Survivors is null);
                if (conn.PlayerObjectCreated && StateOf(conn).Survivors is null)
                {
                    await SendCharacterStats(remote, stream, ciphers, conn, "survivors retired equipment exemptions");
                    await SendSkills(remote, stream, ciphers, conn);
                    await SendSkillBar(remote, stream, ciphers, conn);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    internal static void ReleaseAll()
    {
        foreach (var conn in Active.Keys) Release(conn);
        PendingRuns.Clear();
        SurvivorsCarried.Clear();
    }
}
