using GameServer.Core.Protocol.Instance;

/// <summary>
/// Life and mana flasks (Agents/docs/protocol/13-items.md §8): a use (C→S 0x70) spends ComponentCharges PerCharge and
/// recovers Flasks.dat LifePerUse / ManaPerUse evenly over RecoveryTime (with its mods: FlaskRecovery). Charges come back
/// from kills and other sources (FlaskCharges), and in full in towns and hideouts and on resurrection.
/// </summary>
static class Flasks
{
    /// <summary>What one use recovers (life, mana) over <paramref name="Seconds"/>, and its charges: spent per use, and the
    /// most it holds.</summary>
    public sealed record Flask(int LifePerUse, int ManaPerUse, double Seconds, int ChargesPerUse, int MaxCharges);

    /// <summary>The flask after a use, or null when it has fewer charges than a use needs.</summary>
    public static Item? Spend(Item flask, Flask f) => flask.Charges < f.ChargesPerUse ? null : flask with { Charges = flask.Charges - f.ChargesPerUse };

    /// <summary>Gives each flask in the flask bar (life, mana and utility: <see cref="ItemCatalog.FlaskChargesOf"/>) up to
    /// <paramref name="charges"/> charges, up to its maximum with its mods (<see cref="FlaskCharges.Of"/>), saved now or, with <paramref name="saveNow"/> false, with the next save
    /// (PlayerInventories.ChangeUnsaved): the flasks that changed.</summary>
    public static List<InventoryItem> Fill(PlayerInventories items, ItemCatalog catalog, int charges, bool saveNow = true,
        Func<InventoryItem, bool>? canGain = null)
    {
        List<InventoryItem> changed = [];
        foreach (var flask in items.Items(PlayerInventories.Flask))
            if (canGain?.Invoke(flask) != false && catalog.ByHash(flask.Item.TypeHash) is { } type && catalog.FlaskChargesOf(type) is { } baseUse
                && FlaskCharges.Of(baseUse, ItemModStats.Local(flask.Item, catalog.Mods)) is var use && flask.Item.Charges < use.MaxCharges &&
                (saveNow ? items.Change(PlayerInventories.Flask, flask.Id, i => Gain(i, use, charges)) : items.ChangeUnsaved(PlayerInventories.Flask, flask.Id, i => Gain(i, use, charges))) is { } done)
                changed.Add(done);
        return changed;
    }

    /// <summary>Recovery flasks need room in at least one pool they restore. Mana's cap excludes reservations.</summary>
    public static bool CanRecover(Flask f, uint life, uint maxLife, uint mana, uint unreservedMana) =>
        (f.LifePerUse > 0 && life < maxLife) || (f.ManaPerUse > 0 && mana < unreservedMana);

    /// <summary>The flask with <paramref name="charges"/> more, up to its maximum.</summary>
    public static Item Gain(Item flask, Flask f, int charges) =>
        flask with { Charges = (int)Math.Min(f.MaxCharges, (long)flask.Charges + charges) };

    /// <summary>A flask's buff on the player (S→C 0x1c1): its duration in flags bit1's field, which the client reads as an
    /// f32 in seconds (+0x68, reader 0x141fdf870; without it +infinity), and the flask-bar cell as the source skill (+0x60):
    /// the client draws the effect's draining timer bar under that flask (live 2026-10-25). <paramref name="values"/>: the
    /// IsRecovery rows' one u32 per GrantedStats entry.</summary>
    public static byte[] Buff(uint player, ushort buff, ushort row, byte slot, double? seconds, IReadOnlyList<uint>? values = null) =>
        InstanceEntities.AddBuff(player, buff, row, player, slot, values,
            u68: seconds is double s ? BitConverter.SingleToUInt32Bits((float)s) : null);

