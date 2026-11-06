using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

// Shared transaction boundary for a module-prepared character build after the old session has saved.
static class CharacterBuildImports
{
    internal sealed record Prepared(Guid CharacterId, Guid AccountId, string League, CharacterClass Class,
        int Level, CharacterSaves.Save Save, string Summary, string[] Warnings);

    internal sealed class Session(TimeProvider? time = null)
    {
        readonly TimeProvider clock = time ?? TimeProvider.System;
        Prepared? pending;
        long preparedAt;
        public Prepared? Confirmed { get; private set; }
        public void Clear() { pending = null; Confirmed = null; }
        public void Stage(Prepared build) { Clear(); pending = build; preparedAt = clock.GetTimestamp(); }
        public Prepared? Confirm()
        {
            var build = pending;
            pending = null;
            return build is not null && clock.GetElapsedTime(preparedAt) < TimeSpan.FromMinutes(5) ? build : null;
        }
        public void Accept(Prepared build) => Confirmed = build;
        public bool HasPending => pending is not null;
    }

    internal static readonly ConcurrentDictionary<Guid, byte> Applying = new();
    internal static readonly byte[] Replaced = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 14, 15, 23, 102];
    internal static bool IsApplying(Guid id) => Applying.ContainsKey(id);

    internal static void Release(ClientState conn)
    {
        if (conn.CharacterId is { } id && conn.BuildImport.Confirmed is not null) Applying.TryRemove(id, out _);
        conn.BuildImport.Clear();
    }

    // Called after all old-session writers and leaving hooks, while the instance socket is still open.
    internal static void Apply(Prepared plan)
    {
        Saves.Atomically(() =>
        {
            var accounts = new SqliteAccountStore(Saves.Db);
            var record = accounts.GetCharacter(plan.CharacterId) ?? throw new InvalidDataException("Персонаж удалён.");
            if (record.AccountId != plan.AccountId || record.League != plan.League || record.HardcoreDeath is not null)
                throw new InvalidDataException("Владелец, лига или состояние персонажа изменились.");
            var current = CharacterSaves.Load(record.Name) ?? new CharacterSaves.Save([]);
            var merged = Merge(current, plan.Save);
            Saves.Db.SetMeta($"pob-backup/{record.Id:N}/{Guid.NewGuid():N}", JsonSerializer.Serialize(new { Record = record, Save = current }));
            CharacterSaves.Write(record.Name, merged);
            accounts.UpdateCharacter(record with { Class = plan.Class, Level = plan.Level });
            CharacterSaves.ForgetExperience(record.Name);
            PlayerInventories.Invalidate(record.Name);
        });
    }

    internal static CharacterSaves.Save Merge(CharacterSaves.Save current, CharacterSaves.Save build)
    {
        var inventories = current.Inventories.Where(kv => !Replaced.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        uint next = inventories.Values.SelectMany(v => v).Select(i => i.Id).DefaultIfEmpty(0u).Max();
        foreach (byte row in Replaced)
            if (build.Inventories.TryGetValue(row, out var items))
                inventories[row] = items.Select(i => i with { Id = checked(++next) }).ToList();
        return current with
        {
            Inventories = inventories, SkillBar = build.SkillBar, Experience = build.Experience, Passives = build.Passives,
            MasteryEffects = build.MasteryEffects, Tattoos = build.Tattoos, TattooKeystones = build.TattooKeystones,
            ExtraPassivePoints = build.ExtraPassivePoints, ClusterPassives = build.ClusterPassives, MainAscendancy = build.MainAscendancy,
            PantheonMajor = build.PantheonMajor, PantheonMinor = build.PantheonMinor,
            ActiveAuras = null, Life = null, Mana = null, VitalsOwner = null, Tutorial = null, StartWeapon = null,
            AttackInPlaceSlots = null, SkillBarMask52 = null,
        };
    }

}
