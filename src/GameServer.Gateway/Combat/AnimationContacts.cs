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
readonly record struct AnimationClip(int Length, int Contact, ContactPoint? Point);

/// <summary>
/// The "contact" keyframe of the character classes' animations (data/animation-contacts.json, written by
/// <c>python -I scripts/re/anim_contacts.py</c> from each class's rig .amd and .ao event lists): per animated object, per
/// animation, its length and the contact time in ms, and the keyframe's position in the model's space ("positions").
/// A spell is released at the contact, which the client scales with the cast time, so the server releases it at the
/// same fraction of the cast (Witch spell_projectile_wand: 599 of 1066 ms); an ordinary arrow or wand bolt leaves from
/// the keyframe's point (the bow's nock, the wand's tip).
/// Ordinary wand attacks additionally use the class ACT's ordered stance pool and the native action-seeded selector.
/// </summary>
sealed class AnimationContacts(string folder = "data")
{
    /// <summary>The Witch's spell_projectile fraction, used when the file or the animation is missing.</summary>
    public const double DefaultSpellRelease = 599.0 / 1066;

    /// <summary>The default attack's contact fraction when the class's animation or weapon stance is missing
    /// (attack1_unarmed 333/833 = 0.4 is representative).</summary>
    public const double DefaultAttackContact = 0.4;

    private sealed record ActorAnimations(Dictionary<string, AnimationClip> Clips,
        Dictionary<string, DefaultWandContact[]> WandPools);

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
    /// weapon. The client stretches the clip to the attack time, so the server pays the hit at this fraction of it.</summary>
    public double AttackContact(string animatedObject, WeaponProfile weapon) =>
        AttackClip(animatedObject, weapon) is { } clip ? (double)clip.Contact / clip.Length : DefaultAttackContact;

    /// <summary>Where the same keyframe lies in the model's space (the Ranger's attack1_bow: 5.5 cells in front, 16 steps
    /// up), or null without rig data for the class or weapon.</summary>
    public ContactPoint? AttackContactPoint(string animatedObject, WeaponProfile weapon) => AttackClip(animatedObject, weapon)?.Point;

    private AnimationClip? AttackClip(string animatedObject, WeaponProfile weapon)
    {
        if (!_byAo.Value.TryGetValue(animatedObject, out var actor)) return null;
        var anims = actor.Clips;
        string? main = weapon.Unarmed ? "unarmed" : weapon.Bow ? "bow" : weapon.Staff ? "staff"
            : weapon.Sword && weapon.TwoHandMelee ? "2hsword" : weapon.Mace && weapon.TwoHandMelee ? "maul"
            : weapon.Sword ? "sword" : weapon.Dagger ? "dagger" : weapon.Claw ? "claw" : weapon.Wand ? "wand" : null;
        if (main is null) return null;
        string[] candidates = weapon.Shield
            ? [$"attack1_{main}_shield", $"attack1_{main}"]
            : weapon.DualWield
            ? [$"attack1_{main}_{main}", $"attack1_{main}"]
            : [$"attack1_{main}"];
        foreach (string name in candidates)
            if (anims.TryGetValue(name, out var a) && a.Length > 0) return a;
        foreach (var (name, a) in anims.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (name.StartsWith($"attack1_{main}", StringComparison.Ordinal) && a.Length > 0) return a;
        return null;
    }

    /// <summary>
    /// The ordinary wand's ACT-selected main-hand clip, seeded by a fresh action's zero-extended sequence.
    /// Missing old/generated data returns false so the caller can identify approximate fallback timing.
    /// ACT pools exported here are unconditional, group-zero, weight-one entries; no conditional selection is guessed.
    /// </summary>
    public bool TryDefaultWandContact(string animatedObject, WeaponProfile weapon, ushort sequence,
        out DefaultWandContact selection)
    {
        selection = default;
        if (!weapon.Wand || !_byAo.Value.TryGetValue(animatedObject, out var actor)) return false;
        string stance = weapon.Shield ? "WandShield" : weapon.DualWield ? "WandWand" : "Wand";
        if (!actor.WandPools.TryGetValue(stance, out var pool)) return false;
        selection = pool[AnimationVariantSelector.Select(sequence, pool.Select(a => a.Weight).ToArray())];
        return true;
    }

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
            var clips = ao.Value.GetProperty("animations").EnumerateObject()
                .ToDictionary(a => a.Name, a => new AnimationClip(a.Value[0].GetInt32(), a.Value[1].GetInt32(),
                    positions.TryGetValue(a.Name, out var point) ? point : null));
            var pools = new Dictionary<string, DefaultWandContact[]>(StringComparer.Ordinal);
            if (ao.Value.TryGetProperty("defaultWand", out var wand))
                foreach (var stance in wand.EnumerateObject())
                {
                    var entries = stance.Value.EnumerateArray().Select(a => new DefaultWandContact(
                        a.GetProperty("animation").GetString()!, a.GetProperty("index").GetUInt32(),
                        a.GetProperty("length").GetInt32(), a.GetProperty("contact").GetInt32(),
                        stance.Name, a.GetProperty("weight").GetUInt32(),
                        a.TryGetProperty("position", out var position) ? Point(position) : null)).ToArray();
                    if (entries.Length == 0 || entries.Any(a => string.IsNullOrWhiteSpace(a.Animation)
                        || a.LengthMs <= 0 || a.ContactMs < 0 || a.ContactMs > a.LengthMs || a.Weight != 1))
                        throw new InvalidDataException($"Invalid ordinary wand animation pool {ao.Name}/{stance.Name}.");
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

/// <summary>A rig clip chosen from the ordinary wand ACT pool, including its contact keyframe.</summary>
readonly record struct DefaultWandContact(string Animation, uint AnimationIndex, int LengthMs, int ContactMs,
    string Stance, uint Weight, ContactPoint? Point = null)
{
    public double ContactFraction => (double)ContactMs / LengthMs;
}
