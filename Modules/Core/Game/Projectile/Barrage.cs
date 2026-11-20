using GameServer.Core.Projectiles;

public static partial class Game
{
    sealed record BarrageAttack(int Action, double Start, double Duration, int Count, AnimationClip Fire,
        bool CannotCancelBeforeContact, int SpreadPercent, bool NoSpread, ParallelFiringPoints? Points, bool ExtraEndVolleys)
    {
        public double Windup => Duration * .4;
        public double Interval => Duration * .6 / Count;
        public double FirstContact => Windup + Interval * Fire.ContactFraction;

        // The startup clip permits an early Move in its first 30%. The first firing clip is protected.
        // Later clips use their own contact and lock_cancelling events (3.29.3c, 0x141bad750).
        public bool AllowsMove(double elapsed, bool early)
        {
            if (elapsed < Windup) return early && !CannotCancelBeforeContact && elapsed >= 0 && elapsed <= Windup * .3;
            double phase = (elapsed - Windup) / Interval;
            int shot = (int)Math.Floor(phase);
            if (shot == 0 || CannotCancelBeforeContact && shot < Count - 1) return false;
            double fraction = phase - shot;
            return early && !CannotCancelBeforeContact && fraction <= Math.Max(.2, Fire.ContactFraction - .3)
                || fraction >= (double)(Fire.Cancel ?? Fire.Contact) / Fire.Length;
        }

        // Each fire clip samples three pairs of offsets around a point 100 cells along the aim (0x141bae62c).
        // The new aim is an integer grid point 150 cells out, or at cursor distance for ground-targeted shots.
        public ProjectileLaunch[] Launches(ProjectileVector aim, ushort sequence, int count, bool atCursor)
        {
            var launches = new ProjectileLaunch[count];
            var sideStep = Points?.GridStep(aim) ?? default;
            double heading = Math.Atan2(aim.Y, aim.X);
            float distance = atCursor ? (float)aim.Length : 150;
            float width = NoSpread ? 0 : (100 + SpreadPercent) / 100f * 120;
            for (int i = 0; i < count; i++)
            {
                int slot = Points?.SweepSlot(i, count, ExtraEndVolleys) ?? 0;
                var offset = sideStep * slot;
                var forward = (aim + offset).Unit * 100;
                var random = new AnimationVariantSelector.NativeRandom([sequence, 0, 0, (uint)i + 1]);
                float x = 0, y = 0;
                for (int sample = 0; sample < 3; sample++)
                {
                    x += random.Range(0, width);
                    y += random.Range(0, width);
                }
                static int Offset(float sum, float width) => (int)MathF.Floor((int)(sum / 3 - width * .5f) * .45f + .5f);
                float dx = MathF.Floor((float)forward.X + .5f) + Offset(x, width);
                float dy = MathF.Floor((float)forward.Y + .5f) + Offset(y, width);
                float scale = distance / MathF.Sqrt(dx * dx + dy * dy);
                launches[i] = new(offset, Math.Atan2((int)(dy * scale), (int)(dx * scale)) - heading);
            }
            return launches;
        }
    }

    static readonly RuleTable<ClientState, BarrageAttack> BarrageAttacks = new();

    static BarrageAttack? DirectBarrage(ClientState conn, ushort skill, int effect, int action, double start, double seconds,
        TotemState.Totem? totem = null)
    {
        var own = SupportedSkill(conn, skill).Stats;
        if (GolemStat(own, "projectiles_barrage") == 0) return null;
        var player = CharacterPlayerStats(conn);
        int Get(string key) => GolemStat(own, key) + GolemStat(player, key);
        // These shapes own the volley at the landing point, around the source or per target. The actor
        // plays a start clip and one fire clip per projectile only on the direct attack path (0x141ba5600).
        if (Get("projectiles_rain") != 0 || Get("projectiles_nova") != 0
            || Get("base_number_of_projectiles_in_spiral_nova") != 0 || Get("power_siphon_fire_at_all_targets") != 0
            || Get("power_siphon_base_fire_at_x_targets") > 0)
            return null;
        var weapon = AnimationWeaponProfileFor(conn);
        int count = ResolveProjectilePanelParameters(effect)!.Resolve(Get, 1, weapon.Bow).Count;
        if (count <= 1) return null;
        AnimationClip fire;
        if (totem is null)
        {
            string name = weapon.Bow ? "barrage_bow" : weapon.DualWield ? "barrage_wand_wand"
                : weapon.Shield ? "barrage_wand_shield" : "barrage_wand";
            string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
            fire = Animations.ContactClip(ao, name);
        }
        else
        {
            var clip = SkillsFor(conn).MonsterContact(totem.VarietyId, "BarrageFireMain", totem.Sequence);
            if (clip?.ContactMs is not int contact)
                throw new InvalidOperationException($"Missing Barrage fire contact for {totem.VarietyId}.");
            fire = new(clip.LengthMs, contact, clip.Point, clip.Index);
        }
        return new(action, start, seconds, count, fire,
            Get("cannot_cancel_skill_before_contact_point") != 0, Get("barrage_support_projectile_spread_+%"),
            Get("no_barrage_projectile_spread") != 0, ParallelFiringPoints.From(Get),
            Get("barrage_final_volley_fires_x_additional_projectiles_simultaneously") != 0);
    }

    static bool? BarrageAllowsMove(ClientState conn, double at)
    {
        if (!conn.LockstepHold.Active || !BarrageAttacks.TryGetValue(conn, out var use)
            || conn.ActionCount != use.Action || at >= use.Start + use.Duration) return null;
        return use.AllowsMove(at - use.Start, (conn.LockstepHold.Flags & 0x400) != 0);
    }
}
