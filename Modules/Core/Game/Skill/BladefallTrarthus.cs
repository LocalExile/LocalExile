using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Bladefall of Trarthus (BladefallVolleys): using it turns it on, as the original server does (2026-11-07). The use is
    // answered with 0x1ce, the skill_cost_over_time buff (its one-entry list 0) with the player's mana_spent_per_minute
    // (0x1b3), and the righteous_blades buff, whose BuffVisuals rain the blades around the player; both carry the skill
    // bar's icon. Then every bladefall_volley_frequency_ms a volley hits each monster in the skill's area around the player
    // once (AreaSkillHit) and leaves bladefall_create_X_lingering_blades_per_volley ground blades in it, which stand for the
    // skill's duration, at most maximum_number_of_blades_left_in_ground of them. The mana runs down at the spent rate on top
    // of regeneration; when it is out (or the player dies) the buffs come off and the rate goes back to 0. Using it again
    // while it runs does nothing (no 0x1ce): the original server answered a second press with nothing.
    public static bool IsBladefallTrarthus(int effect) => Skills.EffectId(effect) == BladefallVolleys.EffectId;

    public static async Task<bool> ActivateBladefallTrarthus(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId)
    {
        if (BladefallVolleys.BuffRow(BladefallVolleys.CostBuff) is not ushort costRow
            || BladefallVolleys.BuffRow(BladefallVolleys.BladesBuff) is not ushort bladesRow) return false;
        var gem = SupportedSkill(conn, skillId);
        int Own(string name) => Stats.Key(name) is uint k ? gem.Stats.GetValueOrDefault(k) : 0;
        (ushort Id, ushort Action)? icon = SkillsFor(conn).ActionHash(gem.Effect) is ushort action ? (skillId, action) : null;
        BladefallTrarthusState state;
        lock (conn.Monsters)
        {
            if (conn.BladefallTrarthus is not null || conn.PlayerLife == 0 || conn.PlayerMana == 0 || conn.Closed || conn.LeavingArea) return false;
            state = new(conn.NextBuff++, conn.NextBuff++, skillId)
            {
                ManaPerMinute = BladefallVolleys.ManaPerMinute(Own("base_mana_cost_%_per_minute"), conn.Pools.Mana),
                NextVolley = conn.Clock.Elapsed.TotalSeconds + BladefallVolleySeconds(conn, Own),
            };
            conn.BladefallTrarthus = state;
        }
        await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, state.CostBuff, costRow, PlayerId, skillId,
                activeSkill: icon, list: [0]),
            $"0x01c1 player buff {state.CostBuff}: {BladefallVolleys.CostBuff} (BuffDefinitions row {costRow}) from skill 0x{skillId:x4}");
        await SendBladefallManaSpent(remote, stream, ciphers, state.ManaPerMinute);
        await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, state.BladesBuff, bladesRow, PlayerId, skillId, activeSkill: icon),
            $"0x01c1 player buff {state.BladesBuff}: {BladefallVolleys.BladesBuff} (BuffDefinitions row {bladesRow}): " +
            $"a volley every {BladefallVolleySeconds(conn, Own) * 1000:0} ms, {state.ManaPerMinute / 60.0:0.#} mana per second");
        _ = RunBladefallTrarthus(remote, stream, ciphers, conn, state);
        return true;
    }

    static double BladefallVolleySeconds(ClientState conn, Func<string, int> own) =>
        Math.Max(.05, BladefallVolleys.FrequencyMs(own("bladefall_base_volley_frequency_ms"),
            own("bladefall_volley_frequency_+%_per_100_maximum_mana"), conn.Pools.Mana) / 1000.0);

    static Task SendBladefallManaSpent(string remote, NetworkStream stream, ConnectionCiphers ciphers, int perMinute) =>
        Stats.Key("mana_spent_per_minute") is uint key
            ? Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(key, perMinute)]),
                $"0x01b3 player mana_spent_per_minute {perMinute}")
            : Task.CompletedTask;

    // The mana Bladefall of Trarthus spends over this recovery step (RunResourceRecovery takes it off with regeneration).
    public static double BladefallManaSpent(ClientState conn, double seconds) =>
        conn.BladefallTrarthus is { Ended: false } state ? state.ManaPerMinute / 60.0 * seconds : 0;

    public static async Task EndBladefallTrarthus(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
    {
        BladefallTrarthusState? state;
        lock (conn.Monsters)
        {
            state = conn.BladefallTrarthus;
            conn.BladefallTrarthus = null;
            if (state is not null) state.Ended = true;
        }
        if (state is null) return;
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, state.CostBuff),
            $"0x01c2 player buff {state.CostBuff}: Bladefall of Trarthus ends ({why})");
        await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, state.BladesBuff), null);
        await SendBladefallManaSpent(remote, stream, ciphers, 0);
    }

    // The volleys while it runs, then the blades' last moments after it ends; the connection's end (an area change)
    // stops it, as the area's objects go with it.
    static async Task RunBladefallTrarthus(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        BladefallTrarthusState state)
    {
        const double Lands = 1.0;
        uint? bladeRow = Art.MiscAnimatedRow(BladefallVolleys.GroundBladeArt) is int r ? (uint)r : null;
        var random = new Random();
        try
        {
            while (!conn.Closed && !conn.LeavingArea)
            {
                await WhilePaused(conn);
                double now = conn.Clock.Elapsed.TotalSeconds;
                var landed = new List<uint>();
                var gone = new List<uint>();
                lock (conn.Monsters)
                {
                    foreach (var blade in state.Blades)
                        if (blade.Landed <= now && blade.Landed > 0) landed.Add(blade.Id);
                    if (landed.Count > 0)
                    {
                        var kept = state.Blades.Select(b => landed.Contains(b.Id) ? (b.Id, 0d, b.Gone) : b).ToList();
                        state.Blades.Clear();
                        foreach (var b in kept) state.Blades.Enqueue(b);
                    }
                    while (state.Blades.TryPeek(out var oldest) && oldest.Gone <= now) gone.Add(state.Blades.Dequeue().Id);
                }
                foreach (uint id in landed)
                    await Send(remote, stream, ciphers, InstanceEntities.ComponentUpdate(id, GroundBladeObject.AnimatedIndex, GroundBladeObject.Landed()), null);
                foreach (uint id in gone) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
                if (state.Ended || !ReferenceEquals(conn.BladefallTrarthus, state))
                {
                    if (state.Blades.Count == 0) return;
                }
                else if (now >= state.NextVolley)
                {
                    await BladefallVolley(remote, stream, ciphers, conn, state, bladeRow, random, now, Lands);
                    var own = SupportedSkill(conn, state.Skill).Stats;
                    double interval = BladefallVolleySeconds(conn, n => Stats.Key(n) is uint k ? own.GetValueOrDefault(k) : 0);
                    state.NextVolley = Math.Max(now, state.NextVolley + interval);
                }
                await Task.Delay(25);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Bladefall of Trarthus stopped: {ServerLog.Describe(ex)}"); }
    }

    static async Task BladefallVolley(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        BladefallTrarthusState state, uint? bladeRow, Random random, double now, double lands)
    {
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == state.Skill);
        if (gem.Name is null) { await EndBladefallTrarthus(remote, stream, ciphers, conn, "the gem is gone"); return; }
        var supported = SupportedSkill(conn, state.Skill);
        int Own(string name) => Stats.Key(name) is uint k ? supported.Stats.GetValueOrDefault(k) : 0;
        (int X, int Y) at;
        lock (conn.Monsters) at = conn.Walk.Position(now);
        await AreaSkillHit(remote, stream, ciphers, conn, state.Skill, gem, at, at, immediate: true);
        if (bladeRow is not uint row || Stats.Key("base_skill_effect_duration") is null) return;
        var player = CharacterPlayerStats(conn);
        int Player(string name) => Stats.Key(name) is uint k ? player.GetValueOrDefault(k) : 0;
        double lasts = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own);
        int radius = Math.Max(1, SkillRadius(conn, supported.Stats, true, supported.Effect));
        int count = Math.Max(0, Own("bladefall_create_X_lingering_blades_per_volley"));
        int most = Own("maximum_number_of_blades_left_in_ground") is int m and > 0 ? m : int.MaxValue;
        var created = new List<(uint Id, GroundBladeObject Blade)>();
        var removed = new List<uint>();
        lock (conn.Monsters)
        {
            if (!ReferenceEquals(conn.BladefallTrarthus, state)) return;
            for (int i = 0; i < count; i++)
            {
                // A cell in the area that the layout says is walkable (a few tries), else the player's own.
                (int X, int Y) cell = at;
                for (int tries = 0; tries < 8; tries++)
                {
                    double angle = random.NextDouble() * Math.Tau, distance = Math.Sqrt(random.NextDouble()) * radius;
                    var c = (X: at.X + (int)Math.Round(Math.Cos(angle) * distance), Y: at.Y + (int)Math.Round(Math.Sin(angle) * distance));
                    if (conn.Layout.Walkable?.Invoke(c.X, c.Y) != false) { cell = c; break; }
                }
                uint id = conn.TakeObjectId();
                created.Add((id, new GroundBladeObject(cell.X, cell.Y, (float)(random.NextDouble() * Math.Tau), row)));
                state.Blades.Enqueue((id, now + lands, now + lasts));
                while (state.Blades.Count > most) removed.Add(state.Blades.Dequeue().Id);
            }
        }
        foreach (uint id in removed) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
        foreach (var (id, blade) in created)
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(BladefallVolleys.GroundBlade), blade.Blob()),
                null);
    }
}
