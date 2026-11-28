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

    public static async Task<bool> ActivateBladefallTrarthus(Player player, ushort skillId)
    {
        if (BladefallVolleys.BuffRow(BladefallVolleys.CostBuff) is not ushort costRow
            || BladefallVolleys.BuffRow(BladefallVolleys.BladesBuff) is not ushort bladesRow) return false;
        var gem = SupportedSkill(player, skillId);
        int Own(uint k) => gem.Stats.GetValueOrDefault(k);
        (ushort Id, ushort Action)? icon = SkillsFor(player).ActionHash(gem.Effect) is ushort action ? (skillId, action) : null;
        BladefallTrarthusState state;
        if (player.BladefallTrarthus is not null || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return false;
        var stats = CharacterPlayerStats(player);
        bool paysLife = Lifetap.ConvertsCostToLife(gem.Stats, stats);
        bool freeMana = NoManaCostFor(player, stats.GetValueOrDefault(Stat.NoManaCost));
        if (paysLife ? player.PlayerLife <= 1 : !freeMana && player.PlayerMana == 0) return false;
        int rate = BladefallVolleys.ManaPerMinute(Own(Stat.BaseManaCostPctPerMinute), paysLife ? player.Pools.Life : player.Pools.Mana);
        state = new(player.NextBuff++, player.NextBuff++, skillId)
        {
            PaysLife = paysLife, CostPerMinute = rate,
            NextVolley = AreaTime(player) + BladefallVolleySeconds(player, Own),
        };
        player.BladefallTrarthus = state;
        player.LingeringBladeSources.Add(state);
        player.Area.Buffs.Set(player.Id, new(costRow, player.Id, skillId, ActiveSkill: icon, List: [0]));
        await SendBladefallCost(player, state.PaysLife, !paysLife && freeMana ? 0 : rate);
        player.Area.Buffs.Set(player.Id, new(bladesRow, player.Id, skillId, ActiveSkill: icon));
        Note($"{BladefallVolleys.BladesBuff} (BuffDefinitions row {bladesRow}): " +
            $"a volley every {BladefallVolleySeconds(player, Own) * 1000:0} ms, {state.CostPerMinute / 60.0:0.#} {(state.PaysLife ? "life" : "mana")} per second");
        _ = RunBladefallTrarthus(player, state);
        return true;
    }

    static double BladefallVolleySeconds(Player player, Func<uint, int> own) =>
        Math.Max(.05, BladefallVolleys.FrequencyMs(own(Stat.BladefallBaseVolleyFrequencyMs),
            own(Stat.BladefallVolleyFrequencyPlusPctPer100MaximumMana), player.Pools.Mana) / 1000.0);

    static Task SendBladefallCost(Player player, bool life, int perMinute)
    {
        SetStats(player, player.Id, [(life ? Stat.LifeSpentPerMinute : Stat.ManaSpentPerMinute, perMinute)]);
        return Task.CompletedTask;
    }

    // The mana Bladefall of Trarthus spends over this recovery step (StartResourceRecovery takes it off with regeneration).
    public static double BladefallManaSpent(Player player, double seconds) =>
        BladefallManaSpent(player, seconds, RecoveryFlaskWaivesManaCost(player) ? seconds : 0);

    static double BladefallManaSpent(Player player, double seconds, double waivedSeconds) =>
        player.BladefallTrarthus is { Ended: false, PaysLife: false } state
        && CharacterPlayerStats(player).GetValueOrDefault(Stat.NoManaCost) == 0
            ? state.CostPerMinute / 60.0 * Math.Max(0, seconds - waivedSeconds) : 0;

    public static async Task EndBladefallTrarthus(Player player, string why)
    {
        BladefallTrarthusState? state;
        state = player.BladefallTrarthus;
        player.BladefallTrarthus = null;
        if (state is not null) state.Ended = true;
        if (state is null) return;
        foreach (string buff in (string[])[BladefallVolleys.CostBuff, BladefallVolleys.BladesBuff])
            if (BladefallVolleys.BuffRow(buff) is ushort row) player.Area.Buffs.Clear(player.Id, row, player.Id);
        Note($"Bladefall of Trarthus ends ({why})");
        await SendBladefallCost(player, state.PaysLife, 0);
    }

    // The volleys while it runs, then the blades' last moments after it ends; the connection's end (an area change)
    // stops it, as the area's objects go with it.
    static async Task RunBladefallTrarthus(Player player,
        BladefallTrarthusState state)
    {
        const double Lands = 1.0;
        uint? bladeRow = Art.MiscAnimatedRow(BladefallVolleys.GroundBladeArt) is int r ? (uint)r : null;
        var random = AreaRng(player);
        try
        {
            while (!player.Closed && !player.LeavingArea)
            {
                await WhilePaused(player);
                double now = AreaTime(player);
                var landed = new List<uint>();
                var gone = new List<uint>();
                for (int i = state.Blades.Count - 1; i >= 0; i--)
                {
                    var blade = state.Blades[i];
                    if (blade.Gone <= now)
                    {
                        gone.Add(blade.Id);
                        state.Blades.RemoveAt(i);
                    }
                    else if (blade.Landed > 0 && blade.Landed <= now)
                    {
                        landed.Add(blade.Id);
                        state.Blades[i] = (blade.Id, blade.At, 0, blade.Gone);
                    }
                }
                foreach (uint id in landed)
                    await Send(player, [], null, atGate: () =>
                        state.Blades.Any(blade => blade.Id == id)
                            ? InstanceEntities.ComponentUpdate(id, GroundBladeObject.AnimatedIndex, GroundBladeObject.Landed()) : null);
                foreach (uint id in gone) await Send(player, InstanceProtocol.RemoveObject(id), null);
                if (state.Ended || !ReferenceEquals(player.BladefallTrarthus, state))
                {
                    if (state.Blades.Count == 0) return;
                }
                else if (now >= state.NextVolley)
                {
                    await BladefallVolley(player, state, bladeRow, random, now, Lands);
                    var own = SupportedSkill(player, state.Skill).Stats;
                    double interval = BladefallVolleySeconds(player, n => n is var k ? own.GetValueOrDefault(k) : 0);
                    state.NextVolley = Math.Max(now, state.NextVolley + interval);
                }
                await AreaDelay(player, 0.025);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} Bladefall of Trarthus stopped: {ServerLog.Describe(ex)}"); }
        finally
        {
            player.LingeringBladeSources.Remove(state);
        }
    }

    static async Task BladefallVolley(Player player,
        BladefallTrarthusState state, uint? bladeRow, Random random, double now, double lands)
    {
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == state.Skill);
        if (gem.Name is null) { await EndBladefallTrarthus(player, "the gem is gone"); return; }
        var supported = SupportedSkill(player, state.Skill);
        int Own(uint k) => supported.Stats.GetValueOrDefault(k);
        (int X, int Y) at;
        at = player.Walk.Position(now);
        bool triggered = CooldownTriggerSupport(supported) is not null;
        await AreaSkillHit(player, state.Skill, gem, at, at, immediate: true,
            triggeredCurrent: triggered ? () => ReferenceEquals(player.BladefallTrarthus, state) && !state.Ended : null);
        if (bladeRow is not uint row) return;
        var playerStats = CharacterPlayerStats(player);
        int Player(uint k) => playerStats.GetValueOrDefault(k);
        double lasts = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own);
        int radius = Math.Max(1, SkillRadius(player, supported.Stats, true, supported.Effect));
        int count = Math.Max(0, Own(Stat.BladefallCreateXLingeringBladesPerVolley));
        int most = Own(Stat.MaximumNumberOfBladesLeftInGround) is int m and > 0 ? m : int.MaxValue;
        var created = new List<(uint Id, GroundBladeObject Blade)>();
        var removed = new List<uint>();
        if (!ReferenceEquals(player.BladefallTrarthus, state)) return;
        for (int i = 0; i < count; i++)
        {
            // A cell in the area that the layout says is walkable (a few tries), else the player's own.
            (int X, int Y) cell = at;
            for (int tries = 0; tries < 8; tries++)
            {
                double angle = random.NextDouble() * Math.Tau, distance = Math.Sqrt(random.NextDouble()) * radius;
                var c = (X: at.X + (int)Math.Round(Math.Cos(angle) * distance), Y: at.Y + (int)Math.Round(Math.Sin(angle) * distance));
                if (player.Layout.Walkable?.Invoke(c.X, c.Y) != false) { cell = c; break; }
            }
            uint id = player.TakeObjectId();
            created.Add((id, new GroundBladeObject(cell.X, cell.Y, (float)(random.NextDouble() * Math.Tau), row)));
            state.Blades.Add((id, cell, now + lands, now + lasts));
            if (state.Blades.Count > most)
            {
                removed.Add(state.Blades[0].Id);
                state.Blades.RemoveAt(0);
            }
        }
        foreach (uint id in removed) await Send(player, InstanceProtocol.RemoveObject(id), null);
        foreach (var (id, blade) in created)
            await Send(player, [], null, atGate: () =>
                state.Blades.Any(entry => entry.Id == id)
                    ? InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(BladefallVolleys.GroundBlade), blade.Blob())
                    : null);
    }
}
