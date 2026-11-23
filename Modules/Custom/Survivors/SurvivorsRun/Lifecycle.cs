using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static GameServer.Modules.Survivors.SurvivorsSkills;

namespace GameServer.Modules.Survivors;

static partial class SurvivorsRunRules
{
    internal static IModuleLifetime? Lifetime;
    static readonly ConcurrentDictionary<Player, byte> Active = new();
    internal static readonly ConcurrentDictionary<Player, SurvivorsCarry> PendingRuns = new();

    internal static string? TravelRefusal(Player player, PendingTransfer transfer) =>
        StateOf(player).Survivors is not null && !ReferenceEquals(StateOf(player).StageTransfer, transfer)
            ? "Finish the Survivors stage; the run opens the next area after its boss falls." : null;

    internal static async Task TransferResolved(Player player, PendingTransfer transfer, ControlResponse response)
    {
        if (!PendingRuns.TryRemove(player, out var carry)) return;
        if (response.Ok)
        {
            foreach (var expired in SurvivorsCarried.Where(p => DateTime.UtcNow - p.Value.At > TimeSpan.FromMinutes(5)))
                SurvivorsCarried.TryRemove(expired.Key, out _);
            SurvivorsCarried[response.Ticket] = carry;
        }
        else
        {
            // Re-use the retained area and run after an explicit Auth refusal, without wiping its inventory.
            SurvivorsCarried[player.Ticket] = carry with { Stage = Math.Max(0, carry.Stage - 1) };
            await ResumeSurvivors(player);
        }
    }

    internal static bool Enabled(Player player) => Lifetime?.IsActive == true
        && LeagueFor(player).Modules.Contains("SurvivorsRun");

    internal static bool TryClaimAdmission(Player player)
    {
        if (!Enabled(player)) return false;
        if (StateOf(player).Entered) return false;
        StateOf(player).Entered = true;
        return true;
    }

    internal static async Task EnterArea(Player player)
    {
        if (!TryClaimAdmission(player) || player.Closed || player.LeavingArea) return;
        if (InTown(player) || HideoutCatalog.ByArea(player.Layout.Hash) is not null)
        {
            if (AreaTransitionCatalog.HashOf(SurvivorsLevels[0]) is { } first)
                await ChangeArea(player, new PendingTransfer(first, 0, false), "survivors entry");
            return;
        }
        await ResumeSurvivors(player);
        if (StateOf(player).Survivors is null) await StartSurvivors(player);
    }

    /// <summary>
    /// A resurrection ends whatever run was going and starts a new one from the very beginning: the opening area, an
    /// empty character and the first-skill choice. The "in town" button's own area change hands the start to the town's
    /// area hook, so no second transfer is queued.
    /// </summary>
    internal static async Task Resurrected(Player player)
    {
        if (!Enabled(player)) return;
        StateOf(player).Stage = 0;
        if (player.PendingTransfer is not null) return;
        if (AreaTransitionCatalog.HashOf(SurvivorsLevels[0]) is { } first)
        {
            await ChangeArea(player, new PendingTransfer(first, 0, false), "survivors resurrection");
        }
    }

    internal static void StartRunner(Player player, Func<CancellationToken, Task> run)
    {
        var state = StateOf(player);
        var expected = state.Survivors;
        var ownership = state.Ownership is { } existing && ReferenceEquals(existing.Run, expected)
            ? existing : BindRun(player, expected!);
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
                    Release(player, expected);
                    state.Cancellation = null;
                }
                cancellation.Dispose();
            }
        }
    }

    /// <summary>The Game module's conn.ProjectileRuntime, read here without it: the 10.0.1xx SDKs, the ones Linux distributions ship,
    /// cannot use another assembly's internal extension member (CS0570), though newer ones can.</summary>
    static ProjectileSession? ProjectileSessionOf(Player player) => player.ProjectileRuntimeState as ProjectileSession;

    internal static SurvivorsOwnership BindRun(Player player, SurvivorsRun run)
    {
        var state = StateOf(player);
        state.Cancellation?.Cancel();
        if (state.Ownership is { } previous)
            ProjectileSessionOf(player)?.Runtime.Discard(use => use.Payload is ProjectileOffense shot
                && ReferenceEquals(shot.Owner, previous));
        if (state.SurvivorsPausedByWindow) player.Clock.Start();
        state.SurvivorsPausedByWindow = false;
        player.PauseHeld = false;
        state.SurvivorsOffer = null;
        state.Survivors = run;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime!.Stopping, SkillsStopping, RewardsStopping);
        state.Cancellation = cancellation;
        state.Ownership = new(player, run, cancellation);
        Active[player] = 0;
        return state.Ownership;
    }

    internal static void Release(Player player, SurvivorsRun? expected = null)
    {
        ushort[] buffs;
        uint[] orbs;
        uint[] zones;
        bool closeWindow, wasPaused;
        var state = StateOf(player);
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
            ProjectileSessionOf(player)?.Runtime.Discard(use => use.Payload is ProjectileOffense shot
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
            player.PauseHeld = false;
            player.Clock.Start();
        }
        Active.TryRemove(player, out _);
        if (!player.Closed && !player.LeavingArea && player.Connection.Connected)
            _ = ClearPresentation();

        async Task ClearPresentation()
        {
            try
            {
                if (wasPaused)
                    await Send(player, InstancePause.State(false),
                        "0x01d3 resumed (the survivors run ended)");
                foreach (ushort row in buffs) ClearSurvivorsHerald(player, row);
                foreach (uint orb in orbs)
                    await global::Game.SendWorldRemoval(player, orb, "survivors retired xp orb");
                foreach (uint zone in zones)
                    await global::Game.SendWorldRemoval(player, zone, "survivors retired death zone");
                if (closeWindow)
                    await Send(player, InstanceNpcs.Talk(0), "survivors retired reward window",
                        still: () => StateOf(player).Survivors is null);
                if (player.Connection.PlayerObjectCreated && StateOf(player).Survivors is null)
                {
                    await SendCharacterStats(player, PlayerStatChange.Build);
                    await SendSkills(player);
                    await SendSkillBar(player);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    internal static void ReleaseAll()
    {
        foreach (var player in Active.Keys) Release(player);
        PendingRuns.Clear();
        SurvivorsCarried.Clear();
    }
}