    /// <summary>A hybrid's Life and Mana recover and end independently (wiki Hybrid flask; PoB CalcPerform).
    /// Give each resource its own existing recovery queue, native buff and remaining duration. Its companion continues
    /// after one resource fills or expires. Legacy/manual Use records retain their shared Seconds fallback.</summary>
    public static List<Effect> RecoveryEffects(FlaskRecovery.Use use, byte slot, double now, Func<ushort> nextBuff,
        ushort lifeBuffRow, ushort manaBuffRow)
    {
        var effects = new List<Effect>();
        bool hybrid = use.Life > 0 && use.Mana + use.ManaAtEnd > 0;
        double lifeSeconds = use.LifeSeconds ?? use.Seconds, manaSeconds = use.ManaSeconds ?? use.Seconds;
        if (use.Life > 0 && lifeSeconds > 0)
            effects.Add(new(slot, nextBuff(), lifeBuffRow, use.Life / lifeSeconds, 0, now, now + lifeSeconds,
                LifeStays: use.LifeStaysAtFull, NoQueue: hybrid ? use.LifeStaysAtFull : use.NoQueue));
        if (use.Mana + use.ManaAtEnd > 0 && manaSeconds > 0)
            effects.Add(new(slot, nextBuff(), manaBuffRow, 0, use.Mana / manaSeconds, now, now + manaSeconds,
                use.ManaAtEnd, ManaStays: use.ManaStaysAtFull, NoQueue: hybrid ? use.ManaStaysAtFull : use.NoQueue));
        return effects;
    }

    /// <summary>A flask's recovery under way: the flask-bar cell it came from, its buff (S→C 0x1c1; 0: none, the extra life
    /// of a life flask's suffix) and how much life and mana it gives per second for <paramref name="Ends"/> −
    /// <paramref name="Started"/> seconds of recovery from <paramref name="Started"/> (connection clock seconds; a queued
    /// effect waits, so it may end later); mana that comes all at once when it runs out (<paramref name="ManaAtEnd"/>),
    /// whether it stays on a full pool, and whether it does not queue (FlaskRecovery.Use).</summary>
    public sealed record Effect(byte Slot, ushort Buff, ushort BuffRow, double LifePerSecond, double ManaPerSecond, double Started, double Ends,
        double ManaAtEnd = 0, bool LifeStays = false, bool ManaStays = false, bool NoQueue = false)
    {
        /// <summary>Whether a full pool ends it: a life effect when life is full, a mana one when mana is (a hybrid one when
        /// both are), unless it stays at full.</summary>
        public bool EndsWhenFull(bool lifeFull, bool manaFull) =>
            (LifePerSecond == 0 || lifeFull && !LifeStays) && (ManaPerSecond == 0 && ManaAtEnd == 0 || manaFull && !ManaStays);

        /// <summary>The queue it waits in (poewiki: "Recovery flasks queue up if multiple flasks are active. The effect with
        /// the highest regeneration rate is always applied first"): life (a life or hybrid flask), mana, or none (the extra
        /// life of a suffix, "Effect does not Queue").</summary>
        public int Queue => NoQueue || Buff == 0 ? 0 : LifePerSecond > 0 ? 1 : 2;

        /// <summary>Its regeneration rate in its queue.</summary>
        public double Rate => Queue == 1 ? LifePerSecond : ManaPerSecond + (Ends > Started ? ManaAtEnd / (Ends - Started) : 0);
    }

    /// <summary>The flasks recovering on one connection. Life flask effects queue, the one with the highest rate recovering
    /// while the others wait with the rest of their time; mana flask effects likewise; a use of a flask whose effect still
    /// runs queues another.</summary>
    public sealed class Effects
    {
        sealed class Running(Effect effect)
        {
            public Effect Effect { get; } = effect;
            public double Remaining = Math.Max(0, effect.Ends - effect.Started);
        }

        readonly List<Running> _running = [];
        readonly List<Running> _lastEnded = [];
        readonly Lock _lock = new();
        const double Done = 1e-9;

