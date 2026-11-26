using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    static bool IsDesecrateEffect(string? effect) => effect is "Desecrate" or "DesecrateRoyale";

    public static async Task<bool> Desecrate(Player player,
        ushort source, (int X, int Y) aim, Func<bool> current, bool triggered = false)
    {
        var gem = SupportedSkill(player, source);
        double release = AreaTime(player) + (triggered ? 0 : (CastMs(player, gem.Effect, source) ?? 1000) / 1000d);
        var lifetime = CombatLifetime;
        bool Current() => CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime) && current()
            && !player.Closed && !player.LeavingArea && player.PlayerLife > 0;
        while (AreaTime(player) < release || player.Paused)
        {
            if (!Current()) return false;
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        using (ProjectileMutation(player))
        {
            if (!Current() || !SameTriggerSource(gem, SupportedSkill(player, source))) return false;
            ProjectilesFor(player).PlaceDesecratedGround(aim, gem, triggered ? 1 : GhorrDotMore(player, source));
        }
        EnsureSpectreBankLoaded(player);
        var area = player.Area.Monsters.Values.Where(m => m.Variety is { Supported: true } && m.Variety.LifeData.IsUsableAsCorpse(0)
            && m.Rarity != MonsterRarity.Unique && (m.Hostile || player.Summons.Fighters.Values.Any(g => g.Monster == m && g.Definition.Kind == "Spectre")))
            .Select(m => m.Variety!);
        var bank = player.Summons.SpectreBank.Select(id => Monsters.Find(id))
            .OfType<MonsterCatalog.Variety>()
            .Where(v => v.Supported && v.LifeData.IsUsableAsCorpse(0));
        MonsterCatalog.Variety[] pool = [.. area.Concat(bank).Where(v => !ItemisedCorpses.Bodies.Contains(v.Row))
            .DistinctBy(v => v.Row)];
        if (pool.Length == 0 && Monsters.Find("Metadata/Monsters/Zombies/Zombie") is { Supported: true } fallback) pool = [fallback];
        if (pool.Length == 0) return false;
        int count = Math.Max(1, GolemStat(gem.Stats, Stat.DesecrateNumberOfCorpsesToCreate));
        int maximum = Math.Max(count, GolemStat(gem.Stats, Stat.DesecrateMaximumNumberOfCorpses));
        byte level = (byte)Math.Clamp(Math.Min(GolemStat(gem.Stats, Stat.DesecrateCorpseLevel), Packs.For(player.Layout.Id)?.AreaLevel ?? 1), 1, 100);
        for (int i = 0; i < count; i++)
        {
            double angle = Math.Tau * i / count;
            var at = (aim.X + (int)(Math.Cos(angle) * 6), aim.Y + (int)(Math.Sin(angle) * 6));
            if (!player.Layout.WalkableFor(at.Item1, at.Item2, 0)) at = aim;
            await SpawnSkillCorpse(player, pool[AreaRng(player).Next(pool.Length)], level, at, player.Summons.Corpses,
                maximum, Current);
        }
        return true;
    }

    public sealed partial class ProjectileSession
    {
        internal void PlaceDesecratedGround((int X, int Y) aim, SupportedGem gem, double ghorrMore)
        {
            var caster = CharacterPlayerStats(Player);
            int Own(uint stat) => gem.Stats.GetValueOrDefault(stat);
            int Caster(uint stat) => caster.GetValueOrDefault(stat);
            int Get(uint stat) => ActorScale.Saturate((long)Own(stat) + Caster(stat));
            if (DamageStats.Prohibited(DamageType.Chaos, Get)) return;
            double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.ChaosDamagePlusPct) + Get(Stat.AreaDamagePlusPct)
                + Get(Stat.DamageOverTimePlusPct) + Get(Stat.ChaosDamageOverTimePlusPct)
                + Get(Stat.DamageOverTimePlusPctWithSpells) + Get(Stat.DesecrateDamagePlusPct);
            double more = ghorrMore * Math.Max(0, 1 + Own(Stat.SupportAreaConcentrateAreaDamagePlusPctFinal) / 100d)
                * Math.Max(0, 1 + Own(Stat.SupportVoidManipulationChaosDamagePlusPctFinal) / 100d)
                * Math.Max(0, 1 + Own(Stat.SupportRapidDecayDamageOverTimePlusPctFinal) / 100d)
                * Math.Max(0, 1 + Own(Stat.SupportEfficacyDamageOverTimePlusPctFinal) / 100d)
                * Math.Max(0, 1 + Own(Stat.CrueltyDamageOverTimePlusPctFinal) / 100d);
            double multiplier = Math.Max(0, 1 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.ChaosDotMultiplierPlus)
                + Get(Stat.DamageOverTimeMultiplierPlusWithSpells)) / 100d);
            double dps = Own(Stat.BaseChaosDamageToDealPerMinute) / 60d * Math.Max(0, 1 + increased / 100d) * more * multiplier;
            double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Caster, Own);
            var damage = new SkillDamageContext(SkillHitKind.Spell, caster).OverTime(new(DamageType.Chaos, dps, seconds));
            int radius = SkillRadius(Player, 12, gem.Stats, spell: true, gem.Effect, caster);
            if (damage.DamagePerSecond > 0 && seconds > 0 && radius > 0)
                AddDamageGround(Vector(aim), radius, seconds, DamageType.Chaos, damage.DamagePerSecond,
                    Runtime.Now, desecrated: true);
        }
    }

    static Task<uint> SpawnSkillCorpse(Player player, MonsterCatalog.Variety variety, byte level, (int X, int Y) at,
        Queue<uint> corpses, int maximum, Func<bool> current, MonsterPlacement placement = MonsterPlacement.Proposed,
        bool spectral = false) =>
        SpawnMonster(player, variety, level, MonsterRarity.Normal, at.X, at.Y,
            damagedLife: 1, mods: spectral ? SpectralCorpseStats : MonsterModSet.None, unrisen: false,
            extraStats: spectral ? [(Stat.IsSpectreCorpse, 1)] : null, ownedInitialDeath: true, placement: placement,
            ownedCurrent: current, owned: (id, corpse) =>
            {
                corpse.CreatedAsCorpse = corpse.Dead = true;
                corpse.Life = 0;
                corpse.NoRewards = true;
                corpses.Enqueue(id);
                while (corpses.Count > maximum)
                {
                    uint expired = corpses.Dequeue();
                    if (player.Area.Monsters.Remove(expired))
                        player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(expired),
                            "old skill corpse removed", WorldId: expired, RemovesObject: true));
                    player.Instance.World.MonsterOrigins.TryRemove(expired, out _);
                }
            });
}
