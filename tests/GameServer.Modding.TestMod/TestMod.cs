using GameServer.Modding;

namespace GameServer.Modding.TestMod;

/// <summary>
/// Every hook, named after the mod's id so one assembly can be several mods: /count_&lt;id&gt; counts its own uses (a
/// static, so a reload must start it over), /boom_&lt;id&gt; throws, /ticks_&lt;id&gt; and /kills_&lt;id&gt; tell what it saw.
/// Opcode 0x7777 is taken. Drops are doubled with a Mirror of Kalandra besides, experience tripled, +10 strength.
/// A file "fail-load" in the mod's folder makes Load throw.
/// </summary>
public sealed class TestMod : IMod, IDisposable
{
    static int _count;
    int _ticks, _kills;
    IModContext? _mod;

    public void Load(IModContext mod)
    {
        _mod = mod;
        string id = mod.Manifest.Id;
        mod.OnChatCommand($"count_{id}", $"/count_{id} (counts)", c => c.Player.Tell($"{id} count {Interlocked.Increment(ref _count)}"));
        mod.OnChatCommand($"boom_{id}", $"/boom_{id} (throws)", _ => throw new InvalidOperationException("boom"));
        mod.OnChatCommand($"ticks_{id}", $"/ticks_{id}", c => c.Player.Tell($"{id} ticks {_ticks}"));
        mod.OnChatCommand($"kills_{id}", $"/kills_{id}", c => c.Player.Tell($"{id} kills {_kills}"));
        mod.OnTick((_, _) => { Interlocked.Increment(ref _ticks); return Task.CompletedTask; });
        mod.OnPacket(0x7777, _ => Task.FromResult(true));
        mod.OnPacket(0x7778, _ => Task.FromResult(false));
        mod.OnMonsterKilled(_ => { Interlocked.Increment(ref _kills); return Task.CompletedTask; });
        mod.OnAreaGenerated(p => p.Tell($"{id} area {p.AreaId}"));
        mod.ModifyDrops(d => { d.Quantity *= 2; d.Items.Add("Mirror of Kalandra"); });
        mod.ModifyExperience(e => e.Experience *= 3);
        mod.ModifyStats(s => s.Add("strength", 10));
        // After its hooks: what a failed load registered must not stay.
        if (File.Exists(Path.Combine(mod.Folder, "fail-load"))) throw new InvalidOperationException("fail-load is there");
    }

    public void Dispose() => _mod?.Log("disposed");
}
