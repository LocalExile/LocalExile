using System.Text.Json;

/// <summary>An animation event's position in the model's space (the rig .amd's "x y z" or an .ao event's "position",
/// world units): +X the model's left, −Y its front, −Z up (the Ranger's attack1_bow contact: 59.8 in front, 127.5 up;
/// its arrow_appear 26.8 behind, at the quiver). Only the forward and vertical parts are used: the grid's lateral
/// handedness is not established, and the lateral offsets are under a cell.</summary>
readonly record struct ContactPoint(float X, float Y, float Z)
{
    public double ForwardUnits => -Y;
    public double UpUnits => -Z;
    /// <summary>How far in front of the model the event lies, in cells (negative: behind it).</summary>
    public double ForwardCells => ForwardUnits / WorldUnits.PerCell;
    /// <summary>The event's height above the model's ground in Positioned marker steps.</summary>
    public int HeightSteps => (int)Math.Round(UpUnits / WorldUnits.PerHeightStep);
    public ContactPoint Scaled(double factor) => new((float)(X * factor), (float)(Y * factor), (float)(Z * factor));
}

/// <summary>A rig clip's length, its contact keyframe's time (ms) and, when the keyframe carries one, its position.</summary>
readonly record struct AnimationClip(int Length, int Contact, ContactPoint? Point, uint Index = uint.MaxValue);

/// <summary>The kind of cast a run skill plays: an attack swing, or a spell projectile / area / buff cast.</summary>
enum CastAnimationKind { Attack, Projectile, Area, Buff }

/// <summary>A rig animation the client can play with 0x1b2 kind 0: its name, index and natural length.</summary>
readonly record struct CastAnimationClip(string Name, uint Index, int LengthMs);

/// <summary>
/// The "contact" keyframe of the character classes' animations (data/animation-contacts.json, written by
/// <c>python -I scripts/re/anim_contacts.py</c> from each class's rig .amd and .ao event lists): per animated object, per
/// animation, its length and the contact time in ms, and the keyframe's position in the model's space ("positions").
/// A spell is released at the contact, which the client scales with the cast time, so the server releases it at the
/// same fraction of the cast (Witch spell_projectile_wand: 599 of 1066 ms); an ordinary arrow or wand bolt leaves from
/// the keyframe's point (the bow's nock, the wand's tip).
/// The default attack plays the class ACT's stance pool ("defaultAttack"): a rig clip, or a view of one (a one-hand sword
/// swings the two halves of doublestrike_sword), with the ACT's attack_time budget; <see cref="DefaultAttackClip"/>
/// says when its contact plays.
/// </summary>
sealed class AnimationContacts(string folder = "data")
{
    /// <summary>The Witch's spell_projectile fraction, used when the file or the animation is missing.</summary>
    public const double DefaultSpellRelease = 599.0 / 1066;

    /// <summary>The default attack's contact fraction when the class's animation or weapon stance is missing
    /// (attack1_unarmed 333/833 = 0.4 is representative).</summary>
    public const double DefaultAttackContact = 0.4;

    private sealed record ActorAnimations(Dictionary<string, AnimationClip> Clips,
        Dictionary<string, DefaultAttackClip[]> AttackPools);

    private readonly Lazy<Dictionary<string, ActorAnimations>> _byAo = new(() => Load(folder));

    /// <summary>When in a spell cast its projectile leaves, as a fraction of the cast: the class's spell_projectile
    /// animation (wand, unarmed, then any), else any spell animation's contact.</summary>
    public double SpellRelease(string animatedObject) =>
        SpellContact(animatedObject, ["spell_projectile_wand", "spell_projectile_unarmed"], ["spell_projectile", "spell_"])
        ?? DefaultSpellRelease;

    /// <summary>When an area spell (Ice Nova: the class ACT's SpellAreaOfEffect* animations) lands, as a fraction of the
    /// cast: the class's spell_aoe clip (spellaoe in the newer rigs; Witch 266 of 766 ms), else the projectile release.</summary>
    public double SpellAreaRelease(string animatedObject) =>
        SpellContact(animatedObject, ["spell_aoe_wand", "spellaoe_wand", "spell_aoe_unarmed", "spellaoe_unarmed"],
            ["spell_aoe", "spellaoe"]) ?? SpellRelease(animatedObject);

