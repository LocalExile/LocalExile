using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Legion;

/// <summary>
/// The Legion generals' lines (R14): S->C 0x1c7 NPCTextAudio, the speaker's text and voice. The rows come from client.db
/// (<see cref="LegionData.VoiceOf"/>, by the stem of the NPCTextAudio Id), sent to the players in the area, played globally
/// (object id 0, as Sirus does: the general may be gone when a delayed line is due).
/// </summary>
static class LegionSpeech
{
    /// <summary>GUESS: a Domain general's MapIntro comes this long after the start, and each next general's this much later again
    /// (two lines never overlap).</summary>
    public const uint DomainIntroDelayMs = 8000, DomainIntroStaggerMs = 5000;
    /// <summary>GUESS: the shortest time between two BattleCry or Dismount lines of one general in the Domain (revived every 24 s).</summary>
    public const double ChatterSeconds = 30.0;

    /// <summary>Says a random line of <paramref name="category"/> for the variety's general to everyone in the area; nothing when it
    /// has no such line.</summary>
    public static async Task Say(Player player, string? varietyId, string category, uint delayMs = 0)
    {
        if (LegionData.Current.VoiceOf(varietyId) is not { } voice || !voice.Lines.TryGetValue(category, out var rows) || rows.Length == 0) return;
        byte[] message = InstanceNpcs.TextAudio(0, voice.Npc, rows[Random.Shared.Next(rows.Length)], delayMs);
        foreach (var member in LegionOutput.Members(player))
            await PlayerOf(member).Send(message, $"0x01c7 Legion general {category} (NPCs {voice.Npc})");
    }
}
