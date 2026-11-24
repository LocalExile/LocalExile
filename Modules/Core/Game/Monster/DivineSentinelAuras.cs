using GameServer.Core;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static readonly Lazy<PlayerMinions.Aura[]> DivineSentinelAuras = new(() =>
    {
        var definitions = ClientTables.Table("BuffDefinitions");
        return [.. ClientTables.Table("BuffTemplates").Where(row =>
            row.Text("Id")?.StartsWith("DivineSentinelAura", StringComparison.Ordinal) == true).Select(row =>
        {
            int buff = row.Int("BuffDefinitionsKey");
            var stats = definitions[buff].Ints("StatsKeys").Zip(row.Ints("Buff_StatValues"),
                (stat, value) => ((uint)stat + 1, value)).ToArray();
            return new PlayerMinions.Aura(row.Text("Id")!, checked((ushort)buff), row.Int("AuraRadius"), stats)
            { SharesWithAllies = true };
        })];
    });
    static readonly RuleTable<GameArea, Dictionary<uint, (Player Owner, PlayerMinions.Minion Source)>> DivineSentinels = new();

    static PlayerMinions.Aura? RollDivineSentinelAura(Player owner, GolemState.Golem minion, PlayerMinions.Aura? retained = null)
    {
        string kind = minion.Definition.Kind;
        if (kind is not ("Purity" or "Absolution" or "VaalAbsolution" or "VaalDomination")
            && !kind.StartsWith("Dominating", StringComparison.Ordinal)) return null;
        if (GolemStat(minion.Own, Stat.SummonedSentinelsHaveRandomTemplarAura) == 0) return null;
        var choices = DivineSentinelAuras.Value;
        return retained is { SharesWithAllies: true } ? retained
            : choices.Length > 0 ? choices[AreaRng(owner).Next(choices.Length)] : null;
    }

    static bool DivineSentinelAuraActive(Player owner, PlayerMinions.Minion source, double now) =>
        owner.PlayerLife > 0 && !owner.Closed && !owner.LeavingArea && !source.Monster.Dead
        && now < owner.Reservations.Until.GetValueOrDefault(source.Id, owner.Summons.Until.GetValueOrDefault(source.Id))
        && OwnedFighter(owner, source.Id) is { } fighter
        && ReferenceEquals(fighter.Monster, source.Monster)
        && GolemStat(fighter.Own, Stat.SummonedSentinelsHaveRandomTemplarAura) != 0;

    static void SyncDivineSentinelAura(Player owner, PlayerMinions.Minion source, PlayerMinions.Aura aura, double now)
    {
        if (owner.Frames.Area is not { } area) return;
        if (!DivineSentinelAuraActive(owner, source, now))
        {
            ClearDivineSentinelAura(owner, source.Id, aura);
            return;
        }
        DivineSentinels.GetOrCreateValue(area)[source.Id] = (owner, source);
        int radius = (int)Math.Round(aura.Radius * Math.Sqrt(Math.Max(0, 1
            + source.Monster.Effects[Stat.BaseSkillAreaOfEffectPlusPct] / 100d)));
        SyncZones(owner, source.Id, MinionAuraZones, [(0, radius, source.Monster.Walk.Position(now))],
            new(ReachesAlliesBut(owner, 0), new(source.Id), recipient =>
            {
                if (InZone(owner, source.Id, MinionAuraZones, 0, recipient.Value))
                    owner.Area.Buffs.Set(recipient.Value, new(aura.BuffRow, source.Id,
                        Values: [.. aura.Stats.Select(stat => unchecked((uint)stat.Value))]));
                else owner.Area.Buffs.Clear(recipient.Value, aura.BuffRow, source.Id);
                InvalidateSentinelAuraRecipient(owner, recipient.Value);
            }));
    }

    static void ClearDivineSentinelAura(Player owner, uint source, PlayerMinions.Aura aura)
    {
        if (owner.Frames.Area is not { } area || !DivineSentinels.TryGetValue(area, out var emitters)
            || !emitters.Remove(source)) return;
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
        PresentNearby(player);
    }

    static IEnumerable<PlayerMinions.Aura> DivineSentinelAurasReaching(Player recipient, uint id)
    {
        if (recipient.Frames.Area is not { } area || !DivineSentinels.TryGetValue(area, out var emitters) || emitters.Count == 0)
            return [];
        double now = AreaTime(recipient);
        return emitters.Values.Where(entry => DivineSentinelAuraActive(entry.Owner, entry.Source, now)
                && InZone(entry.Owner, entry.Source.Id, MinionAuraZones, 0, id))
            .Select(entry => entry.Source.OwnerAura!).DistinctBy(aura => aura.BuffRow);
    }
}
