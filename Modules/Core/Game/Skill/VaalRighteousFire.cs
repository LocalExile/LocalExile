using GameServer.Core.World;

public static partial class Game
{
    const string VaalRighteousFireEffect = "VaalRighteousFire";
    const ushort VaalRighteousFireBuff = 1160;
    const ushort VaalRighteousFireBurn = 1148;
    const string VaalRighteousFireRing = "Metadata/Effects/Spells/vaal_skills/ri_fire/vaal_ri_fire.ao";

    internal readonly record struct RighteousFireSacrifice(uint Shield, uint Life)
    {
        public ulong Total => (ulong)Shield + Life;
    }

    static RighteousFireSacrifice PlanRighteousFireSacrifice(Player player, uint lifeCost, int percent)
    {
        uint life = player.PlayerLife - Math.Min(player.PlayerLife, lifeCost);
        uint shield = player.EnergyShield.Current;
        ulong sacrifice = ((ulong)life + shield) * (uint)Math.Clamp(percent, 0, 100) / 100;
        uint shieldLost = (uint)Math.Min(shield, sacrifice);
        uint lifeLost = life > 0 ? (uint)Math.Min(life - 1, sacrifice - shieldLost) : 0;
        return new(shieldLost, lifeLost);
    }

    sealed class VaalRighteousFireState(GameArea area, int epoch, double until, int spellMore)
    {
        public readonly GameArea Area = area;
        public readonly int Epoch = epoch;
        public readonly double Until = until;
        public readonly int SpellMore = spellMore;
        public DamageAura? Aura;
        public Action? RemoveRing;
    }

    static readonly RuleTable<Player, VaalRighteousFireState> VaalRighteousFires = new();

    static bool VaalRighteousFireCurrent(Player player, VaalRighteousFireState state, double now) =>
        !player.Closed && !player.LeavingArea && player.PendingTransfer is null && player.PlayerLife > 0
        && state.Epoch == player.ProjectileEpoch && now < state.Until;

    static void AddVaalRighteousFireStats(Player player, Dictionary<uint, int> stats)
    {
        if (!VaalRighteousFires.TryGetValue(player, out var state)) return;
        double now = AreaTime(player);
        if (!VaalRighteousFireCurrent(player, state, now)) return;
        StatsChangeIn(state.Until - now);
        uint key = Stat.VaalRighteousFireSpellDamagePlusPctFinal;
        stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + state.SpellMore);
    }

    static void StartVaalRighteousFire(Player player, VaalUseReservation paid)
    {
        if (paid.FireSacrifice is not { } sacrifice) return;
        using var mutation = ProjectileMutation(player);
        if (!VaalAdmissionCurrent(player, paid) || !PlayerActionCurrent(player, paid.Action)) return;

        var gem = SupportedSkill(player, paid.Skill);
        var stats = CharacterPlayerStats(player);
        int Own(uint key) => gem.Stats.GetValueOrDefault(key);
        int PlayerStat(uint key) => stats.GetValueOrDefault(key);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), PlayerStat, Own,
            DurationKinds(player, gem.Effect)) / RecipientEffectTime.Rate(1, PlayerStat);
        if (seconds <= 0) return;

        EndVaalRighteousFire(player);
        var state = new VaalRighteousFireState(player.Area, player.ProjectileEpoch, AreaTime(player) + seconds,
            Own(Stat.VaalRighteousFireSpellDamagePlusPctFinal));
        VaalRighteousFires.AddOrUpdate(player, state);
        state.Area.Buffs.Set(player.Id, new(VaalRighteousFireBuff, player.Id, paid.Skill,
            Values: [unchecked((uint)state.SpellMore)], U68: Buff.Seconds(seconds)));
        MarkPlayerStats(player, PlayerStatStep.Sources);

        int radius = RighteousFireRadius(player, gem.Stats);
        ProjectilesFor(player).PresentVaalRighteousFireRing(radius, remove => state.RemoveRing = remove);
        double baseDps = sacrifice.Total * (Math.Max(0, Own(Stat.VaalRighteousFireLifeAndEsPctAsDamagePerSecond)) / 100d);
        bool Current() => VaalRighteousFireCurrent(player, state, AreaTime(player))
            && VaalRighteousFires.TryGetValue(player, out var current) && ReferenceEquals(current, state);
        state.Aura = new DamageAura
        {
            Name = "Vaal Righteous Fire",
            Radius = () => radius,
            BuffRow = VaalRighteousFireBurn,
            Skill = paid.Skill,
            Marks = [],
            Running = Current,
            Valid = Current,
            Active = () => !InGracePeriod(player),
            Damage = interval =>
            {
                using var scope = new PlayerStatScope(player, fresh: true);
                var live = new Dictionary<uint, int>(CharacterPlayerStats(player));
                double dps = ScaleRighteousFireDps(baseDps, live, gem.Stats) * paid.PactDamageMore;
                var damage = new DamageOverTime(DamageType.Fire, dps, interval);
                return (new SkillDamageContext(SkillHitKind.Spell, live).OverTime(damage), live);
            },
        };
        StartDamageAura(player, state.Aura);
        AfterSeconds(player, seconds, () => EndVaalRighteousFire(player, state));
    }

    static void EndVaalRighteousFire(Player player, VaalRighteousFireState? expected = null)
    {
        if (!VaalRighteousFires.TryGetValue(player, out var state)
            || expected is not null && !ReferenceEquals(state, expected)) return;
        lock (state)
        {
            if (!VaalRighteousFires.TryGetValue(player, out var current) || !ReferenceEquals(current, state)) return;
            VaalRighteousFires.Remove(player);
            state.Area.Buffs.Clear(player.Id, VaalRighteousFireBuff, player.Id);
            MarkPlayerStats(player, PlayerStatStep.Sources);
        }
        void Remove(GameArea area)
        {
            if (state.Aura is { } aura)
            {
                foreach (uint monster in aura.Marks.Keys)
                    area.Buffs.Clear(monster, VaalRighteousFireBurn, player.Id);
                foreach (var (monster, _) in aura.Left)
                    area.Buffs.Clear(monster, VaalRighteousFireBurn, player.Id);
            }
            state.RemoveRing?.Invoke();
        }
        if (state.Area.Owned) Remove(state.Area);
        else state.Area.Defer(Remove);
    }

    public sealed partial class ProjectileSession
    {
        internal void PresentVaalRighteousFireRing(int radius, Action<Action> captureRemoval)
        {
            var at = Player.Walk.Position(AreaTime(Player));
            Effect(at.X, at.Y, VaalRighteousFireRing, lifetime: null, radius: radius, parent: Player.Id,
                artRadius: 500, captureRemoval: captureRemoval);
        }
    }
}
