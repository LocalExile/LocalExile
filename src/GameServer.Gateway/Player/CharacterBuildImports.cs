using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;

// Shared transaction boundary for a mod-prepared character build after the old session has saved.
static class CharacterBuildImports
{
    internal const int MaxJsonLength = 8 * 1024 * 1024;

    internal static CharacterBuildImports.Prepared Validate(string text, Character record)
    {
        if (text.Length > MaxJsonLength) throw new InvalidDataException("Ответ внешнего конвертера слишком большой.");
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (root.GetProperty("Version").GetInt32() != 1)
            throw new InvalidDataException("Неподдерживаемая версия формата импорта.");
        var converted = root.GetProperty("Record").Deserialize<Character>()
            ?? throw new InvalidDataException("Нет персонажа в результате импорта.");
        var save = root.GetProperty("Save").Deserialize<CharacterSaves.Save>()
            ?? throw new InvalidDataException("Нет сохранения в результате импорта.");
        if (converted.Id != record.Id || converted.AccountId != record.AccountId || converted.Name != record.Name
            || converted.League != record.League || converted.Level is < 1 or > 100 || !Enum.IsDefined(converted.Class)
            || save.Inventories is null || save.Inventories.Keys.Any(r => !CharacterBuildImports.Replaced.Contains(r))
            || save.Inventories.Values.Any(v => v is null) || save.Inventories.Values.Sum(v => v.Count) > 1024)
            throw new InvalidDataException("Некорректный результат внешнего конвертера.");
        foreach (var item in save.Inventories.Values.SelectMany(v => v))
            _ = CharacterSaves.FromSaved(item.Item, Program.Items).Payload();
        string summary = root.GetProperty("Summary").GetString() ?? "";
        string[] warnings = root.GetProperty("Warnings").Deserialize<string[]>() ?? [];
        if (summary.Length > 800 || warnings.Length > 100 || warnings.Any(w => w is null || w.Length > 800))
            throw new InvalidDataException("Слишком длинное описание результата импорта.");
        return new(record.Id, record.AccountId, record.League, converted.Class, converted.Level, save, summary, warnings);
    }

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
        string? replaced = null;
        Saves.Atomically(() =>
        {
            var accounts = new SqliteAccountStore(Saves.Db);
            var record = accounts.GetCharacter(plan.CharacterId) ?? throw new InvalidDataException("Персонаж удалён.");
            if (record.AccountId != plan.AccountId || record.League != plan.League || record.HardcoreDeath is not null)
                throw new InvalidDataException("Владелец, лига или состояние персонажа изменились.");
            var current = CharacterSaves.Load(record.Name) ?? new CharacterSaves.Save([]);
            var merged = Merge(current, plan.Save);
            Saves.Db.SetMeta($"character-build-backup/{record.Id:N}/{Guid.NewGuid():N}", JsonSerializer.Serialize(new { Record = record, Save = current }));
            CharacterSaves.Write(record.Name, merged);
            accounts.UpdateCharacter(record with { Class = plan.Class, Level = plan.Level });
            replaced = record.Name;
            CharacterSaves.ForgetExperience(record.Name);
            PlayerInventories.Invalidate(record.Name);
        });
        // A rollback leaves the old build and all its derived identities intact.
        CharacterEquipmentState.Replaced(replaced!);
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
            ActiveAuras = null, PetrifiedBlood = null, Stance = null,
            ModuleData = current.ModuleData?.Where(kv => kv.Key != "Game.Golems").ToDictionary(kv => kv.Key, kv => kv.Value),
            Life = null, Mana = null, VitalsOwner = null, Tutorial = null, StartWeapon = null,
            AttackInPlaceSlots = null, SkillBarMask52 = null,
        };
    }

}