    double? SpellContact(string animatedObject, string[] names, string[] prefixes)
    {
        if (!_byAo.Value.TryGetValue(animatedObject, out var actor)) return null;
        var anims = actor.Clips;
        foreach (string name in names)
            if (anims.TryGetValue(name, out var a) && a.Length > 0) return (double)a.Contact / a.Length;
        foreach (string prefix in prefixes)
            foreach (var (name, a) in anims.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (name.StartsWith(prefix, StringComparison.Ordinal) && a.Length > 0) return (double)a.Contact / a.Length;
        return null;
    }

    /// <summary>When a melee attack's weapon visually hits (the attack1 animation's contact keyframe), as a fraction of the
    /// attack: the class's <c>attack1_&lt;weapon&gt;</c> clip, then its shield/dual-wield variant, then any attack1 of that
    /// weapon. An attack gem's clip is taken to stretch to the attack time; the default attack's own clip is in
    /// <see cref="DefaultAttackContactSeconds"/>.</summary>
    public double AttackContact(string animatedObject, WeaponProfile weapon) =>
        AttackClip(animatedObject, weapon) is { } found ? (double)found.Clip.Contact / found.Clip.Length : DefaultAttackContact;

    /// <summary>Where the same keyframe lies in the model's space (the Ranger's attack1_bow: 5.5 cells in front, 16 steps
    /// up), or null without rig data for the class or weapon.</summary>
    public ContactPoint? AttackContactPoint(string animatedObject, WeaponProfile weapon) => AttackClip(animatedObject, weapon)?.Clip.Point;

    /// <summary>
    /// The rig clip a run skill's cast plays through 0x1b2 kind 0 (a clip change, which does not touch the action
    /// state, unlike 0x1d1's input lock). The weapon's own stance clip comes first, then the unarmed one, then any clip
    /// of the same kind: the older rigs name spells <c>spell_projectile_*</c>, the newer ones <c>spellprojectile_*</c>.
    /// </summary>
    public CastAnimationClip? CastClip(string animatedObject, WeaponProfile weapon, CastAnimationKind kind)
    {
        if (!_byAo.Value.TryGetValue(animatedObject, out var actor)) return null;
        if (kind == CastAnimationKind.Attack)
            return AttackClip(animatedObject, weapon) is { } found && found.Clip.Index != uint.MaxValue
                ? new(found.Name, found.Clip.Index, found.Clip.Length) : null;
        string main = WeaponSuffix(weapon) ?? "unarmed";
        string[] names = kind switch
        {
            CastAnimationKind.Projectile =>
                [$"spell_projectile_{main}", $"spellprojectile_{main}", $"spell_upwards_{main}",
                 "spell_projectile_unarmed", "spellprojectile_unarmed", "spell_upwards_unarmed"],
            CastAnimationKind.Area =>
                [$"spell_aoe_{main}", $"spellaoe_{main}", $"spell_aoe_{main.Replace("staff", "stave")}",
                 "spell_aoe_unarmed", "spellaoe_unarmed"],
            _ =>
                [$"spell_buff_{main}", $"spellbuff_{main}", "spell_buff_unarmed", "spellbuff_unarmed"],
        };
        foreach (string name in names)
            if (actor.Clips.TryGetValue(name, out var clip) && clip.Length > 0 && clip.Index != uint.MaxValue)
                return new(name, clip.Index, clip.Length);
        string[] prefixes = kind switch
        {
            CastAnimationKind.Projectile => ["spell_projectile_", "spellprojectile_", "spell_upwards_"],
            CastAnimationKind.Area => ["spell_aoe", "spellaoe"],
            _ => ["spell_buff", "spellbuff"],
        };
        foreach (string prefix in prefixes)
            foreach (var (name, clip) in actor.Clips.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && clip.Length > 0 && clip.Index != uint.MaxValue)
                    return new(name, clip.Index, clip.Length);
        return null;
    }

    /// <summary>The suffix the class ACT uses for this weapon's stance clips.</summary>
    static string? WeaponSuffix(WeaponProfile weapon) =>
        weapon.Unarmed ? "unarmed" : weapon.Bow ? "bow" : weapon.Staff ? "staff"
        : weapon.Sword && weapon.TwoHandMelee ? "2hsword" : weapon.Mace ? "maul"
        : weapon.Sword ? "sword" : weapon.Dagger ? "dagger" : weapon.Claw ? "claw" : weapon.Wand ? "wand"
        : weapon.Axe ? (weapon.TwoHandMelee ? "2hsword" : "sword") : null;

    private (string Name, AnimationClip Clip)? AttackClip(string animatedObject, WeaponProfile weapon)
    {
        if (!_byAo.Value.TryGetValue(animatedObject, out var actor)) return null;
        var anims = actor.Clips;
        string? main = weapon.Unarmed ? "unarmed" : weapon.Bow ? "bow" : weapon.Staff ? "staff"
            : weapon.Sword && weapon.TwoHandMelee ? "2hsword" : weapon.Mace ? "maul"
            : weapon.Sword ? "sword" : weapon.Dagger ? "dagger" : weapon.Claw ? "claw" : weapon.Wand ? "wand"
            : weapon.Axe ? (weapon.TwoHandMelee ? "2hsword" : "sword") : null;
        if (main is null) return null;
        string[] candidates = weapon.Shield
            ? [$"attack1_{main}_shield", $"attack1_{main}"]
            : weapon.DualWield
            ? [$"attack1_{main}_{main}", $"attack1_{main}"]
            : [$"attack1_{main}"];
        foreach (string name in candidates)
            if (anims.TryGetValue(name, out var a) && a.Length > 0) return (name, a);
        foreach (var (name, a) in anims.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (name.StartsWith($"attack1_{main}", StringComparison.Ordinal) && a.Length > 0) return (name, a);
        return null;
    }

    /// <summary>The ACT stance the client picks the default attack's clip from, by the hands' items (the thrusting-sword
    /// and one-hand mace stances are not told apart here), or null for a weapon without one.</summary>
    public static string? StanceFor(WeaponProfile weapon)
    {
        string? main = weapon.Unarmed ? (weapon.Shield ? "Shield" : "Unarmed") : weapon.Bow ? "Bow" : weapon.Staff ? "Staff"
            : weapon.Sword && weapon.TwoHandMelee ? "TwoHandSword" : weapon.Mace && weapon.TwoHandMelee ? "TwoHandMace"
            : weapon.Sword ? "OneHandSword" : weapon.Dagger ? "Dagger" : weapon.Claw ? "Claw" : weapon.Wand ? "Wand" : null;
        if (main is null || weapon.Unarmed || weapon.Bow || weapon.TwoHandMelee || weapon.Staff) return main;
        return weapon.Shield ? main + "Shield" : weapon.DualWield ? main + main : main;
    }

    /// <summary>
    /// The default attack's clip for this use: the stance pool's entry the native action-seeded selector picks (the
    /// Witch's four wand clips: <see cref="AnimationVariantSelector"/>), or, for a pair of views the client plays in an
    /// order not predicted (live 2026-11-03: 51 sword swings, no seed found), the one whose contact plays later, so that
    /// a Move held until it is never early. False without pool data for the class or stance: the caller falls back to
    /// the attack1 keyframe. Pools are unconditional, group-zero, weight-one entries; no conditional selection is guessed.
    /// </summary>
    public bool TryDefaultAttackClip(string animatedObject, WeaponProfile weapon, ushort sequence, double attackSeconds,
        out DefaultAttackClip selection)
    {
        selection = default;
        if (StanceFor(weapon) is not { } stance || !_byAo.Value.TryGetValue(animatedObject, out var actor)
            || !actor.AttackPools.TryGetValue(stance, out var pool)) return false;
        selection = pool.Any(a => a.Pair)
            ? pool.MaxBy(a => a.ContactSeconds(attackSeconds))
            : pool[AnimationVariantSelector.Select(sequence, pool.Select(a => a.Weight).ToArray())];
        return true;
    }

    /// <summary>When the default attack's contact plays after the swing's start (seconds): the stance pool's clip as the
    /// client plays it (<see cref="DefaultAttackClip.ContactSeconds"/>), else the attack1 keyframe's fraction of the
    /// attack.</summary>
    public double DefaultAttackContactSeconds(string animatedObject, WeaponProfile weapon, ushort sequence, double attackSeconds) =>
        TryDefaultAttackClip(animatedObject, weapon, sequence, attackSeconds, out var clip)
            ? clip.ContactSeconds(attackSeconds) : attackSeconds * AttackContact(animatedObject, weapon);

    /// <summary>When the client runs a Move it holds during the default attack's swing (seconds after the swing's start):
    /// the stance pool's clip's lock_cancelling event, else its contact (<see cref="DefaultAttackClip.CutInSeconds"/>);
    /// without pool data, the attack1 keyframe's fraction of the attack.</summary>
    public double DefaultAttackCutInSeconds(string animatedObject, WeaponProfile weapon, ushort sequence, double attackSeconds) =>
        TryDefaultAttackClip(animatedObject, weapon, sequence, attackSeconds, out var clip)
            ? clip.CutInSeconds(attackSeconds) : attackSeconds * AttackContact(animatedObject, weapon);

    private static Dictionary<string, ActorAnimations> Load(string folder)
    {
        string path = Path.Combine(folder, "animation-contacts.json");
        var result = new Dictionary<string, ActorAnimations>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var ao in d.RootElement.EnumerateObject())
        {
            var positions = new Dictionary<string, ContactPoint>(StringComparer.Ordinal);
            if (ao.Value.TryGetProperty("positions", out var listed) && listed.ValueKind == JsonValueKind.Object)
                foreach (var entry in listed.EnumerateObject())
                    if (Point(entry.Value) is { } point) positions[entry.Name] = point;
            var indices = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            if (ao.Value.TryGetProperty("indices", out var listedIndices) && listedIndices.ValueKind == JsonValueKind.Object)
                foreach (var entry in listedIndices.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetUInt32(out uint index))
                        indices[entry.Name] = index;
            var clips = ao.Value.GetProperty("animations").EnumerateObject()
                .ToDictionary(a => a.Name, a => new AnimationClip(a.Value[0].GetInt32(), a.Value[1].GetInt32(),
                    positions.TryGetValue(a.Name, out var point) ? point : null,
                    indices.GetValueOrDefault(a.Name, uint.MaxValue)), StringComparer.OrdinalIgnoreCase);
            // The default attack's stance pools cover the wand's ACT-selected clip too, which main's "defaultWand" pools
            // served before the stance pools replaced them.
            var pools = new Dictionary<string, DefaultAttackClip[]>(StringComparer.Ordinal);
            if (ao.Value.TryGetProperty("defaultAttack", out var attack))
                foreach (var stance in attack.EnumerateObject())
                {
                    var entries = stance.Value.EnumerateArray().Select(a => new DefaultAttackClip(
                        a.GetProperty("animation").GetString()!, a.GetProperty("source").GetString()!,
                        a.GetProperty("index").GetInt32(), a.GetProperty("begin").GetInt32(),
                        a.GetProperty("length").GetInt32(), a.GetProperty("contact").GetInt32(),
                        a.TryGetProperty("cancel", out var cancel) && cancel.ValueKind == JsonValueKind.Number ? cancel.GetInt32() : null,
                        a.TryGetProperty("slow", out var slow) && slow.ValueKind == JsonValueKind.Array ? slow[0].GetInt32() : null,
                        a.TryGetProperty("slow", out slow) && slow.ValueKind == JsonValueKind.Array ? slow[1].GetInt32() : null,
                        a.TryGetProperty("budget", out var budget) && budget.ValueKind == JsonValueKind.Number ? budget.GetDouble() : null,
                        stance.Name, a.GetProperty("weight").GetUInt32(),
                        a.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.True,
                        a.TryGetProperty("pair", out var pair) && pair.ValueKind == JsonValueKind.True,
                        a.TryGetProperty("position", out var position) ? Point(position) : null)).ToArray();
                    if (entries.Length == 0 || entries.Any(a => string.IsNullOrWhiteSpace(a.Animation)
                        || a.LengthMs <= 0 || a.ContactMs < 0 || a.ContactMs > a.LengthMs || a.Weight != 1
                        || a.CancelMs is < 0 || a.CancelMs > a.LengthMs || a.BudgetSeconds is <= 0))
                        throw new InvalidDataException($"Invalid default attack animation pool {ao.Name}/{stance.Name}.");
                    pools.Add(stance.Name, entries);
                }
            result[ao.Name] = new ActorAnimations(clips, pools);
        }
        return result;
    }

    /// <summary>A three-number JSON array as a point; anything else is no position, and so is the rig's placeholder for
    /// an event without one ("0 -0 -0" in the .amd), which would otherwise read as a release at the feet.</summary>
    internal static ContactPoint? Point(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3) return null;
        Span<float> parts = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            if (value[i].ValueKind != JsonValueKind.Number) return null;
            parts[i] = (float)value[i].GetDouble();
            if (!float.IsFinite(parts[i])) return null;
        }
        return parts[0] == 0 && parts[1] == 0 && parts[2] == 0 ? null : new(parts[0], parts[1], parts[2]);
    }
}

