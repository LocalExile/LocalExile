using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    internal const string SmokeMineEffect = "SmokeMine";
    internal const int SmokeMineCloudRadius = 18;
    static ushort SmokeMineSpeedRow;
    static Dictionary<int, (string Ao, int ArtRadius)> SmokeMineArt = [];

    sealed class SmokeMineSpeed
    {
        public int Refresh;
    }

    static readonly RuleTable<Player, SmokeMineSpeed> SmokeMineSpeedBuffs = new();

    static void LoadSmokeMineData()
    {
        SmokeMineSpeedRow = BuffRow("smoke_mine_movement_speed", 233);
        if (!ClientTables.Has("GroundEffects") || !ClientTables.Has("GroundEffectTypes")) return;
        int? smoke = ClientTables.Table("GroundEffectTypes").Where(row => row.Text("Id") == "Smoke")
            .Select(row => (int?)row.Index).FirstOrDefault();
        if (smoke is null) return;
        SmokeMineArt = ClientTables.Table("GroundEffects")
            .Where(row => row.IntOrNull("GroundEffectTypesKey") == smoke && row.Int("__col2") == 2)
            .Select(row => (Variation: row.Int("__col1"), Ao: row.Texts("AOFile").FirstOrDefault(), Radius: row.Int("__col4")))
            .Where(art => !string.IsNullOrEmpty(art.Ao))
            .ToDictionary(art => art.Variation, art => (art.Ao!, art.Radius));
    }

    static void TriggerSmokeMine(Player player, PlacedSkills.Placed mine, double now, WorldRecordBuilder bytes)
    {
        var from = player.Walk.Position(now);
        var to = mine.At;
        var owner = CharacterPlayerStats(player);
        int Own(uint stat) => mine.Own.GetValueOrDefault(stat);
        int Owner(uint stat) => owner.GetValueOrDefault(stat);
        bytes.World(mine.Id, InstanceProtocol.ActionStarted(mine.Id, to.X, to.Y, from.X, from.Y,
            0x4000, 0xffff, player.MonsterOrderFlags));

        // Walls prevent the teleport, but the detonation still releases smoke at both ends.
        if (from != to && player.PendingTransfer is null && Owner(Stat.YourMovementSkillsAreDisabled) == 0
            && player.Layout.WalkableFor(to.X, to.Y, 1) && !player.Barrier.Contains(to)
            && !player.Blockers.Any(b => Math.Max(Math.Abs((long)to.X - b.X), Math.Abs((long)to.Y - b.Y)) - b.Size < 2)
            && ClientPathfinder.Line(from, to).All(cell => !player.SightBarrier.Contains(cell)
                && (player.Layout.Sight?.Invoke(cell.X, cell.Y) ?? (player.Layout.WalkableFor(cell.X, cell.Y, 0) ? 1 : 0)) >= 1))
        {
            player.Walk.Teleport(to.X, to.Y, now);
            bytes.World(player.Id, InstanceProtocol.Teleport(player.Id, to.X, to.Y), "Smoke Mine teleport");
        }

        double cloudSeconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSecondarySkillEffectDuration),
            Owner, Own, SkillDurationModifiers.Kinds.Secondary);
        if (cloudSeconds > 0)
        {
            int radius = SkillRadius(player, SmokeMineCloudRadius, mine.Own, spell: true, mine.Effect, owner);
            AddCloud(from);
            if (to != from) AddCloud(to);
            PresentNearby(player);

            void AddCloud((int X, int Y) at)
            {
                const FlaskGrounds.Kind kind = FlaskGrounds.Kind.SmokeCloud;
                uint? art = SmokeMineArt.TryGetValue(Own(Stat.GroundSmokeArtVariation), out var variation)
                    ? QueueGroundArtLocked(player, variation, at, radius) : QueueGroundArtLocked(player, kind, at, radius);
                player.FlaskGrounds.Add(new(kind, at.X, at.Y, radius, now + cloudSeconds, art));
            }
        }

        long mineDuration = Own(Stat.AdditiveMineDurationModifiersApplyToBuffEffectDuration) != 0
            ? (long)Owner(Stat.MineDurationPlusPct) + Own(Stat.MineDurationPlusPct) : 0;
        double buffSeconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Owner,
            stat => stat == Stat.SkillEffectDurationPlusPct ? ActorScale.Saturate(mineDuration + Own(stat)) : Own(stat),
            SkillDurationModifiers.Kinds.Buff);
        int speed = Own(Stat.BaseMovementVelocityPlusPct);
        if (buffSeconds > 0 && speed > 0) GrantSmokeMineSpeed(player, mine, speed, buffSeconds);
    }

    static void GrantSmokeMineSpeed(Player player, PlacedSkills.Placed mine, int speed, double seconds)
    {
        var area = player.Area;
        var state = SmokeMineSpeedBuffs.GetValue(player, _ => new());
        int refresh = ++state.Refresh;
        var entity = new EntityId(player.Id);
        var stats = new Dictionary<uint, int> { [Stat.BaseMovementVelocityPlusPct] = speed };
        area.SetStatBuff(entity, state, stats);
        ushort source = GemSkillEffects(player).Any(g => g.Id == mine.Skill && g.Effect == mine.Effect) ? mine.Skill : (ushort)0;
        area.Buffs.Set(player.Id, BuffRows.Spec(SmokeMineSpeedRow, player.Id, stats, seconds) with { SourceSkill = source });
        AfterSeconds(player, seconds, () =>
        {
            if (ReferenceEquals(player.Area, area) && state.Refresh == refresh) ClearSmokeMineSpeed(player);
        });
    }

    static void ClearSmokeMineSpeed(Player player)
    {
        if (!SmokeMineSpeedBuffs.TryGetValue(player, out var state)) return;
        state.Refresh++;
        player.Area.RemoveStatBuff(new EntityId(player.Id), state);
        player.Area.Buffs.Clear(player.Id, SmokeMineSpeedRow, player.Id);
    }
}
