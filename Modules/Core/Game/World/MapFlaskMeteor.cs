using System.Buffers.Binary;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string FlaskMeteorSkill = "GAMeteorOnFlaskUse";
    const string FlaskMeteorDaemon = "Metadata/Monsters/Daemon/PrimordialAltars/AltarDownsideDaemonPlayerMeteorOnFlaskUse";
    const string FlaskMeteorArt = "Metadata/Effects/Spells/monsters_effects/AtlasofWorldsBosses/CleansingFodder_Minions/ao/meteor_improved_01.ao";
    // MiscAnimated 6448 starts the warning immediately and its "animate" clip contacts at 1.8208787 s.
    const double FlaskMeteorContactSeconds = 1.821;
    const int FlaskMeteorRadius = 15; // GeometryAttack 918 (behaviour variation 941).

    // Both callers are inside the accepted charge transaction. The ground position belongs to that paid use.
    static void QueueMapFlaskMeteor(Player player, double now)
    {
        if (MapRunOf(player) is not { } map) return;
        var instance = player.Instance;
        int chance = instance.AreaStats(() => AreaLeagueStats(player)).GetValueOrDefault(Stat.MapPlayerCreateEnemyMeteorDaemonOnFlaskUsePctChance);
        if (chance <= 0 || chance < 100 && AreaRng(player).Next(100) >= chance) return;
        var area = player.Area;
        var frames = instance.World.Frames;
        var skills = SkillsFor(player);
        int effect = skills.EffectRow(FlaskMeteorSkill);
        if (effect < 0 || Monsters.Find(FlaskMeteorDaemon) is not { } variety || skills.ActionHash(effect) is not ushort action) return;

        byte level = (byte)Math.Clamp(map.Area.Level > 0 ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);
        var native = BossKits.AttackFor(player, variety, level, effect, (ushort)(action & 0x3fff));
        var conversion = new DamageConversion().AddSkill(DamageType.Physical, DamageType.Fire,
            ArenaHazards.Stat(player, effect, level, Stat.SkillPhysicalDamagePctToConvertToFire));
        var attack = native with { Damage = native.Damage.Convert(conversion), Spell = true, Scripted = true };
        double minionFactor = Math.Max(0, 1 + ArenaHazards.Stat(player, effect, level,
            Stat.MonsterPenaltyAgainstMinionsDamagePlusPctFinalVsPlayerMinions) / 100d);
        double afterContact = Math.Max(0, native.Seconds - FlaskMeteorContactSeconds);
        var spot = player.Walk.Position(now);
        uint id = instance.World.TakeObjectId();
        var source = new SpawnedMonster(variety.Name, new(spot.X, spot.Y, 0), 0, 0, 1)
        {
            Variety = variety, Level = level, Life = 1, Hostile = true, NoRewards = true, ObjectId = id,
        };
        var owner = CombatLifetime;
        var stopping = owner?.Stopping ?? default;
        bool Active() => CurrentGameRules && ReferenceEquals(owner, CombatLifetime) && !stopping.IsCancellationRequested;
        bool Present(Player p) => p.Connection.PlayerSent && !p.Closed && !p.LeavingArea
            && !p.Connection.WorldOutput.Closed && ReferenceEquals(p.Instance, instance) && ReferenceEquals(p.Area, area);
        Player[] Viewers() => [.. (frames.Running ? frames.Players() : instance.PlayersExcept(player.Id).Append(player)).Where(Present)];
        var warning = $"[map-content] flask meteor warning at ({spot.X}, {spot.Y})";
        const string removal = "[map-content] flask meteor ends";
        uint visualType = InstanceEntities.ObjectTypeHash(EffectObject.TypePath);
        if (area.Owned || frames.Running) QueueOnAreaFrames();
        else _ = WarnAndStrikeUnframed();

        async Task Strike(Func<bool> current)
        {
            // Each hit rechecks that the victim still belongs to this instance at the commit gate.
            foreach (var target in Viewers())
                try
                {
                    await ArenaHazards.Hit(target, id, source, attack, at => ArenaHazards.Distance(at, spot) <= FlaskMeteorRadius,
                        () => current() && Present(target), minionFactor);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
        }

        void QueueOnAreaFrames()
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // A frame wait is released when the instance's last connection leaves, even though its area timers stop.
            // The player argument only supplies the wait's ordering within the shared frame.
            var watch = frames.Running ? CancellationTokenSource.CreateLinkedTokenSource(stopping) : null;
            var driverEnd = watch is null ? null : frames.Wait(player, PlayerUpdateRole.MonsterAction, int.MaxValue, watch.Token);
            AreaTimer? timer = null;
            bool Current() => Active() && !ended.Task.IsCompleted && driverEnd?.IsCompleted != true;
            if (driverEnd is null)
                area.Defer(_ => Warn());
            else
            {
                _ = WatchAreaEnd(driverEnd, watch!);
                _ = frames.Run(Warn);
            }

            void Warn()
            {
                if (!area.Owned || !Current()) { Finish(); return; }
                var viewers = Viewers();
                if (viewers.Length == 0) { Finish(); return; }
                var visual = new EffectObject(spot.X, spot.Y, FlaskMeteorArt) { Awake = true };
                var create = InstanceEntities.CreateObject(id, visualType, visual.Blob(), headerU32: uint.MaxValue);
                var clip = InstanceProtocol.AnimationUpdate(id, 0, 0, 1);
                BinaryPrimitives.WriteUInt32BigEndian(clip.AsSpan(6), uint.MaxValue);
                // Retain the effect in this map for joining clients. Address queued packets to viewers in this visit.
                area.Objects.RecordDirect(id, create, AreaDelta.Kind.Create, viewers[0], warning);
                area.Objects.RecordDirect(id, clip, AreaDelta.Kind.World, viewers[0]);
                foreach (var viewer in viewers)
                {
                    area.Delta.Add(id, create, warning, AreaDelta.Kind.Create, only: viewer);
                    area.Delta.Add(id, clip, only: viewer);
                }
                timer = area.Timers.After(area.Frame, FramesUntil(FlaskMeteorContactSeconds), _ => OnImpact(), source: owner);
            }

            static long FramesUntil(double seconds) => Math.Max(1, (long)Math.Ceiling(seconds / FrameSeconds));

            void OnImpact()
            {
                if (!Current() || Viewers().Length == 0) { Finish(); return; }
                timer = area.Timers.After(area.Frame, FramesUntil(afterContact), _ => Finish(), source: owner);
                _ = Strike(Current);
            }

            async Task WatchAreaEnd(Task leaving, CancellationTokenSource wait)
            {
                using (wait)
                {
                    if (await Task.WhenAny(leaving, ended.Task).ConfigureAwait(false) == leaving) Finish();
                    else wait.Cancel();
                }
            }

            void Finish()
            {
                if (!ended.TrySetResult()) return;
                if (area.Driven && !area.Owned) area.Defer(_ => Retire());
                else Retire();
            }

            void Retire()
            {
                timer?.Cancel();
                var bytes = InstanceProtocol.RemoveObject(id);
                if (area.Owned && area.Objects.Has(id) && Viewers().Length > 0)
                    area.Delta.RemoveSharedObject(id, bytes, removal);
                else area.Objects.RecordDirect(id, bytes, AreaDelta.Kind.Remove, player);
            }
        }

        async Task WarnAndStrikeUnframed()
        {
            bool warned = false;
            try
            {
                if (!Active() || Viewers().FirstOrDefault() is not { } recipient) return;
                var visual = new EffectObject(spot.X, spot.Y, FlaskMeteorArt);
                warned = await SendWorldCreate(recipient, id, () => InstanceEntities.CreateObject(id, visualType, visual.Blob()), warning,
                    still: () => Active() && Present(recipient), wake: true,
                    afterCreation: () => [InstanceProtocol.AnimationUpdate(id, 0, 0, 1)]);
                if (!warned || !Active() || Viewers().FirstOrDefault() is not { } clock) return;
                await AreaDelay(clock, FlaskMeteorContactSeconds, stopping);
                if (!Active()) return;
                await Strike(Active);
                if (Viewers().FirstOrDefault() is { } viewer) await AreaDelay(viewer, afterContact, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
            finally
            {
                if (warned)
                {
                    try
                    {
                        if (Viewers().FirstOrDefault() is { } viewer)
                            await SendWorldRemoval(viewer, id, removal, still: () => Present(viewer));
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
                    finally { area.Objects.RecordDirect(id, InstanceProtocol.RemoveObject(id), AreaDelta.Kind.Remove, player); }
                }
            }
        }
    }
}
