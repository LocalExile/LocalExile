using GameServer.Core;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static PlayerMinions.Aura[] DivineSentinelAuras = [];
    static readonly RuleTable<GameArea, Dictionary<uint, (Player Owner, PlayerMinions.Minion Source)>> SharedMinionAuraEmitters = new();

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

    static bool SharedMinionAuraActive(Player owner, PlayerMinions.Minion source, double now)
    {
        if (owner.PlayerLife == 0 || owner.Closed || owner.LeavingArea || source.Monster.Dead || source.Monster.Life == 0
            || !ReferenceEquals(owner.Area.Monsters.GetValueOrDefault(source.Id), source.Monster)) return false;
        if (IsRelicSkill(source.Skill)) return now < source.Until;
        return now < owner.Reservations.Until.GetValueOrDefault(source.Id, owner.Summons.Until.GetValueOrDefault(source.Id))
            && OwnedFighter(owner, source.Id) is { } fighter
            && ReferenceEquals(fighter.Monster, source.Monster)
            && GolemStat(fighter.Own, Stat.SummonedSentinelsHaveRandomTemplarAura) != 0;
    }

    static void SyncSharedMinionAura(Player owner, PlayerMinions.Minion source, PlayerMinions.Aura aura, double now)
    {
        if (owner.Frames.Area is not { } area) return;
        if (!SharedMinionAuraActive(owner, source, now))
        {
            ClearSharedMinionAura(owner, source.Id, aura);
            return;
        }
        SharedMinionAuraEmitters.GetOrCreateValue(area)[source.Id] = (owner, source);
        int radius = (int)Math.Round(aura.Radius * Math.Sqrt(Math.Max(0, 1
            + source.Monster.Effects[Stat.BaseSkillAreaOfEffectPlusPct] / 100d)));
        SyncZones(owner, source.Id, MinionAuraZones, [(0, radius, source.Monster.Walk.Position(now))],
            new(ReachesAlliesBut(owner, 0), new(source.Id), recipient =>
            {
                bool inside = InZone(owner, source.Id, MinionAuraZones, 0, recipient.Value);
                if (recipient.Value == owner.Id) source.OwnerBuff = inside ? (ushort)1 : (ushort)0;
                if (inside)
                    owner.Area.Buffs.Set(recipient.Value, new(aura.BuffRow, source.Id,
                        Values: [.. aura.Stats.Select(stat => unchecked((uint)stat.Value))]));
                else owner.Area.Buffs.Clear(recipient.Value, aura.BuffRow, source.Id);
                InvalidateSentinelAuraRecipient(owner, recipient.Value);
            }));
    }

    static void ClearSharedMinionAura(Player owner, uint source, PlayerMinions.Aura aura)
    {
        if (owner.Frames.Area is not { } area || !SharedMinionAuraEmitters.TryGetValue(area, out var emitters)
            || !emitters.Remove(source, out var emitter)) return;
        emitter.Source.OwnerBuff = 0;
        var recipients = ZoneMembers(owner, source, MinionAuraZones, 0).ToArray();
        DropZones(owner, source, MinionAuraZones);
        foreach (var recipient in recipients)
        {
            owner.Area.Buffs.Clear(recipient.Value, aura.BuffRow, source);
            InvalidateSentinelAuraRecipient(owner, recipient.Value);
        }
    }

    static void InvalidateSentinelAuraRecipient(Player owner, uint recipient)
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
        return emitters.Values.Where(entry => SharedMinionAuraActive(entry.Owner, entry.Source, now)
                && InZone(entry.Owner, entry.Source.Id, MinionAuraZones, 0, id))
            .Select(entry => entry.Source.OwnerAura!);
    }

    static bool PlayerAuraAtLeastAsStrong(Player player, PlayerMinions.Aura minion, IEnumerable<AuraCatalog.Aura> auras, int effect)
    {
        var same = auras.Where(a => AuraCatalog.Family(a.EffectId) == AuraCatalog.Family(minion.EffectId)).ToArray();
        return same.Length > 0 && AuraCatalog.RawStats(same, effect, SkillsFor(player), Stats).Sum(s => (long)s.Value)
            >= minion.Stats.Sum(s => (long)s.Value);
    }
}