        /// <summary>Whether any flask is recovering.</summary>
        public bool Any
        {
            get { lock (_lock) return _running.Count > 0; }
        }

        /// <summary>Starts an effect: it recovers at once, or waits in its queue. Nothing is replaced (null).</summary>
        public Effect? Start(Effect effect)
        {
            lock (_lock) _running.Add(new Running(effect));
            return null;
        }

        /// <summary>The seconds of recovery an effect has left (0 when it is not running).</summary>
        public double Remaining(Effect effect)
        {
            lock (_lock) return _running.FirstOrDefault(r => ReferenceEquals(r.Effect, effect))?.Remaining ?? 0;
        }

        /// <summary>The life and mana recovered between <paramref name="from"/> and <paramref name="to"/>, and the effects
        /// that ran out by <paramref name="to"/> (removed; one with mana at the end gives it then).</summary>
        public (double Life, double Mana, List<Effect> Ended) Advance(double from, double to)
        {
            double life = 0, mana = 0;
            List<Effect> ended = [];
            void Run(Running r, double seconds)
            {
                life += r.Effect.LifePerSecond * seconds;
                mana += r.Effect.ManaPerSecond * seconds;
                r.Remaining -= seconds;
                if (r.Remaining > Done) return;
                mana += r.Effect.ManaAtEnd;
                _running.Remove(r);
                ended.Add(r.Effect);
            }
            lock (_lock)
            {
                foreach (var r in _running.Where(r => r.Effect.Queue == 0).ToList())
                    Run(r, Math.Min(r.Remaining, Math.Max(0, to - Math.Max(from, r.Effect.Started))));
                foreach (int queue in new[] { 1, 2 })
                    for (double t = from; t < to - Done;)
                    {
                        var waiting = _running.Where(r => r.Effect.Queue == queue).ToList();
                        var now = waiting.Where(r => r.Effect.Started <= t + Done).ToList();
                        double nextStart = waiting.Where(r => r.Effect.Started > t + Done).Select(r => r.Effect.Started).DefaultIfEmpty(to).Min();
                        if (now.Count == 0)
                        {
                            if (nextStart >= to) break;
                            t = nextStart;
                            continue;
                        }
                        // The highest rate first; of equal ones the earlier.
                        var first = now.OrderByDescending(r => r.Effect.Rate).ThenBy(r => r.Effect.Started).First();
                        double seconds = Math.Min(first.Remaining, Math.Min(nextStart, to) - t);
                        Run(first, seconds);
                        t += seconds;
                    }
            }
            return (life, mana, ended);
        }

        /// <summary>Of the effects a full pool ended (<paramref name="ended"/>, from <see cref="End"/>), the ones that stay at
        /// a full pool or still owe their mana at the end (<see cref="Effect.EndsWhenFull"/>) and have time left go back:
        /// the rest, which end. (<paramref name="now"/>: unused, the time left decides.)</summary>
        public List<Effect> KeepRunning(List<Effect> ended, bool lifeFull, bool manaFull, double now)
        {
            lock (_lock)
            {
                var back = _lastEnded.Where(r => r.Remaining > Done && !r.Effect.EndsWhenFull(lifeFull, manaFull)
                    && ended.Any(e => ReferenceEquals(e, r.Effect)) && !_running.Contains(r)).ToList();
                _running.AddRange(back);
                return [.. ended.Where(e => !back.Any(r => ReferenceEquals(r.Effect, e)))];
            }
        }

        /// <summary>Ends the effects <paramref name="which"/> picks (a full pool, death): them, removed.</summary>
        public List<Effect> End(Func<Effect, bool> which)
        {
            lock (_lock)
            {
                var ended = _running.Where(r => which(r.Effect)).ToList();
                foreach (var r in ended) _running.Remove(r);
                _lastEnded.Clear();
                _lastEnded.AddRange(ended);
                return [.. ended.Select(r => r.Effect)];
            }
        }
    }
}
