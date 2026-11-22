/// <summary>The game's own effect kinds for <see cref="EffectRules"/>: what a rule can do, each with its named
/// parameters. A new kind is one entry here (or a mod's <see cref="EffectRules.Register"/>), then any row can use it.</summary>
public static partial class Game
{
    static bool _effectKinds;

    /// <summary>Registers the kinds below, once. EffectRules calls this before it reads its rows.</summary>
    public static void EnsureEffectKinds()
    {
        if (_effectKinds) return;
        _effectKinds = true;

        // Onslaught for "seconds": starts it or lengthens it, never shortens the one running (KillOnslaught), and the
        // buff goes out from the thread pool because an event can be recorded inside the Monsters lock.
        EffectRules.Register(new("onslaught", ["seconds"], [], fire =>
        {
            var conn = fire.Conn;
            bool gained;
            lock (conn.Monsters) gained = conn.PlayerLife > 0 && KillOnslaughtOf(conn).Extend(fire.Now, fire.Number("seconds"));
            if (gained && conn.Wire is (var remote, { } stream, var ciphers))
                _ = Task.Run(() => SendKillOnslaught(remote, stream, ciphers, conn));
        }));

        // "count" charges of "type" (endurance, frenzy, power), as a skill's own grant: the charge rules apply it at
        // their next step, with the maximum and the refresh they give any gain.
        EffectRules.Register(new("charge", ["count"], ["type"], fire =>
        {
            int count = (int)Math.Round(fire.Number("count"));
            if (count > 0 && Enum.TryParse<ChargeType>(fire.Word("type"), ignoreCase: true, out var type))
                ChargesOf(fire.Conn).Enqueue(new ChargeEvent.Grant(type, count, "effect rule"));
        }, (parameter, word) => Enum.TryParse<ChargeType>(word, ignoreCase: true, out ChargeType known)));
    }
}
