using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Blight;

/// <summary>
/// Sister Cassia in Blight encounters (<c>Metadata/Monsters/Masters/BlightBuilderWild</c>, NPCs row 396):
/// stands beside the Blight Pump and plays her authentic <c>NPCTextAudio</c> voice lines (<c>0x01c7</c>) on encounter
/// start, victory and failure. Each party member receives its own freshly allocated packet buffer.
/// </summary>
public static class BlightCassia
{
    public const string IntroVoice = "BlightBuilderIntroduction";
    public const string StartVoice = "BlightBuilderQuest";
    public const string VictoryVoice = "BlightBuilderQuestComplete";
    public const string FailureVoice = "BlightBuilderQuestFailed";

    /// <summary>Spawns Sister Cassia beside the pump once per defence.</summary>
    public static async Task SpawnNearPump(Player player, Defence d)
    {
        lock (d)
        {
            if (d.CassiaId != 0) return;
        }
        if (Monsters.Find(BlightTables.CassiaWildVariety) is not { Supported: true } variety) return;
        var cell = player.Layout.Near((d.Cell.X + 8, d.Cell.Y - 4));
        uint id = await SpawnMonster(player, variety, d.Level, MonsterRarity.Normal, cell.X, cell.Y,
            friendly: true, log: false, alone: false, instanceOwned: true,
            owned: (_, m) => { m.NonBlocking = true; m.NoRewards = true; m.DamageImmune = true; });
        if (id == 0) return;
        bool keep;
        lock (d)
        {
            keep = d.CassiaId == 0;
            if (keep) d.CassiaId = id;
        }
        if (!keep)
        {
            if (player.Area.Monsters.Remove(id, out var orphan)) { orphan.Dead = true; orphan.Life = 0; orphan.ActionGeneration++; }
            player.Instance?.World.MonsterOrigins.TryRemove(id, out _);
            await SendWorldRemoval(player, id, $"Duplicate Sister Cassia 0x{id:x} removed");
        }
    }

    /// <summary>Sends a random Cassia NPCTextAudio line of <paramref name="voiceId"/> to every player in the instance.</summary>
    public static async Task Say(Player player, string voiceId, Func<Player, IReadOnlyList<Player>> membersOf, uint delayMs = 0)
    {
        if (!BlightTables.CassiaVoices.TryGetValue(voiceId, out var rows) || rows.Length == 0) return;
        ushort row = rows[Random.Shared.Next(rows.Length)];
        foreach (var member in membersOf(player))
            await Send(member, InstanceNpcs.TextAudio(0, BlightTables.CassiaNpcRow, row, delayMs),
                $"0x01c7 Sister Cassia {voiceId} (row {row})");
    }
}
