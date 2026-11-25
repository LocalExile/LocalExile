// Focused Ballista: the support whose stat says a ranged attack totem only attacks when its owner attacks
// (GrantedEffects row 12028 SupportFocusedBallista -> its stat set's ImplicitStats [14486]; Stats row 14486
// ranged_attack_totem_only_attacks_when_owner_attacks). The stat gives no duration and no target, so it says WHEN the
// totem fires, not what it fires at: each attack the owner uses is spent at the totem's next due tick, and the totem
// then shoots the target an ordinary ballista would pick (PickSpellTotemTarget), no faster than its own attack time.
public static partial class Game
{
    /// <summary>The stat Focused Ballista grants: a ranged attack totem only attacks when its owner attacks.</summary>
    public const uint FocusedBallistaStat = Stat.RangedAttackTotemOnlyAttacksWhenOwnerAttacks;

    /// <summary>Whether this skill's ballista totems wait for their owner's attacks: the support's stat is on the
    /// supported skill's stats, so this is one stat read.</summary>
    public static bool FocusedBallista(Player player, ushort skill) =>
        GolemStat(SupportedSkill(player, skill).Stats, FocusedBallistaStat) != 0;

    // How many attacks the owner has used, one count per player, and how many of them each focused totem has already
    // spent, one count per totem. An attack made while a totem cools down waits for its next due tick, and two made
    // during one cooldown buy one volley, not two. Clearing the count starts a new epoch, which the credits follow.
    // Only a totem placed with the support has a credit, so the totem tick reads no stats. Both are rule tables: they
    // are emptied when the module unloads.
    sealed class OwnerAttackCount { public long Count; public int Epoch; }
    static readonly RuleTable<Player, OwnerAttackCount> OwnerAttackCounts = new();
    sealed class TotemAttackCredit { public long Served; public int Epoch; }
    static readonly RuleTable<TotemState.Totem, TotemAttackCredit> TotemAttackCredits = new();

    static (long Count, int Epoch) OwnerAttacks(Player player)
    {
        if (!OwnerAttackCounts.TryGetValue(player, out var count)) return (0, 0);
        lock (count) return (count.Count, count.Epoch);
    }

    /// <summary>Record that the owner used an attack. Called where a use is admitted (the default attack's swing
    /// and an attack gem's use, on a monster or on the ground), not at the press, so a use refused for mana, a cooldown,
    /// the weapon or reach arms nothing. A triggered skill does not come this way, and neither does a trap or mine
    /// being placed or a totem being planted. A non-lockstep default attack at the ground is a Move, not an attack.</summary>
    public static void NotePlayerAttack(Player player)
    {
        var count = OwnerAttackCounts.GetOrCreateValue(player);
        lock (count) count.Count++;
    }

    /// <summary>An attack gem's admitted use is the owner attacking; planting a totem is not.</summary>
    static void NoteGemAttackUse(Player player, ushort skill, int effect)
    {
        if (SkillsFor(player).IsAttack(effect) && !IsTotemSkill(player, skill)) NotePlayerAttack(player);
    }

    /// <summary>A focused totem starts with the attacks the owner used before it was placed already spent.</summary>
    static void BindFocusedBallista(Player player, TotemState.Totem totem)
    {
        if (!totem.Ballista || !FocusedBallista(player, totem.SkillId)) return;
        var (count, epoch) = OwnerAttacks(player);
        TotemAttackCredits.AddOrUpdate(totem, new() { Served = count, Epoch = epoch });
    }

    /// <summary>The totem is due to attack: whether it may. A focused totem needs an owner attack it has not spent
    /// yet, and spends it here whether or not it then finds a target, so an attack never waits for a monster to come
    /// into range. Any other totem always may.</summary>
    static bool FocusedBallistaMayAttack(Player player, TotemState.Totem totem)
    {
        if (!totem.Ballista || !TotemAttackCredits.TryGetValue(totem, out var credit)) return true;
        var (count, epoch) = OwnerAttacks(player);
        if (credit.Epoch != epoch) { credit.Epoch = epoch; credit.Served = 0; }
        if (count <= credit.Served) return false;
        credit.Served = count;
        return true;
    }

    /// <summary>Forget the owner's attacks (death, revival, leaving the area): none of them counts afterwards.</summary>
    public static void ClearOwnerAttacks(Player player)
    {
        if (!OwnerAttackCounts.TryGetValue(player, out var count)) return;
        lock (count) { count.Count = 0; count.Epoch++; }
    }
}
