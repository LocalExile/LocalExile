using GameServer.Core.Accounts;
using GameServer.Core.World;

internal readonly record struct LacerateWaves((int X, int Y) Origin, double X, double Y, int Radius, int Angle, double HalfAngleCos)
{
    internal const int BaseRadius = 38;
    static readonly (double Sin, double Cos) WaveOffset = Math.SinCos(20 * Math.PI / 180);

    internal static LacerateWaves At((int X, int Y) from, (int X, int Y) aim, int radius, int angle)
    {
        var (x, y) = from == aim ? (0d, -1d) : GroundSlamWave.Direction(from, aim);
        // The native scan backs up by the player's one-cell body size, truncated toward zero per axis.
        var origin = (from.X - (int)x, from.Y - (int)y);
        return new(origin, x, y, radius, angle, Math.Cos(angle * Math.PI / 360));
    }

    internal bool Covers(int phase, (int X, int Y) at, int body)
    {
        if (Radius <= 0) return false;
        double x = at.X - Origin.X, y = at.Y - Origin.Y;
        double range = Radius + Math.Max(0, body);
        double squared = x * x + y * y;
        if (squared > range * range) return false;
        double forward = x * X + y * Y;
        double across = -x * Y + y * X;
        double alongWave = forward * WaveOffset.Cos + across * (phase == 0 ? WaveOffset.Sin : -WaveOffset.Sin);
        // The client extends the radial limit by the target's size but tests its centre against the angle.
        return alongWave >= Math.Sqrt(squared) * HalfAngleCos;
    }
}

public static partial class Game
{
    internal static bool LacerateEffect(string? effect) =>
        effect is "Lacerate" or "LacerateAltX" or "LacerateAltY" or "LacerateRoyale";

    static void ApplyStanceMeleeBleed(Player player, int effect, Dictionary<uint, int> own)
    {
        int chance = own.GetValueOrDefault(Stat.ChanceToBleedOnHitPctChanceInBloodStance);
        int more = own.GetValueOrDefault(Stat.ActiveSkillBleedingDamagePlusPctFinalInBloodStance);
        if ((chance == 0 && more == 0) || player.Stance == StanceCatalog.SandSelection) return;
        string? id = SkillsFor(player).EffectId(effect);
        if (!LacerateEffect(id) && !IsBladestorm(id)) return;

        const uint hitChance = Stat.BleedOnHitWithAttacksPct;
        const uint damage = Stat.ActiveSkillBleedingDamagePlusPctFinal;
        own[hitChance] = ActorScale.Add(hitChance, own.GetValueOrDefault(hitChance), chance);
        if (more != 0)
            own[damage] = ActorScale.Saturate((100L + own.GetValueOrDefault(damage)) * (100L + more) / 100 - 100);
    }

    internal sealed class LacerateUse(LacerateWaves waves, AttackHand hands, SupportedGem source,
        int interruption, double start, double duration, double first, double second, string? clip)
    {
        internal LacerateWaves Waves { get; } = waves;
        internal SupportedGem Source { get; } = source;
        internal int Phase { get; private set; }
        internal string? Clip { get; } = clip;
        internal double Fraction => Phase == 0 ? first : second;
        internal double DueAt => start + duration * Fraction;
        internal AttackHand Hand => hands == AttackHand.Both ? (Phase == 0 ? AttackHand.Main : AttackHand.Off) : hands;
        internal bool FortificationGranted;
        internal bool RageGranted;

        internal bool Current(Player player) => player.ActionInterruptionGeneration == interruption;

        internal bool TryAdvance()
        {
            if (Phase != 0) return false;
            Phase = 1;
            return true;
        }
    }

    static LacerateUse StartLacerate(Player player, SupportedGem gem, ushort skill,
        (int X, int Y) from, (int X, int Y) aim, AttackHand hands, double start, double radiusMore)
    {
        int variation = SkillsFor(player).BehaviourVariation(gem.Effect);
        bool wide = variation == 2 || variation == 1 && player.Stance == StanceCatalog.SandSelection;
        int angle = wide ? (int)Math.Clamp((100L + gem.Stats.GetValueOrDefault(Stat.SkillAnglePlusPctInSandStance)) * 60 / 100, 0, 360)
            : 60;
        var area = SkillAreaStats(player, gem.Stats, gem.Effect);
        int radius = (int)(SkillArea.Radius(LacerateWaves.BaseRadius, area.GetValueOrDefault, spell: false) * radiusMore);
        var actor = SkillsFor(player).ClassFor(player.Character.ClassRow);
        var weapon = AnimationWeaponProfileFor(player);
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? actor.AnimatedObject;
        string name = LacerateClip(player, weapon, (CharacterClass)actor.IntegerId, hands);
        var first = Animations.SkillAttackContactAt(ao, weapon, name, 0)
            ?? Animations.SkillAttackContactAt(ao, weapon, "doubleslash", 0);
        var second = first is { } contact ? Animations.SkillAttackContactAt(ao, weapon, contact.Name, 1) : null;
        return new(LacerateWaves.At(from, aim, radius, angle), hands, gem, player.ActionInterruptionGeneration,
            start, SupportedAttackSeconds(player, skill, hands), first?.Contact ?? .3, second?.Contact ?? 2.0 / 3,
            first?.Name);
    }

    static string LacerateClip(Player player, WeaponProfile weapon, CharacterClass actor, AttackHand hands)
    {
        if (weapon.TwoHandMelee) return "doubleslash_2hsword";
        if (hands == AttackHand.Both) return "doubleslash_sword_sword";
        if (weapon.Shield) return "doubleslash_sword_shield";

        // These rigs give the mixed weapon pairs their own ACT clips.
        if (hands == AttackHand.Off)
        {
            if (actor is CharacterClass.Shadow or CharacterClass.Scion)
            {
                if (weapon.Claw) return "doubleslash_claw_sword_left";
                if (weapon.Dagger) return "doubleslash_dagger_sword";
            }
            string side = actor == CharacterClass.Templar ? "right" : "left";
            string pair = weapon.Sword || weapon.Axe || weapon.Mace || weapon.Claw ? "sword_sword" : "sword_dagger";
            return $"doubleslash_{pair}_{side}";
        }
        string? offClass = weapon.DualWield ? ActiveOffHand(player)?.Type?.Class : null;
        if (actor is CharacterClass.Shadow or CharacterClass.Scion && offClass is "Dagger" or "Rune Dagger")
            return "doublestrike_sword_dagger";
        if (actor == CharacterClass.Ranger && offClass is "One Hand Sword" or "Thrusting One Hand Sword"
            or "One Hand Mace" or "One Hand Axe") return "doubleslash_sword_claw";
        return "doubleslash_sword";
    }
}
