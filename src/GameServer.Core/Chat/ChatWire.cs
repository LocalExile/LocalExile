using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Chat;

/// <summary>One message between a chat member and a chat server (Agents/docs/protocol/10-chat-and-commands.md §6). What
/// <see cref="Name"/> means depends on the type: the speaker in a line, the other player in a whisper.</summary>
public sealed record ChatFrame(string Type, string? Name = null, ChatChannelKind? Kind = null, ushort? Channel = null, string? Text = null, byte? Language = null,
    IReadOnlyList<ChatFrameItem>? Items = null)
{
    public const string Hello = "hello", Join = "join", Say = "say", Whisper = "whisper";
    public const string Welcome = "welcome", Line = "line", Whispered = "whispered", Offline = "offline", TooFast = "too-fast";
}

/// <summary>An item linked in a line: the '_' of the text it stands on, and the item as the Gateway's ChatItems wrote it. A
/// chat server only passes it on; the Gateway that shows it checks it.</summary>
public sealed record ChatFrameItem(int At, string Item);

/// <summary>The chat servers' wire: each frame is a big-endian u16 length and that many bytes of JSON. Everything read from
/// it is untrusted: a public server is open to anyone, and a member cannot vouch for its server.</summary>
public static class ChatWire
{
    public const int MaxFrame = 8192, MaxText = 1024, MaxName = 32, MaxItems = 5;

    /// <summary>The character the client writes in a line for each linked item.</summary>
    public const char ItemMark = '_';

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>A character's name as the accounts allow it, so a name cannot pose as a channel prefix.</summary>
    public static bool ValidName(string? name) =>
        name is { Length: > 0 and <= MaxName } && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    /// <summary>A name on a chat server: a character's, or one with the number the hub gave it ("Name_1"). Not "Name[1]": the
    /// client does not colour a line from such a sender.</summary>
    public static bool ValidMember(string? name)
    {
        if (ValidName(name)) return true;
        int mark = name?.LastIndexOf('_') ?? -1;
        return mark > 0 && mark < name!.Length - 1 && name[(mark + 1)..].All(char.IsAsciiDigit) && ValidName(name[..mark]);
    }

    /// <summary>The text of a line without control characters, at most <see cref="MaxText"/> long.</summary>
    public static string Clean(string? text)
    {
        string clean = string.Concat((text ?? "").Where(c => !char.IsControl(c)));
        return clean.Length > MaxText ? clean[..MaxText] : clean;
    }

    /// <summary>Cleans a frame's text and keeps each item on its original mark after control characters are removed.</summary>
    public static ChatFrame CleanLine(ChatFrame frame)
    {
        string original = frame.Text ?? "", text = Clean(original);
        var items = new List<ChatFrameItem>();
        int at = 0, removed = 0;
        foreach (var item in Items(original, frame.Items) ?? [])
        {
            for (; at < item.At; at++)
                if (char.IsControl(original[at])) removed++;
            int position = item.At - removed;
            if (position < text.Length) items.Add(item with { At = position });
        }
        return frame with { Text = text, Items = items.Count == 0 ? null : items };
    }

    /// <summary>The items that stand on a mark of the text, in its order as the client wants them; null for none.</summary>
    public static IReadOnlyList<ChatFrameItem>? Items(string text, IReadOnlyList<ChatFrameItem>? items)
    {
        if (items is null) return null;
        ChatFrameItem[] placed = [.. items.Where(i => i is { Item.Length: > 0 } && i.At >= 0 && i.At < text.Length && text[i.At] == ItemMark)
            .DistinctBy(i => i.At).OrderBy(i => i.At).Take(MaxItems)];
        return placed.Length == 0 ? null : placed;
    }

    /// <summary>The frame without its items when they would make it too long: the line still goes, its marks as they are.</summary>
    public static ChatFrame Fitted(ChatFrame frame) =>
        frame.Items is null || JsonSerializer.SerializeToUtf8Bytes(frame, Options).Length <= MaxFrame ? frame : frame with { Items = null };

    public static async Task WriteAsync(Stream stream, ChatFrame frame, CancellationToken cancel)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(frame, Options);
        if (body.Length > MaxFrame) throw new InvalidDataException($"a chat frame of {body.Length} bytes");
        byte[] message = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(message, (ushort)body.Length);
        body.CopyTo(message, 2);
        await stream.WriteAsync(message, cancel);
    }

    /// <summary>The next frame, or null when the other side closed the stream between frames.</summary>
    /// <exception cref="InvalidDataException">The frame is too long or is not one.</exception>
    public static async Task<ChatFrame?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        byte[] head = new byte[2];
        int read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancel);
        if (read == 0) return null;
        if (read < head.Length) throw new EndOfStreamException();
        int length = BinaryPrimitives.ReadUInt16BigEndian(head);
        if (length is 0 or > MaxFrame) throw new InvalidDataException($"a chat frame of {length} bytes");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancel);
        try { return JsonSerializer.Deserialize<ChatFrame>(body, Options) is { Type: not null } frame ? frame : throw new InvalidDataException("a chat frame without a type"); }
        catch (JsonException e) { throw new InvalidDataException("a chat frame that is not JSON", e); }
    }

    /// <summary>Writes the queue's frames until it completes: a slow peer fills its queue instead of holding up the sender.</summary>
    internal static async Task PumpAsync(Stream stream, ChannelReader<ChatFrame> frames, CancellationToken cancel)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(cancel)) await WriteAsync(stream, frame, cancel);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
    }

    internal static Channel<ChatFrame> Queue() => Channel.CreateBounded<ChatFrame>(new BoundedChannelOptions(256) { SingleReader = true });
}
