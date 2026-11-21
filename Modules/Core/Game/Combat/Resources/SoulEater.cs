using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    /// <summary>"Gain Soul Eater during any Flask Effect": while a flask effect is on the player, enemies killed within
    /// this radius feed a soul (poedb: 60 units). Each soul gives 5% increased attack and cast speed and 1% character
    /// size; one is lost every 0.5 s after 4 s without a kill.</summary>
    public const uint SoulEaterStat = Stat.GainSoulEaterDuringFlaskEffect;
    /// <summary>Soul Ascension's "Eat a Soul when you Hit a Rare or Unique Enemy, no more than once every 0.5 seconds":
    /// the stat's value is that cooldown in ms (500; the Divergent-like variant 2000), unscalable.</summary>
    public const uint SoulEaterHitStat = Stat.GainSoulEaterStackOnHitVsUniqueCooldownMs;
    public const int SoulEaterRadius = 60;
    /// <summary>BuffDefinitions row "soul_eater_aura" (277): the aura around the eater. Invisible as an icon, its
    /// BuffVisuals 288 EPK draws the soul visuals (the same aura BuffTemplate "MapModPlayerSoulEater" gives a player).</summary>
    public const ushort SoulEaterAuraRow = 277;
    /// <summary>BuffDefinitions row "soul_eater_player" (279), the player's Soul Eater buff: ShowCount with
    /// <c>virtual_soul_eater_maximum_stacks</c> as its maximum.</summary>
    public const ushort SoulEaterBuffRow = 279;

    static bool SoulEaterStatPresent(IReadOnlyDictionary<uint, int> stats) =>
        SoulEaterStat is var key && stats.GetValueOrDefault(key) != 0;

    static int SoulEaterHitCooldownMs(IReadOnlyDictionary<uint, int> stats) =>
        SoulEaterHitStat is var key ? Math.Max(0, stats.GetValueOrDefault(key)) : 0;

    // "Any Flask Effect": a running utility flask, a permanent one from a belt's always-apply effect, or a life/mana
    // flask still recovering. Life and mana flasks keep no utility effect here, but their recovery is a flask effect too.
    public static bool AnyFlaskEffect(Player player) =>
        player.UtilityFlaskEffects.Sources().Count > 0 || MagebloodSources(player, _ => 0).Count > 0 || player.FlaskEffects.Any;

    /// <summary>The client's own "during any Flask Effect" conditions and its native flask logic read <c>using_flask</c>
    /// (a stat, not the server's evaluated stats): 1 while any effect is on. Soul Eater's soul visuals are one of them.</summary>
    public static void WithUsingFlask(Player player, StatMap stats)
    {
        if (Stat.UsingFlask is var key) stats[key] = AnyFlaskEffect(player) ? 1 : 0;
    }

    static int SoulEaterMaximum(IReadOnlyDictionary<uint, int> stats)
    {
        int bonus = Stat.SoulEaterMaximumStacksPlus is var key ? stats.GetValueOrDefault(key) : 0;
        return (int)Math.Clamp(SoulEaterState.MaximumStacks + (long)bonus, 0, ushort.MaxValue);
    }

    public static bool SoulEaterActive(Player player, IReadOnlyDictionary<uint, int> stats) =>
        SoulEaterHitCooldownMs(stats) > 0 || SoulEaterStatPresent(stats) && AnyFlaskEffect(player);

    /// <summary>The buff's attack speed part, added to the final main-hand value without rebuilding the pools.</summary>
    public static int SoulEaterAttackSpeed(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        if (!SoulEaterActive(player, stats)) return 0;
        return 5 * SoulEaterStacks(player);
    }

    /// <summary>The stat snapshot's part: the attack/cast speed buckets and the capped character size.</summary>
    // The stacks now; the reading step runs again when the next soul is lost.
    static int SoulEaterStacks(Player player)
    {
        double now = AreaTime(player);
        int stacks = player.SoulEater.Stacks(now);
        if (player.SoulEater.NextLossAt is double at) StatsChangeIn(at - now);
        return stacks;
    }

    // The sources as the stats give them (PlayerStatsUpdated): which ones feed souls, and the maximum.
    static void ConfigureSoulEater(Player player, IReadOnlyDictionary<uint, int> stats)
    {
        bool flask = SoulEaterStatPresent(stats) && AnyFlaskEffect(player);
        int hitCooldown = SoulEaterHitCooldownMs(stats);
        bool active = flask || hitCooldown > 0;
        player.SoulEater.Enabled = active;
        player.SoulEater.FlaskFed = flask;
        player.SoulEater.HitFed = hitCooldown > 0;
        player.SoulEater.HitCooldownSeconds = hitCooldown / 1000.0;
        if (!active) { player.SoulEater.Clear(); return; }
        player.SoulEater.SetMaximum(SoulEaterMaximum(stats));
    }

    public static void WithSoulEater(Player player, StatMap stats)
    {
        if (!SoulEaterActive(player, stats)) return;
        // The buff's ShowCount reads this virtual: the base maximum plus the Soul Ascension gloves' increase.
        if (Stat.VirtualSoulEaterMaximumStacks is var maximumKey)
            stats[maximumKey] = SoulEaterMaximum(stats);
        int stacks = SoulEaterStacks(player);
        if (stacks <= 0) return;
        void Add(uint name, int amount)
        {
            if (amount != 0 && name is var key)
                stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + amount, int.MinValue, int.MaxValue);
        }
        Add(Stat.AttackSpeedPlusPct, 5 * stacks);
        Add(Stat.CastSpeedPlusPct, 5 * stacks);
        Add(Stat.CappedActorScalePlusPct, Math.Min(stacks, 50));
    }

    /// <summary>An enemy killed at <paramref name="at"/>: one soul for the flask source ("during any Flask Effect"),
    /// when the source is active and the player is near. Soul Ascension's hit source does not feed on kills.</summary>
    public static void SoulEaterSoul(Player player, uint id, (int X, int Y) at)
    {
        if (!player.SoulEater.FlaskFed) return;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        double now = AreaTime(player);
        if (Dist(at, player.Walk.Position(now)) > SoulEaterRadius) return;
        player.SoulEater.Soul(now);
        QueueSoul(player, id, at);
    }

    /// <summary>Soul Ascension: a hit on a rare or unique enemy eats one soul, no more than once per the item's
    /// cooldown (500 ms). No radius: a bow hit far away still eats. No beam: the official server's object for a hit's
    /// soul has not been captured, so only the counted buff and the stats it changes are published.</summary>
    internal static void SoulEaterHit(Player player, SpawnedMonster monster)
    {
        if (!player.SoulEater.HitFed) return;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        if (monster.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique)) return;
        player.SoulEater.TryHitSoul(AreaTime(player));
    }

    static void QueueSoul(Player player, uint id, (int X, int Y) at)
    {
        if (player.SoulEater.Souls.Count >= 32) player.SoulEater.Souls.RemoveAt(0);
        player.SoulEater.Souls.Add((id, at.X, at.Y));
    }

    /// <summary>The eaten-soul buff and, when its count or the aura changed, the stats and skills it changes. Sent from the
    /// player tick (250 ms) like Rage and Onslaught, so a pack's kills coalesce into one update instead of one per corpse.
    /// The aura is its own buff (as a self aura's source buff is): its EPK draws the soul visuals; the counted buff is the
    /// icon the client shows.</summary>
    public static byte[]? SoulEaterPresentation(Player player) => SoulEaterRecords(player, new WorldRecordBuilder(player, null)) is { } batch
            ? [.. batch.Records.SelectMany(r => r.CopyBytes())] : null;

    static OutputBatch? SoulEaterRecords(Player player, WorldRecordBuilder bytes)
    {
        if (player.Closed || player.LeavingArea || player.RageRetired) return null;
        // Refresh the source before deciding what to publish. Recovery flasks can start/end without a soul gain.
        var currentStats = CharacterPlayerStats(player);
        double now = AreaTime(player);
        bool active = player.SoulEater.Enabled && player.PlayerLife > 0;
        bool usingFlask = Stat.UsingFlask is var flaskKey && currentStats.GetValueOrDefault(flaskKey) != 0;
        int stacks = SoulEaterCount(player, now);
        bool auraChanged = active != (player.SoulEater.AuraBuff != 0);
        bool statsChanged = auraChanged || stacks != player.SoulEater.SentStacks || usingFlask != player.SoulEater.SentUsingFlask;
        if (!active) player.SoulEater.Souls.Clear();
        if (!statsChanged && player.SoulEater.Souls.Count == 0) return null;
        if (player.SoulEater.Souls.Count > 0)
        {
            // One line per soul, from the corpse to the player, as the official server's ServerBeamEffect.
            var (px, py) = player.Walk.Position(now);
            foreach (var (soulId, sx, sy) in player.SoulEater.Souls)
            {
                uint beam = player.TakeObjectId();
                bytes.Create(beam, InstanceEntities.CreateObject(beam, InstanceEntities.ObjectTypeHash(BeamEffectObject.TypePath),
                    new BeamEffectObject(sx, sy, soulId, px * BeamEffectObject.WorldPerCell,
                        py * BeamEffectObject.WorldPerCell, player.Id).Blob()));
                bytes.Presentation(beam, InstanceProtocol.EnterWakeZone(beam));
                // The connection owns the expiry so the replacement Game module can retire it after a reload.
                AddTemporarySkillEffect(player, beam, 0.6);
            }
            player.SoulEater.Souls.Clear();
        }
        if (auraChanged)
        {
            if (!active) player.Area.Buffs.Clear(player.Id, SoulEaterAuraRow, player.Id, auraSource: true);
            else player.Area.Buffs.Set(player.Id, new(SoulEaterAuraRow, player.Id, AuraSource: true, DisplayState: 0));
            player.SoulEater.AuraBuff = (ushort)(active ? 1 : 0);
        }
        if (stacks != player.SoulEater.SentStacks)
        {
            if (stacks == 0) player.Area.Buffs.Clear(player.Id, SoulEaterBuffRow, player.Id);
            else player.Area.Buffs.Set(player.Id, new(SoulEaterBuffRow, player.Id, Stacks: (ushort)Math.Min(ushort.MaxValue, stacks)));
        }
        if (statsChanged)
        {
            // Attack and cast speed are in the character's stats; the skill list carries the attack time the client
            // swings with, so both go out when a soul is gained or lost.
            bytes.World(player.Id, PlayerStatsOffFrames(player));
            bytes.World(player.Id, PlayerSkillsIfChanged(player, PlayerSkills(player)));
            player.SoulEater.SentStacks = SoulEaterCount(player, now);
            player.SoulEater.SentUsingFlask = usingFlask;
            Console.WriteLine($"[gateway] {CharacterName(player)} Soul Eater {player.SoulEater.SentStacks}/{player.SoulEater.Limit}");
        }
        return bytes.Build();
    }

    public static Task SendSoulEater(Player player) =>
        SendWorldBatch(player, [], admission => SoulEaterRecords(player, new(player, admission)));

    // The souls the client should see: none while the source is off or the player is dead, otherwise the decaying count.
    static int SoulEaterCount(Player player, double now) =>
        player.SoulEater.Enabled && player.PlayerLife > 0 ? player.SoulEater.Stacks(now) : 0;
}
