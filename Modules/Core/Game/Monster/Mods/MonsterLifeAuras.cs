using GameServer.Core;
using GameServer.Core.Protocol.Instance;

// BuffTemplates expresses the radius in cells and names its recipients in __col4.
internal sealed record MonsterAuraTemplate(ushort Buff, int Radius, bool Self, bool Allies)
{
    public static MonsterAuraTemplate? Read(ModCatalog.Mod mod)
    {
        if (mod.BuffTemplate is not int row || ClientTables.Table("BuffTemplates").Row(row) is not { } template ||
            template.IntOrNull("BuffDefinitionsKey") is not int buff || buff <= 0 || template.Int("AuraRadius") <= 0)
            return null;
        var targets = template.Ints("__col4");
        return new(checked((ushort)buff), template.Int("AuraRadius"), targets.Contains(1), targets.Contains(2));
    }
}

public static partial class Game
{
    const string LifeAuraStat = "life_regeneration_rate_per_minute_%";
    const ushort MonsterLifeAuraBuff = 90; // monster_aura_life_regen

    sealed record LifeAuraMod(MonsterAuraTemplate Template, int StatIndex);
    readonly record struct LifeAuraRoll(MonsterAuraTemplate Template, int Rate);
    readonly record struct LifeAuraField(uint Source, int X, int Y, double RadiusSquared, LifeAuraRoll Roll);
    sealed class LifeAuraFields
    {
        public readonly List<LifeAuraField> Active = [];
        public readonly Dictionary<uint, int> IncludedInMods = [];
    }

    static readonly Lazy<Dictionary<ushort, LifeAuraMod>> LifeAuraMods = new(() =>
    {
        var rows = new Dictionary<ushort, LifeAuraMod>();
        if (!ClientTables.Has("BuffTemplates")) return rows;
        foreach (var mod in Items.Mods.All)
        {
            if (mod.Domain != MonsterModCatalog.Domain || mod.BuffTemplate is null) continue;
            for (int i = 0; i < mod.Stats.Count; i++)
                if (mod.Stats[i].Id == LifeAuraStat && MonsterAuraTemplate.Read(mod) is { Buff: MonsterLifeAuraBuff } aura)
                    rows.Add(mod.Hash, new(aura, i));
        }
        return rows;
    });

    static readonly RuleTable<IReadOnlyList<ItemMod>, LifeAuraRoll[]> RolledLifeAuras = new();

    static LifeAuraRoll[] ReadRolledLifeAuras(IReadOnlyList<ItemMod> mods) =>
        [.. from roll in mods
            let def = LifeAuraMods.Value.GetValueOrDefault(roll.Hash)
            where def is not null
            select new LifeAuraRoll(def.Template, roll.Values[def.StatIndex])];

    static LifeAuraFields? GatherLifeAuras(IReadOnlyDictionary<uint, SpawnedMonster> monsters, double now)
    {
        LifeAuraFields? fields = null;
        foreach (var (id, monster) in monsters)
        {
            var mods = monster.Mods.Mods;
            if (mods.Count == 0) continue;
            var rolls = RolledLifeAuras.GetValue(mods, ReadRolledLifeAuras);
            if (rolls.Length == 0) continue;
            fields ??= new();
            bool active = monster.Hostile && !monster.WorldHidden && !monster.Dead && monster.Life > 0;
            var at = monster.Walk.Position(now);
            double area = Math.Max(0, 1 + monster.Mods.Effects["base_aura_area_of_effect_+%"] / 100d);
            int included = 0;
            foreach (var roll in rolls)
            {
                included += roll.Rate;
                if (active)
                {
                    double radius = roll.Template.Radius;
                    fields.Active.Add(new(id, at.X, at.Y, radius * radius * area, roll));
                }
            }
            fields.IncludedInMods.Add(id, included);
        }
        return fields;
    }

    static void ApplyLifeAura(uint id, SpawnedMonster monster, LifeAuraFields? fields, double now)
    {
        if (fields is null && monster.LifeAura == default) return;
        uint source = 0;
        int rate = 0;
        bool emits = false;
        if (fields is not null && monster.Hostile && !monster.WorldHidden && !monster.Dead && monster.Life > 0)
        {
            var at = monster.Walk.Position(now);
            foreach (var field in fields.Active)
            {
                bool self = field.Source == id;
                emits |= self;
                var aura = field.Roll.Template;
                if (!(self ? aura.Self : aura.Allies) || field.Roll.Rate < rate ||
                    (field.Roll.Rate == rate && field.Source >= source)) continue;
                double dx = (double)at.X - field.X, dy = (double)at.Y - field.Y;
                if (dx * dx + dy * dy > field.RadiusSquared) continue;
                source = field.Source;
                rate = field.Roll.Rate;
            }
        }
        // A rolled aura already contributes to its bearer's own modifier total. Only the strongest
        // instance of buff 90 applies. Ordinary regeneration remains additive.
        int extra = rate - (fields?.IncludedInMods.GetValueOrDefault(id) ?? 0);
        if (monster.LifeAura == (emits, source, rate, extra)) return;
        monster.LifeAura = (emits, source, rate, extra);
        if (extra == 0) monster.TemporaryStats.Remove("monster_aura_life_regen");
        else monster.TemporaryStats["monster_aura_life_regen"] = new Dictionary<string, int> { [LifeAuraStat] = extra };
    }

    static bool LifeAuraChanged(SpawnedMonster monster, MonsterView view)
    {
        var active = monster.Dead || monster.Life == 0 ? default : monster.LifeAura;
        var old = view.LifeAura;
        return (old.EmitterBuff != 0) != active.Emits || old.Source != active.Source || old.Rate != active.Rate;
    }

    // BuffVisuals 94 has separate EPKs for the emitter and the recipient. The latter uses a viewer-local
    // buff instance even when two monsters receive the same emitter's aura.
    static MonsterView PresentLifeAura(ClientState conn, WorldRecordBuilder packets, uint id, SpawnedMonster monster, MonsterView view)
    {
        var old = view.LifeAura;
        var active = monster.Dead || monster.Life == 0 ? default : monster.LifeAura;
        // A framed observer gets this view again after the new source's birth is written.
        if (active.Source != 0 && active.Source != id && (!conn.MonsterViews.ContainsKey(active.Source) ||
            conn.LockstepClient && !conn.WorldOutput.PublishedObject(active.Source).IsReady)) return view;
        ushort emitter = old.EmitterBuff, receiver = old.ReceiverBuff;
        if (active.Emits && emitter == 0)
            packets.Presentation(id, InstanceEntities.AddBuff(id, emitter = conn.NextBuff++, MonsterLifeAuraBuff, id, auraSource: true, displayState: 0));
        if (!active.Emits && emitter != 0)
        {
            packets.Presentation(id, InstanceEntities.RemoveBuff(id, emitter));
            emitter = 0;
        }
        if (old.Source != active.Source)
        {
            if (receiver != 0) packets.Presentation(id, InstanceEntities.RemoveBuff(id, receiver));
            receiver = 0;
            if (active.Source != 0)
                packets.Presentation(id, InstanceEntities.AddBuff(id, receiver = conn.NextBuff++, MonsterLifeAuraBuff, active.Source));
        }
        if (old.Rate != active.Rate && !monster.Dead && monster.Life > 0)
            packets.Presentation(id, InstanceEntities.StatsUpdate(id, [(Stats.Key(LifeAuraStat)!.Value, monster.Effects[LifeAuraStat])]));
        return view with { LifeAura = (emitter, receiver, active.Source, active.Rate) };
    }
}
