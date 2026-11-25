using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort CorpseWalkBuff = 1623;
    static readonly RuleTable<Player, CorpseWalkState> CorpseWalks = new();
    static readonly RuleTable<Player, Queue<uint>> WalkingCorpses = new();

    sealed class CorpseWalkState(WornGrantedSkills.Grant grant, Player player, double now)
    {
        public readonly WornGrantedSkills.Grant Grant = grant;
        public readonly PlayerWalk Walk = player.Walk;
        public readonly int Epoch = player.ProjectileEpoch;
        public (double Moving, int Teleports) Observed = player.Walk.ObserveTravel(now);
        public double Accrued;

        public bool Matches(Player owner, WornGrantedSkills.Grant source) =>
            source == Grant && ReferenceEquals(owner.Walk, Walk) && owner.ProjectileEpoch == Epoch;
    }

    static async Task TickCorpseWalk(Player player, WornGrantedSkills.Grant source, double now)
    {
        MonsterCatalog.Variety? variety;
        CorpseWalkState? state;
        (int X, int Y) position;
        byte level;
        bool spectral;
        var lifetime = CombatLifetime;
        using (ProjectileMutation(player, advance: false))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return;
            CorpseWalks.TryGetValue(player, out state);
            bool hideout = HideoutCatalog.ByArea(player.Layout.Hash) is not null;
            if (source.Host == 0 || IsInactive(player, source.Host) || player.PlayerLife == 0 || InTown(player) && !hideout)
            {
                if (state is not null)
                {
                    CorpseWalks.Remove(player);
                    player.Area.Buffs.Clear(player.Id, CorpseWalkBuff, player.Id);
                }
                return;
            }
            if (state is null || !state.Matches(player, source))
            {
                CorpseWalks.AddOrUpdate(player, new(source, player, now));
                player.Area.Buffs.Set(player.Id, new(CorpseWalkBuff, player.Id));
                return;
            }
            var observation = state.Walk.ObserveTravel(now);
            state.Accrued = observation.Teleports != state.Observed.Teleports ? 0
                : state.Accrued + Math.Max(0, observation.Moving - state.Observed.Moving);
            state.Observed = observation;
            if (state.Accrued < 1) return;
            state.Accrued %= 1;
            var random = AreaRng(player);
            variety = WalkingSpectreCorpse(player, random, hideout);
            spectral = variety is not null;
            if (!spectral)
            {
                var candidates = player.Area.Monsters.Values
                    .Where(m => m is { Hostile: true, CreatedAsCorpse: false, Rarity: not MonsterRarity.Unique }
                        && m.Variety is { Supported: true } v && v.LifeData.IsUsableAsCorpse(0)
                        && !ItemisedCorpses.Bodies.Contains(v.Row))
                    .Select(m => m.Variety!).DistinctBy(v => v.Row).ToArray();
                variety = candidates.Length > 0 ? candidates[random.Next(candidates.Length)]
                    : Monsters.Find("Metadata/Monsters/Zombies/Zombie");
            }
            position = state.Walk.Position(now);
            var stats = SkillsFor(player).EffectStats(source.Effect, source.Level);
            int levelCap = stats.FirstOrDefault(stat => stat.Item1 == Stat.DesecrateCorpseLevel).Item2;
            level = (byte)Math.Clamp(Math.Min(levelCap, Packs.For(player.Layout.Id)?.AreaLevel ?? 1), 1, 100);
        }
        if (variety is not { Supported: true }) return;
        await SpawnSkillCorpse(player, variety, level, position, WalkingCorpses.GetValue(player, static _ => new()), 10,
            () => CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime) && player.PlayerLife > 0
                && CorpseWalks.TryGetValue(player, out var active) && ReferenceEquals(active, state)
                && state.Matches(player, source) && !IsInactive(player, source.Host)
                && AllWornGrantedSkillsOf(player).Contains(source), MonsterPlacement.Established, spectral);
    }

    static MonsterCatalog.Variety? WalkingSpectreCorpse(Player player, Random random, bool hideout)
    {
        if (!GemSkillEffects(player).Any(g => SkillsFor(player).EffectId(g.Effect) == "RaiseSpectre"
                && SupportedSkill(player, g.Id).RequirementsMet)) return null;
        EnsureSpectreBankLoaded(player);
        // The updated 3.25 rules use 15% in combat areas and always pick a recent Spectre in a hideout.
        if (!hideout && !RandomRolls.Chance(15, 100, random)) return null;
        var pool = player.Summons.SpectreBank.Take(10).Select(Monsters.Find)
            .OfType<MonsterCatalog.Variety>().Where(v => v.Supported && v.LifeData.IsUsableAsCorpse(0)
                && !ItemisedCorpses.Bodies.Contains(v.Row)).ToArray();
        return pool.Length > 0 ? pool[random.Next(pool.Length)] : null;
    }
}
