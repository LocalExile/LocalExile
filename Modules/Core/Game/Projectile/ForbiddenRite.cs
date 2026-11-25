using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string ForbiddenRitePrimary = "Metadata/Projectiles/SoulfeastPrimaryMortar";
    const string ForbiddenRiteSecondary = "Metadata/Projectiles/SoulfeastSecondaryMortar";

    internal readonly record struct ForbiddenRiteCast(int SecondaryLimit, int Backlash, TotemState.Totem? Totem, int ArtVariation);

    static (uint Life, uint Shield) ForbiddenRitePools(Player player, uint? totemLife, bool placed)
    {
        if (placed) return (1, 0);
        if (totemLife is uint life) return (life, 0);
        return (player.PlayerMaxLife, player.Pools.EnergyShield);
    }

    static ForbiddenRiteCast CaptureForbiddenRite(Player player, SupportedGem gem, TotemState.Totem? totem,
        Func<uint, int> get, int variation)
    {
        var placement = PlacedKind(gem);
        bool placed = placement.Trap || placement.Mine;
        var (life, shield) = ForbiddenRitePools(player, totem?.MaxLife, placed);
        int backlash = placed ? 0 : (int)Math.Clamp((life * (double)get(Stat.SoulfeastTakePctMaximumLifeAsChaosDamage)
            + shield * (double)get(Stat.SoulfeastTakePctMaximumEnergyShieldAsChaosDamage)) / 100d, 0, int.MaxValue);
        return new(Math.Clamp(get(Stat.SoulfeastNumberOfSecondaryProjectiles), 0, 256), backlash, totem, variation);
    }

    public sealed partial class ProjectileSession
    {
        void ReleaseForbiddenRite(ProjectileUse primary, ProjectileOffense shot, ForbiddenRiteCast rite)
        {
            double now = Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            if (rite.SecondaryLimit > 0)
            {
                var origin = primary.Origin;
                // Use the same bounded flight search as other targeted volleys. The exported stat caps targets.
                double range = primary.Profile.Range;
                var targets = Player.Area.MonstersNear((origin.X, origin.Y), range)
                    .Where(entry => TotemTargetable(entry.Value) && !entry.Value.WorldHidden)
                    .Select(entry => (entry.Key, At: Vector(entry.Value.Walk.Position(now))))
                    .Select(entry => (entry.Key, entry.At, Distance: (entry.At - origin).Length))
                    .Where(entry => entry.Distance <= range)
                    .OrderBy(entry => entry.Distance).ThenBy(entry => entry.Key)
                    .Take(rite.SecondaryLimit).Select(entry => entry.At).ToArray();
                if (targets.Length != 0)
                {
                    string metadata = Art.Projectile(ForbiddenRiteSecondary, rite.ArtVariation);
                    var profile = primary.Profile with
                    {
                        Id = metadata, Count = targets.Length, NativeVolley = null, ParallelOrigins = null, ReturnRolls = null
                    };
                    var presentation = shot.ClientPredicted ? shot.Presentation
                        : WithProjectileClips(null, SkillsFor(Player).Clips(metadata), range / profile.Speed);
                    Runtime.Release(primary with { Profile = profile, ProjectileTargets = targets,
                        Payload = shot with { ForbiddenRite = null, Presentation = presentation } });
                }
            }

            if (rite.Backlash <= 0) return;
            if (rite.Totem is { } totem)
            {
                if (!totem.Alive(now)) return;
                var stats = CharacterPlayerStats(Player);
                int resistance = (Monsters.Find(totem.VarietyId) is { } variety
                    ? Monsters.BaseResistance(variety, totem.Level, DamageType.Chaos) ?? 0 : 0)
                    + stats.GetValueOrDefault(Stat.TotemChaosResistancePct);
                int mitigated = DamageModel.Mitigate(rite.Backlash, resistance);
                RedirectTotemHitLocked(Player, totem, 0, mitigated, 100, now, stats);
                return;
            }

            uint life = Player.PlayerLife, mana = Player.PlayerMana, shield = Player.EnergyShield.Current;
            ApplySelfHitLocked(Player, DamagePacket.Of(DamageType.Chaos, rite.Backlash, rite.Backlash), now);
            Player.Frames.Area?.Life.Changed(Player);
            if (life > 0 && Player.PlayerLife == 0)
            {
                int epoch = Player.IncomingHitEpoch;
                Enqueue(() => PlayerDiedAtEpoch(Player, "death from Forbidden Rite", epoch));
            }
            else if (Player.Frames.Area is null
                && (life != Player.PlayerLife || mana != Player.PlayerMana || shield != Player.EnergyShield.Current))
            {
                int epoch = Player.ProjectileEpoch;
                Enqueue(async () =>
                {
                    if (!Player.ProjectileActive(epoch)) return;
                    if (shield != Player.EnergyShield.Current)
                        await SendPlayerEnergyShield(Player);
                    if (life != Player.PlayerLife)
                        await SendWorldRecord(Player, Player.Id, PlayerLifeUpdate(Player), "life after Forbidden Rite");
                    await SendPlayerMana(Player, "resources after Forbidden Rite");
                });
            }
        }
    }
}
