using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const int StormbindRunePitch = 14;
    const int StormbindRange = 100;
    // At +90% area, 27 base cells give the fully improved rune's 36-cell radius.
    const int StormbindExplosionRadius = 27;
    const double StormbindChainStep = .06;
    const double StormbindTeleportDelay = .1;
    const string StormbindArt = "Metadata/Effects/Spells/channelled_run_explosion/";
    static readonly (int X, int Y)[] StormbindNeighbours =
        [(0, -1), (1, 0), (0, 1), (-1, 0), (1, -1), (1, 1), (-1, 1), (-1, -1)];
    static readonly RuleTable<Player, StormbindChannel> StormbindChannels = new();
    static readonly RuleTable<GameArea, Dictionary<(int X, int Y), List<StormbindRune>>> StormbindFields = new();

    internal sealed record StormbindSource((ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Skill,
        SupportedGem Gem, GemSourceLifetimes.SourceLifetime Lifetime, ushort Sequence)
    {
        public bool Current(Player player) => GemSourceLifetimeCurrentLocked(player, Skill.Id, Skill.Effect, Lifetime)
            && SameTriggerSource(Gem, SupportedSkill(player, Skill.Id))
            && GemSkillInstanceDisabled(player, Skill.Effect, Skill.Id) is null;
    }

    sealed class StormbindChannel(StormbindSource source, int generation, int action, int interruption, int epoch,
        (int X, int Y) aim, bool blast, bool teleports, uint initialMana, double startedAt)
    {
        public readonly StormbindSource Source = source;
        public readonly int Generation = generation, Action = action, Interruption = interruption, Epoch = epoch;
        public readonly bool Blast = blast, Teleports = teleports;
        public readonly uint InitialMana = initialMana;
        public readonly double StartedAt = startedAt;
        public (int X, int Y) Aim = aim;
        public (int X, int Y)? Landing;
    }

    sealed class StormbindRune(ProjectileSession owner, StormbindSource source, (int X, int Y) cell, double expires, double more, Action remove, Action<uint> animate)
    {
        public readonly StormbindSource Source = source;
        public readonly ProjectileSession Owner = owner;
        public readonly (int X, int Y) Cell = cell;
        public readonly double Expires = expires, More = more;
        public readonly Action RemoveArt = remove;
        public readonly Action<uint> Animate = animate;
        public readonly int Hinder = -source.Gem.Stats.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct);
        public readonly int UpgradeCost = source.Gem.Stats.GetValueOrDefault(Stat.RunePaintManaSpendPerRuneUpgrade);
        public readonly int MaxLevel = source.Gem.Stats.GetValueOrDefault(Stat.RunePaintMaxRuneLevel);
        public int Level;
        public uint SpentMana;
    }

    static bool IsStormbind(Player player, int effect) =>
        SkillsFor(player).EffectId(effect) is "Stormbind" or "StormbindAltX" or "StormbindAltY" or "RuneBlast" or "RuneBlastAltX";

    static void BeginStormbind(Player player, SupportedGem supported,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) skill, ushort sequence, int generation,
        int action, (int X, int Y) aim, uint mana)
    {
        if (CaptureGemSourceLifetimeLocked(player, skill.Id, skill.Effect) is not { } lifetime) return;
        var id = SkillsFor(player).EffectId(skill.Effect);
        StormbindChannels.AddOrUpdate(player, new(new(skill, supported, lifetime, sequence), generation, action,
            player.ActionInterruptionGeneration, player.ProjectileEpoch, aim, id is "RuneBlast" or "RuneBlastAltX",
            id == "RuneBlastAltX", mana, AreaTime(player)));
    }

    static StormbindChannel? CurrentStormbind(Player player) =>
        StormbindChannels.TryGetValue(player, out var channel) && channel.Generation == player.ChannelGeneration
            && channel.Source.Skill.Id == player.ChannelSkill && channel.Action == player.ActionCount ? channel : null;

    static bool StormbindChannelCurrent(Player player, StormbindChannel channel) =>
        CurrentGameWork && ReferenceEquals(channel, CurrentStormbind(player)) && player.ProjectileActive(channel.Epoch)
        && channel.Interruption == player.ActionInterruptionGeneration && player.PlayerLife > 0 && !player.RageRetired
        && channel.Source.Current(player);

    static bool AimStormbind(Player player, (int X, int Y) aim)
    {
        if (CurrentStormbind(player) is not { } channel || aim == channel.Aim) return false;
        channel.Aim = aim;
        return true;
    }

    static void RetireStormbind(Player player, int generation)
    {
        if (StormbindChannels.TryGetValue(player, out var channel) && channel.Generation == generation)
            StormbindChannels.Remove(player);
    }

    static void PulseStormbind(Player player, StormbindChannel channel, uint mana, double pact)
    {
        using var mutation = ProjectileMutation(player, advance: false);
        if (!StormbindChannelCurrent(player, channel)) return;
        var session = ProjectilesFor(player);
        double now = AreaTime(player);
        if (channel.Blast) session.ImproveStormbindRunes(channel.Aim, mana, now);
        else
        {
            int focused = channel.Source.Gem.Stats.GetValueOrDefault(Stat.SupportFocusChannelDamagePlusPctFinalPerSecondChannellingUpTo60Pct);
            session.PlaceStormbindRune(channel.Source, channel.Aim, now, pact * FocusedChannelMore(now - channel.StartedAt, focused));
        }
    }

    static (int X, int Y)? ReleaseStormbindLocked(Player player, double at)
    {
        if (CurrentStormbind(player) is not { } channel) return null;
        bool releases = channel.Blast && StormbindChannelCurrent(player, channel) && !PlayerActionBlocked(player, at, at);
        RetireStormbind(player, channel.Generation);
        if (releases && player.ProjectileRuntime is ProjectileSession { Active: true } session)
            channel.Landing = session.DetonateStormbindRunes(channel.Aim, channel.Teleports, at);
        return channel.Landing;
    }

    static (int X, int Y) StormbindCell((int X, int Y) point) =>
        ((point.X + StormbindRunePitch / 2) / StormbindRunePitch, (point.Y + StormbindRunePitch / 2) / StormbindRunePitch);

    static (int X, int Y) StormbindPoint((int X, int Y) cell) =>
        (cell.X * StormbindRunePitch, cell.Y * StormbindRunePitch);

    static (int X, int Y) StormbindNext((int X, int Y) cell, (int X, int Y) offset) =>
        (cell.X + offset.X, cell.Y + offset.Y);

    static bool StormbindLine(Player player, (int X, int Y) from, (int X, int Y) to)
    {
        if (player.Layout.Sight is { } sight && ClientPathfinder.LineBelow(from, to, sight, 1)) return false;
        return player.SightBarrier.Count == 0 || !ClientPathfinder.Line(from, to).Any(player.SightBarrier.Contains);
    }

    static int StormbindHinder(GameArea area, SpawnedMonster monster, double at)
    {
        if (!StormbindFields.TryGetValue(area, out var grid)
            || grid.Count == 0 || !monster.Hostile
            || !grid.TryGetValue(StormbindCell(monster.Walk.Position(at)), out var runes)
            || monster.Effects[Stat.YouCannotBeHindered] != 0 || monster.Effects[Stat.ImmuneToHinder] != 0) return 0;
        int strongest = 0;
        foreach (var rune in runes)
            if (at < rune.Expires && rune.Owner.Active) strongest = Math.Max(strongest, rune.Hinder);
        return strongest;
    }

    public sealed partial class ProjectileSession
    {
        Dictionary<(int X, int Y), StormbindRune>? stormbindRunes;
        GameArea? stormbindArea;
        double lastStormbindDetonation = double.NegativeInfinity;

        bool StormbindOnGrid((int X, int Y) at) =>
            (uint)at.X < (uint)Player.Layout.GridW && (uint)at.Y < (uint)Player.Layout.GridH;

        bool StormbindWalkable((int X, int Y) at) => Player.Layout.WalkableFor(at.X, at.Y, 0) && !Player.Barrier.Contains(at);

        internal void PlaceStormbindRune(StormbindSource source, (int X, int Y) aim, double now, double more)
        {
            var runes = stormbindRunes ??= [];
            var origin = Player.Walk.Position(now);
            if (!StormbindOnGrid(aim) || Dist(origin, aim) > StormbindRange) return;
            var start = StormbindCell(aim);
            if (!Available(start)) return;

            // Spread through the owner's existing neighbours, so a blocked tile cannot connect separate rooms.
            var cell = start;
            if (runes.ContainsKey(cell))
            {
                var pending = new Queue<(int X, int Y)>();
                var seen = new HashSet<(int X, int Y)> { start };
                pending.Enqueue(start);
                while (pending.TryDequeue(out var from))
                {
                    foreach (var offset in StormbindNeighbours)
                    {
                        var to = StormbindNext(from, offset);
                        if (!Available(to) || !StormbindLine(Player, StormbindPoint(from), StormbindPoint(to)) || !seen.Add(to)) continue;
                        if (!runes.ContainsKey(to)) { cell = to; break; }
                        pending.Enqueue(to);
                    }
                    if (cell != start) break;
                }
                if (cell == start) return;
            }

            var stats = source.Gem.Stats;
            double duration = SkillDurationModifiers.Seconds(stats.GetValueOrDefault(Stat.BaseSkillEffectDuration),
                CharacterPlayerStats(Player).GetValueOrDefault, stats.GetValueOrDefault);
            if (duration <= 0) return;
            var at = StormbindPoint(cell);
            Action? remove = null;
            Action<uint>? animate = null;
            // The rune's rig.amd indexes fadeout at 0 and the four charge states t1..t4 at 1..4.
            long expiresAt = ProjectileRuntime.Time(now + duration);
            long artLifetime = Math.Max(0, expiresAt - Runtime.Now);
            Effect(at.X, at.Y, StormbindArt + "rune/rig.ao", artLifetime, endClip: (0, ProjectileRuntime.Time(.8)),
                captureRemoval: action => remove = action, captureAnimation: action => animate = action);
            var rune = new StormbindRune(this, source, cell, now + duration, more, remove!, animate!);
            runes.Add(cell, rune);
            var area = stormbindArea ??= Player.Area;
            var field = StormbindFields.GetOrCreateValue(area);
            if (!field.TryGetValue(cell, out var stack)) field.Add(cell, stack = []);
            stack.Add(rune);
            Runtime.Schedule(expiresAt, () =>
            {
                if (runes.GetValueOrDefault(cell) == rune) runes.Remove(cell);
                RemoveStormbindRune(area, rune, retireArt: false);
            });
            TraceStormbind($"placed at={at} expires={rune.Expires:F3}");

            bool Available((int X, int Y) candidate)
            {
                var point = StormbindPoint(candidate);
                return Dist(origin, point) <= StormbindRange && StormbindWalkable(point) && StormbindLine(Player, origin, point);
            }
        }

        internal void ImproveStormbindRunes((int X, int Y) aim, uint mana, double now)
        {
            if (mana == 0 || StormbindAt(aim, now) is not { } target) return;
            Improve(target);
            var at = StormbindPoint(target.Cell);
            foreach (var offset in StormbindNeighbours)
            {
                var next = StormbindNext(target.Cell, offset);
                if (stormbindRunes!.TryGetValue(next, out var rune) && rune.Expires > now
                    && StormbindLine(Player, at, StormbindPoint(next))) Improve(rune);
            }

            void Improve(StormbindRune rune)
            {
                if (mana == 0 || rune.UpgradeCost <= 0 || rune.Level >= rune.MaxLevel
                    || !rune.Source.Current(Player)) return;
                uint cost = (uint)rune.UpgradeCost;
                ulong totalNeeded = (ulong)(rune.MaxLevel - rune.Level) * cost;
                ulong remaining = totalNeeded > rune.SpentMana ? totalNeeded - rune.SpentMana : 0;
                uint add = (uint)Math.Min(mana, remaining);
                if (add == 0) return;
                mana -= add;
                rune.SpentMana += add;
                while (rune.SpentMana >= cost && rune.Level < rune.MaxLevel)
                {
                    rune.SpentMana -= cost;
                    rune.Level++;
                    rune.Animate((uint)Math.Min(4, rune.Level + 1));
                    TraceStormbind($"improved at={StormbindPoint(rune.Cell)} level={rune.Level} spent={cost}");
                }
            }
        }

        StormbindRune? StormbindAt((int X, int Y) aim, double now) =>
            StormbindOnGrid(aim) && stormbindRunes?.GetValueOrDefault(StormbindCell(aim)) is { } rune && rune.Expires > now
            && rune.Source.Current(Player) && StormbindLine(Player, Player.Walk.Position(now), StormbindPoint(rune.Cell)) ? rune : null;

        internal (int X, int Y)? DetonateStormbindRunes((int X, int Y) aim, bool teleports, double now)
        {
            if (stormbindArea is not { } area || StormbindAt(aim, now) is not { } first) return null;
            var pending = new Queue<(StormbindRune Rune, int Wave)>();
            pending.Enqueue((first, 0));
            stormbindRunes!.Remove(first.Cell);
            var crits = new Dictionary<ushort, SkillCriticalRoll>();
            while (pending.TryDequeue(out var step))
            {
                var (rune, wave) = step;
                var at = StormbindPoint(rune.Cell);
                if (!crits.TryGetValue(rune.Source.Skill.Id, out var critical))
                    crits[rune.Source.Skill.Id] = critical = CaptureSkillCriticalRoll(Player, rune.Source.Skill.Id);
                Runtime.Schedule(ProjectileRuntime.Time(now + wave * StormbindChainStep), () =>
                {
                    if (!Active || rune.Expires <= ProjectileWorldTime(Player)) return;
                    RemoveStormbindRune(area, rune);
                    if (Player.PlayerLife == 0 || !rune.Source.Current(Player)) return;
                    StormbindExplosion(rune, at, critical, wave);
                });
                foreach (var offset in StormbindNeighbours)
                {
                    var next = StormbindNext(rune.Cell, offset);
                    if (!stormbindRunes.TryGetValue(next, out var adjacent) || adjacent.Expires <= now
                        || !adjacent.Source.Current(Player) || !StormbindLine(Player, at, StormbindPoint(next))) continue;
                    stormbindRunes.Remove(next);
                    pending.Enqueue((adjacent, wave + 1));
                }
            }

            bool canTeleport = now - lastStormbindDetonation >= StormbindTeleportDelay;
            lastStormbindDetonation = now;
            var landing = StormbindPoint(first.Cell);
            if (!teleports || !canTeleport || !StormbindWalkable(landing)
                || CharacterPlayerStats(Player).GetValueOrDefault(Stat.YourMovementSkillsAreDisabled) != 0
                || Player.Blockers.Any(b => Math.Max(Math.Abs(landing.X - b.X), Math.Abs(landing.Y - b.Y)) - b.Size < 2))
                return null;
            Effect(landing.X, landing.Y, StormbindArt + "teleport/rig.ao", ProjectileRuntime.Time(.5));
            Player.Walk.Teleport(landing.X, landing.Y, now);
            TraceStormbind($"teleported at={landing}");
            return landing;
        }

        void StormbindExplosion(StormbindRune rune, (int X, int Y) at, SkillCriticalRoll critical, int wave)
        {
            var source = rune.Source;
            var own = source.Gem.Stats;
            var area = SkillAreaStats(Player, own, source.Skill.Effect);
            int baseRadius = area.GetValueOrDefault(Stat.ActiveSkillBaseAreaOfEffectRadius);
            if (baseRadius <= 0) baseRadius = StormbindExplosionRadius;
            double areaMore = 1 + rune.Level * (double)own.GetValueOrDefault(Stat.RunePaintAreaOfEffectPlusPctFinalPerRuneLevel) / 100;
            double damageMore = 1 + rune.Level * (double)own.GetValueOrDefault(Stat.RunePaintDamagePlusPctFinalPerRuneLevel) / 100;
            int radius = SkillArea.Radius(baseRadius, area.GetValueOrDefault, spell: true, areaMore);
            var art = rune.Level == 0 ? "rune/rune_explosion.ao" : "rune/rune_explosion_bigger.ao";
            Effect(at.X, at.Y, StormbindArt + art, ProjectileRuntime.Time(.6), radius);
            TraceStormbind($"detonated at={at} level={rune.Level} wave={wave} radius={radius} more={damageMore:F2}");
            var contact = AreaSkillHitForUse(null, Player, source.Skill.Id, source.Skill, at, at, immediate: true,
                sequence: source.Sequence, criticalRoll: critical, radiusOverride: radius, nearbyOnly: true,
                scheduledSession: this, triggeredCurrent: () => Active && Player.PlayerLife > 0,
                hitAndAilmentMore: damageMore * rune.More,
                targetFilter: (id, target) => StormbindLine(Player, at, target)
                    && Player.Area.Monsters.TryGetValue(id, out var monster) && !monster.WorldHidden);
            if (!contact.IsCompleted) throw new InvalidOperationException("Stormbind explosion contact must not yield.");
            contact.GetAwaiter().GetResult();
        }

        static void RemoveStormbindRune(GameArea area, StormbindRune rune, bool retireArt = true)
        {
            if (!StormbindFields.TryGetValue(area, out var field) || !field.TryGetValue(rune.Cell, out var stack)
                || !stack.Remove(rune)) return;
            if (stack.Count == 0) field.Remove(rune.Cell);
            if (retireArt) rune.RemoveArt();
        }

        void ClearStormbindRunes()
        {
            if (stormbindArea is not { } area) return;
            stormbindArea = null;
            stormbindRunes = null;
            if (area.Driven && !area.Owned) area.Defer(Remove);
            else Remove(area);

            void Remove(GameArea current)
            {
                if (!StormbindFields.TryGetValue(current, out var field)) return;
                // This includes pending detonations that have already left the owner's targetable grid.
                var owned = field.Values.SelectMany(stack => stack).Where(rune => rune.Owner == this).ToArray();
                foreach (var rune in owned) RemoveStormbindRune(current, rune);
            }
        }

        void TraceStormbind(string message)
        {
            if (ServerTrace.Projectiles) Console.WriteLine($"[gateway] {Player.Connection.Remote} Stormbind {message}");
        }
    }
}
