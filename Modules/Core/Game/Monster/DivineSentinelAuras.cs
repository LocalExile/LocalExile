using GameServer.Core;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static PlayerMinions.Aura[] DivineSentinelAuras = [];
    sealed record MinionAuraEmitter(Player Owner, PlayerMinions.Minion Source, bool RegisteredAura)
    {
        public PlayerMinions.Aura[] Auras = [];
        public double Until;
        public double RadiusScale;
    }
    static readonly RuleTable<GameArea, Dictionary<uint, MinionAuraEmitter>> SharedMinionAuraEmitters = new();

    static void LoadDivineSentinelAuraData()
    {
        var definitions = ClientTables.TableOrEmpty("BuffDefinitions");
        DivineSentinelAuras = [.. ClientTables.TableOrEmpty("BuffTemplates").Where(row =>
            row.Text("Id")?.StartsWith("DivineSentinelAura", StringComparison.Ordinal) == true).Select(row =>
        {
            int buff = row.Int("BuffDefinitionsKey");
            var stats = definitions[buff].Ints("StatsKeys").Zip(row.Ints("Buff_StatValues"),
                (stat, value) => ((uint)stat + 1, value)).ToArray();
            return new PlayerMinions.Aura(row.Text("Id")!, checked((ushort)buff), row.Int("AuraRadius"), stats)
            { SharesWithAllies = true };
        })];
    }

    static PlayerMinions.Aura? RollDivineSentinelAura(Player owner, GolemState.Golem minion, PlayerMinions.Aura? retained = null)
    {
        string kind = minion.Definition.Kind;
        if (kind is not ("Purity" or "Absolution" or "VaalAbsolution" or "VaalDomination")
            && !kind.StartsWith("Dominating", StringComparison.Ordinal)) return null;
        if (GolemStat(minion.Own, Stat.SummonedSentinelsHaveRandomTemplarAura) == 0) return null;
        var choices = DivineSentinelAuras;
        return retained is { SharesWithAllies: true } ? retained
            : choices.Length > 0 ? choices[AreaRng(owner).Next(choices.Length)] : null;
    }

    static bool SharedMinionAuraActive(MinionAuraEmitter emitter, double now)
    {
        var (owner, source, registered) = emitter;
        if (owner.PlayerLife == 0 || owner.Closed || owner.LeavingArea || source.Monster.Dead || source.Monster.Life == 0
            || now >= emitter.Until || !ReferenceEquals(owner.Area.Monsters.GetValueOrDefault(source.Id), source.Monster)) return false;
        if (!registered) return OwnedFighter(owner, source.Id) is { } native && ReferenceEquals(native.Monster, source.Monster);
        if (IsRelicSkill(source.Skill)) return now < source.Until;
        return now < owner.Reservations.Until.GetValueOrDefault(source.Id, owner.Summons.Until.GetValueOrDefault(source.Id))
            && OwnedFighter(owner, source.Id) is { } fighter
            && ReferenceEquals(fighter.Monster, source.Monster)
            && GolemStat(fighter.Own, Stat.SummonedSentinelsHaveRandomTemplarAura) != 0;
    }

    static void SyncSharedMinionAura(Player owner, PlayerMinions.Minion source, PlayerMinions.Aura aura, double now) =>
        SyncSharedMinionAuras(owner, source, [aura], now, double.PositiveInfinity, registered: true);

    static void SyncSharedMinionAuras(Player owner, PlayerMinions.Minion source, PlayerMinions.Aura[] auras, double now,
        double until, bool registered = false)
    {
        if (owner.Frames.Area is not { } area) return;
        if (auras.Length == 0)
        {
            ClearSharedMinionAuras(owner, source.Id);
            return;
        }
        var emitters = SharedMinionAuraEmitters.GetOrCreateValue(area);
        if (!emitters.TryGetValue(source.Id, out var emitter))
            emitters[source.Id] = emitter = new(owner, source, registered);
        emitter.Until = until;
        if (!SharedMinionAuraActive(emitter, now))
        {
            ClearSharedMinionAuras(owner, source.Id);
            return;
        }
        double radiusScale = Math.Sqrt(Math.Max(0, 1 + source.Monster.Effects[Stat.BaseSkillAreaOfEffectPlusPct] / 100d));
        if (emitter.RadiusScale == radiusScale && emitter.Auras.SequenceEqual(auras)) return;
        HashSet<EntityId>? changedRecipients = null;
        foreach (var old in emitter.Auras)
            if (!auras.Contains(old))
                foreach (var id in ZoneMembers(owner, source.Id, MinionAuraZones, old.BuffRow))
                {
                    owner.Area.Buffs.Clear(id.Value, old.BuffRow, source.Id);
                    (changedRecipients ??= []).Add(id);
                }
        emitter.Auras = auras;
        emitter.RadiusScale = radiusScale;
        var at = source.Monster.Walk.Position(now);
        SyncZones(owner, source.Id, MinionAuraZones,
            auras.Select(aura => ((uint)aura.BuffRow, (int)Math.Round(aura.Radius * radiusScale), at)),
            new(ReachesAlliesBut(owner, 0), new(source.Id), recipient => RefreshMinionAuraRecipient(emitter, recipient.Value)));
        if (changedRecipients is not null)
            foreach (var id in changedRecipients) RefreshMinionAuraRecipient(emitter, id.Value);
    }

    static void RefreshMinionAuraRecipient(MinionAuraEmitter emitter, uint recipient)
    {
        var (owner, source, registered) = emitter;
        bool active = SharedMinionAuraActive(emitter, AreaTime(owner));
        foreach (var aura in emitter.Auras)
        {
            bool inside = active && ReachesFromMinionAura(emitter, aura, recipient);
            if (registered && recipient == owner.Id) source.OwnerBuff = inside ? (ushort)1 : (ushort)0;
            if (inside)
                owner.Area.Buffs.Set(recipient, new(aura.BuffRow, source.Id,
                    Values: aura.BuffValues ?? [.. aura.Stats.Select(stat => unchecked((uint)stat.Value))]));
            else owner.Area.Buffs.Clear(recipient, aura.BuffRow, source.Id);
        }
        InvalidateMinionAuraRecipient(owner, recipient);
    }

    static bool ReachesFromMinionAura(MinionAuraEmitter emitter, PlayerMinions.Aura aura, uint recipient) =>
        (aura.AffectsSource || recipient != emitter.Source.Id)
        && InZone(emitter.Owner, emitter.Source.Id, MinionAuraZones, aura.BuffRow, recipient);

    static void ClearSharedMinionAuras(Player owner, uint source)
    {
        if (owner.Frames.Area is not { } area || !SharedMinionAuraEmitters.TryGetValue(area, out var emitters)
            || !emitters.Remove(source, out var emitter)) return;
        emitter.Source.OwnerBuff = 0;
        var recipients = new HashSet<EntityId>();
        foreach (var aura in emitter.Auras)
            foreach (var recipient in ZoneMembers(owner, source, MinionAuraZones, aura.BuffRow))
            {
                owner.Area.Buffs.Clear(recipient.Value, aura.BuffRow, source);
                recipients.Add(recipient);
            }
        DropZones(owner, source, MinionAuraZones);
        foreach (var recipient in recipients) InvalidateMinionAuraRecipient(owner, recipient.Value);
    }

    static void InvalidateMinionAuraRecipient(Player owner, uint recipient)
    {
        if ((owner.Id == recipient ? owner : AreaPlayer(owner, recipient)) is not { } player) return;
        player.Minions.StatRevision++;
        player.AuraStatsDirty = true;
        player.Pools = BuildPoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        player.EnergyShield.SetMaximum(player.Pools.EnergyShield);
        PresentNearby(player);
    }

    static IEnumerable<PlayerMinions.Aura> SharedMinionAurasReaching(Player recipient, uint id)
    {
        if (recipient.Frames.Area is not { } area || !SharedMinionAuraEmitters.TryGetValue(area, out var emitters) || emitters.Count == 0)
            return [];
        double now = AreaTime(recipient);
        return emitters.Values.Where(entry => SharedMinionAuraActive(entry, now))
            .SelectMany(entry => entry.Auras.Where(aura => ReachesFromMinionAura(entry, aura, id)));
    }

    static bool PlayerAuraAtLeastAsStrong(Player player, PlayerMinions.Aura minion, IEnumerable<AuraCatalog.Aura> auras, int effect)
    {
        var same = auras.Where(a => AuraCatalog.Family(a.EffectId) == AuraCatalog.Family(minion.EffectId)).ToArray();
        return same.Length > 0 && AuraCatalog.RawStats(same, effect, SkillsFor(player), Stats).Sum(s => (long)s.Value)
            >= minion.Stats.Sum(s => (long)s.Value);
    }
}