/// <summary>
/// A default attack's clip from the class ACT's stance pool: a rig clip (<see cref="Source"/>, its index in the rig)
/// from <see cref="BeginMs"/> for <see cref="LengthMs"/> (a view of it, or all of it), its contact keyframe, its
/// lock_cancelling keyframe when it has one, and its slow span, all relative to that start, the ACT's attack_time
/// budget in seconds when it has one, and whether the item's own rig plays it (a bow's attack1_bow).
/// </summary>
readonly record struct DefaultAttackClip(string Animation, string Source, int Index, int BeginMs, int LengthMs, int ContactMs,
    int? CancelMs, int? SlowBeginMs, int? SlowEndMs, double? BudgetSeconds, string Stance, uint Weight, bool Item, bool Pair,
    ContactPoint? Point)
{
    public double ContactFraction => (double)ContactMs / LengthMs;

    /// <summary>
    /// When the contact plays after the swing's start, for an attack of <paramref name="attackSeconds"/>. A clip without a
    /// budget is taken to stretch to the attack (a 600 ms wand clip's contact at 300 plays 0.333 s into a 0.667 s
    /// attack, a 1066 ms bow clip plays at 1.49 throughout a 0.714 s attack: live 2026-11-03, from held Moves; the
    /// client's own shot leaves at <see cref="ReleaseSeconds"/>). A budgeted clip plays
    /// over its budget and, when the attack is longer, the client stretches it to the attack time before the contact
    /// (the Ranger's attack_sword_01a, 365 ms with the contact at 299 and a 0.45 s budget, in a 0.645 s attack: the
    /// first 100 ms at 0.81 of the clip's speed, then 0.29 until 430 ms, then 0.81 again, the contact at 0.56 s; 01b,
    /// 768 ms with the contact at 534: 1.62, 0.38 and 1.62, the contact at 0.52 s). An attack shorter than the budget
    /// is taken to compress the clip to it (not measured).
    /// </summary>
    public double ContactSeconds(double attackSeconds) => PlayedAt(ContactMs, attackSeconds);

    /// <summary>
    /// When the client runs a Move it holds during the swing: the clip's lock_cancelling keyframe where it has one (the
    /// bow's attack1_bow at 600 of 1066 ms, after its contact at 399: live 2026-11-03, Moves 0.37 to 0.40 s into a
    /// 0.714 s attack ran at 0.40 to 0.43 s, later ones at once), else the contact (the wand's and the sword's clips
    /// carry none). A keyframe after the contact plays at the clip's pace after the stretch, as the contact does.
    /// </summary>
    public double CutInSeconds(double attackSeconds) => PlayedAt(CancelMs ?? ContactMs, attackSeconds);

    /// <summary>
    /// When the client's own shot leaves, on the clip clock it sets up (<c>0x141b0ac20</c> → <c>0x141b0a9b0</c>): a clip
    /// without a budget that is shorter than the attack plays at its own speed outside its slow span, and the span takes
    /// up the rest, at most 8 times its length (the 600 ms wand clip's contact at 300, span 150–235, leaves 0.367 s into a
    /// 0.667 s attack). A budgeted clip, or one no shorter than the attack, leaves at <see cref="ContactSeconds"/>.
    /// The client fires on the frame tick after this (<c>Game.LockstepShotAt</c>): live 2026-11-05, the wand on frame 12
    /// of 33 ms (38 of 39), which bounds its release to 0.363–0.396 s and rules out the linear 0.333 s (frame 11); the
    /// 0.367 s here is the model's, consistent with that, not a measured value.
    /// </summary>
    public double ReleaseSeconds(double attackSeconds)
    {
        double length = LengthMs / 1000.0, at = ContactMs / 1000.0;
        if (BudgetSeconds is not null || attackSeconds <= length || SlowBeginMs is not { } b || SlowEndMs is not { } e || e <= b)
            return ContactSeconds(attackSeconds);
        double begin = b / 1000.0, end = e / 1000.0, span = end - begin;
        double spanPlayed = Math.Min(8 * span, attackSeconds - length + span);
        double pace = (length - span) / (attackSeconds - spanPlayed);
        if (at <= begin) return at / pace;
        if (at <= end) return begin / pace + (at - begin) * spanPlayed / span;
        return begin / pace + spanPlayed + (at - end) / pace;
    }

    double PlayedAt(int ms, double attackSeconds)
    {
        if (BudgetSeconds is not { } budget || attackSeconds < budget) return attackSeconds * ms / LengthMs;
        return ms / 1000.0 * budget / (LengthMs / 1000.0) + (attackSeconds - budget);
    }
}
