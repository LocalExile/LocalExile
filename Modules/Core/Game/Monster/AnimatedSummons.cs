using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    sealed record AnimatedSource(uint Id, WorldItemObject? Ground, Dictionary<byte, Item> Equipment);

    static AnimatedSource? AnimatedItems(ClientState conn, SummonCatalog.Definition definition, SupportedGem gem,
        (int X, int Y) aim, uint target, Dictionary<byte, Item>? restored)
    {
        if (restored is not null) return new(0, null, new(restored));
        if (definition.Family == "Clone")
        {
            if (ActiveMainHand(conn) is not { Type.Class: "Bow" } bow) return null;
            var equipment = ItemsOf(conn).EquipmentSnapshot().ToDictionary(p => p.Row, p => p.Item.Item);
            return new(0, null, equipment);
        }
        var catalog = ItemsFor(conn);
        byte Slot(Item item)
        {
            if (!item.Identified || catalog.ByHash(item.TypeHash) is not { } type
                || ItemModStats.LevelRequirement(item, type.DropLevel, catalog.Mods) > GolemStat(gem.Stats, "animate_item_maximum_level_requirement")) return 0;
            if (WeaponIn(conn, (item, type)) is { } weapon)
                return definition.Family == "AnimatedGuardian" || weapon.Melee || definition.Id == "AnimateWeaponAltY" ? (byte)2 : (byte)0;
            if (definition.Family != "AnimatedGuardian") return 0;
            return type.Class switch { "Body Armour" => 1, "Shield" => 3, "Helmet" => 4, "Gloves" => 8, "Boots" => 9, _ => (byte)0 };
        }
        var ground = target != 0 ? conn.Instance.Ground.Get(target) : null;
        uint id = target;
        if (ground is null)
        {
            var found = conn.Instance.Ground.Within(aim.X, aim.Y, 15, p => Slot(p.Item) != 0)
                .OrderBy(p => Dist((p.Item.GridX, p.Item.GridY), aim)).FirstOrDefault();
            id = found.Id; ground = found.Item;
        }
        var gear = definition.Family == "AnimatedGuardian" ? new Dictionary<byte, Item>(conn.Summons.GuardianEquipment) : [];
        if (ground is null) return gear.Count > 0 ? new(0, null, gear) : null;
        byte slot = Slot(ground.Item);
        if (slot == 0) return null;
        gear[slot] = ground.Item;
        return new(id, ground, gear);
    }

    static IReadOnlyList<VisibleItem> SummonLooks(GolemState.Golem fighter) => fighter.Equipment
        .Where(p => SummonVisibleSlots.ContainsKey(p.Key)).Select(p => (Slot: SummonVisibleSlots[p.Key], Look: LookOf(p.Value)))
        .Where(p => p.Look is not null).Select(p => p.Look! with { Slot = p.Slot }).ToArray();
    static readonly IReadOnlyDictionary<byte, byte> SummonVisibleSlots = new Dictionary<byte, byte>
        { [1] = 2, [2] = 0, [3] = 1, [4] = 4, [5] = 11, [6] = 8, [7] = 9, [8] = 5, [9] = 6, [10] = 10 };

    static async Task<bool> AnimateGroundItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        WorldItemObject ground, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        var skill = GemSkillEffects(conn).FirstOrDefault(g => g.Id == use.SkillId);
        if (skill.Name is null || SummonData.Find(SkillsFor(conn).EffectId(skill.Effect)) is not { Family: "AnimatedWeapon" or "AnimatedGuardian" }) return false;
        var here = conn.Walk.Position(AreaTime(conn)); var aim = (ground.GridX, ground.GridY);
        if (!TryBeginSkillCooldownUse(conn, use.SkillId, skill.Effect, CooldownFor(conn, skill.Effect, skill.Level, skill.Quality), false,
            () => { Interlocked.Increment(ref conn.ActionCount); conn.Walk.Order(here.X, here.Y, AreaTime(conn)); }, out var accepted, requestedAt: requestedAt)) return true;
        bool acknowledged = false;
        await Send(remote, stream, ciphers,
            [.. InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, aim.GridX, aim.GridY, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                .. InstanceProtocol.ActionEnded(PlayerId)], "animate ground equipment",
            still: () => acknowledged = SkillAcknowledgementAllowed(conn, skill.Effect, use.SkillId, () => accepted?.CommitNativeUse(conn, use.SkillId) ?? true));
        if (acknowledged) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, here, aim, use.TargetId,
            sequence: use.Sequence, actionToken: accepted?.ActionToken, cooldownUse: accepted);
        return true;
    }
}
