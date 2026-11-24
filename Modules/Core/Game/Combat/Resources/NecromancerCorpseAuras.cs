public static partial class Game
{
    // Both Necromancer notables check six metres around the caster and count a maximum of ten corpses.
    const int NecromancerCorpseRadius = 60, NecromancerCorpseLimit = 10;

    sealed class CorpseAuraSample
    {
        public GameArea? Area;
        public double At = double.NegativeInfinity;
        public (int X, int Y) Position;
        public int Count;
    }

    static readonly RuleTable<Player, CorpseAuraSample> CorpseAuraSamples = new();

    static int NearbyNecromancerCorpses(Player caster, (int X, int Y) position, double now)
    {
        var sample = CorpseAuraSamples.GetOrCreateValue(caster);
        if (sample.Area == caster.Area && sample.At == now && sample.Position == position) return sample.Count;
        // Corpses stop blocking movement and can be absent from the living-monster grid.
        int count = 0;
        foreach (var corpse in caster.Area.Monsters.Values)
            if (IsCorpse(corpse) && !corpse.Ailments.Shattered
                && Dist(corpse.Walk.Position(now), position) <= NecromancerCorpseRadius && ++count == NecromancerCorpseLimit)
                break;
        sample.Area = caster.Area;
        sample.At = now;
        sample.Position = position;
        return sample.Count = count;
    }

    static void AddMinionNecromancerCorpseAuras(Player owner, GolemState.Golem minion,
        Dictionary<uint, int> received, double now)
    {
        var position = minion.Monster.Walk.Position(now);
        int damage = 0, energyShield = 0, mana = 0;
        Apply(owner);
        foreach (var peer in owner.Instance.PlayersExcept(owner.Id)) Apply(peer);
        Add(Stat.DamagePlusPctFinalFromNecromancerAura, damage);
        Add(Stat.BaseEnergyShieldRegenerationRatePerMinutePct, energyShield);
        Add(Stat.BaseManaRegenerationRatePerMinute, mana);

        void Apply(Player caster)
        {
            if (caster.Closed || caster.LeavingArea || caster.PlayerLife == 0 || InTown(caster)) return;
            var from = caster.Walk.Position(now);
            if (Dist(from, position) > NecromancerCorpseRadius) return;
            var stats = StatSourceValues(caster);
            int more = stats.GetValueOrDefault(Stat.NecromancerDamagePlusPctFinalForYouAndAlliesWithNearbyCorpse);
            int perCorpse = stats.GetValueOrDefault(Stat.DamagePlusPctFinalToYouAndNearbyAlliesPerNearbyCorpsesUpTo10Pct);
            int shield = stats.GetValueOrDefault(Stat.NecromancerEnergyShieldRegenerationRatePerMinutePctForYouAndAlliesPerNearbyCorpse);
            int manaRate = stats.GetValueOrDefault(Stat.NecromancerManaRegenerationRatePerMinuteForYouAndAlliesPerNearbyCorpse);
            if (more == 0 && perCorpse == 0 && shield == 0 && manaRate == 0) return;
            int count = NearbyNecromancerCorpses(caster, from, now);
            if (count == 0) return;
            damage = Math.Max(damage, Math.Max(more, (int)Math.Clamp((long)perCorpse * count, 0, 10)));
            energyShield = Math.Max(energyShield, ActorScale.Saturate((long)shield * count));
            mana = Math.Max(mana, ActorScale.Saturate((long)manaRate * count));
        }

        void Add(uint stat, int value)
        {
            if (value > 0) received[stat] = ActorScale.Saturate((long)received.GetValueOrDefault(stat) + value);
        }
    }

    static double MinionNecromancerCorpseDamage(MonsterModEffects effects) =>
        Math.Max(0, 1 + effects[Stat.DamagePlusPctFinalFromNecromancerAura] / 100d);
}