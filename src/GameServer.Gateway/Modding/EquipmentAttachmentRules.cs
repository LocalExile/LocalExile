/// <summary>Global options registered by a user mod; disposing its registration restores the previous options.</summary>
public sealed record EquipmentAttachmentRules(bool GlobalModifiers = false, bool LocalDamage = false, bool LocalDefences = false, bool LocalAccuracy = false);
