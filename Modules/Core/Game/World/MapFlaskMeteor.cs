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
        uint id = player.TakeObjectId();
        var source = new SpawnedMonster(variety.Name, new(spot.X, spot.Y, 0), 0, 0, 1)
        {
            Variety = variety, Level = level, Life = 1, Hostile = true, NoRewards = true, ObjectId = id,
        };
        var owner = CombatLifetime;
        var stopping = owner?.Stopping ?? default;
        bool Active() => CurrentGameWork && ReferenceEquals(owner, CombatLifetime) && !stopping.IsCancellationRequested;
        bool Present(Player p) => p.Connection.PlayerSent && !p.Closed && !p.LeavingArea
            && ReferenceEquals(p.Instance, instance) && ReferenceEquals(p.Area, area);
        IEnumerable<Player> Viewers() => InGame.Keys.Append(player).Where(Present).Distinct();
        _ = WarnAndStrike();

        async Task WarnAndStrike()
        {
            bool warned = false;
            try
            {
                var visual = new EffectObject(spot.X, spot.Y, FlaskMeteorArt);
                warned = await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id,
                    InstanceEntities.ObjectTypeHash(EffectObject.TypePath), visual.Blob()),
                    $"[map-content] flask meteor warning at ({spot.X}, {spot.Y})",
                    still: () => Active() && Present(player), wake: true,
                    afterCreation: () => [InstanceProtocol.AnimationUpdate(id, 0, 0, 1)]);
                if (!warned || !Active() || Viewers().FirstOrDefault() is not { } clock) return;
                await AreaDelay(clock, FlaskMeteorContactSeconds, stopping);
                if (!Active()) return;
                foreach (var target in Viewers().ToArray())
                    await ArenaHazards.Hit(target, id, source, attack, at => ArenaHazards.Distance(at, spot) <= FlaskMeteorRadius,
                        () => Active() && Present(target), minionFactor);
                if (Viewers().FirstOrDefault() is { } viewer) await AreaDelay(viewer, afterContact, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
            finally
            {
                if (warned)
                {
                    try
                    {
                        if (Viewers().FirstOrDefault() is { } viewer)
                            await SendWorldRemoval(viewer, id, "[map-content] flask meteor ends", still: () => Present(viewer));
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
                    finally { area.Objects.RecordDirect(id, InstanceProtocol.RemoveObject(id), AreaDelta.Kind.Remove, player); }
                }
            }
        }
    }
}
