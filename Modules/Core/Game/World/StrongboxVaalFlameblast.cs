using static Program;

public static partial class MapStrongboxes
{
    const string VaalFlameblastEffect = "ChestVaalFlameblast";
    public sealed record VaalFlameblast(long Started, string Daemon) : VesselCast(Started, Daemon);

    sealed record FlameblastSkill(VesselSkill Spell, long BurstTicks, VesselImpact[] Bursts);

    static FlameblastSkill ReadFlameblast(ClientState conn)
    {
        var spell = ReadVesselSpell(conn, VaalFlameblastEffect);
        // ActiveSkills: one explosion every three stages through stage 15. PoB's 45-cell base radius loses the native
        // flat radius per stage after the first. The hit and ailment damage multipliers count the current stage.
        var bursts = new VesselImpact[5];
        for (int i = 0; i < bursts.Length; i++)
        {
            int stage = (i + 1) * 3;
            int radius = SkillArea.Radius(45 + (stage - 1) * spell.Required("vaal_flameblast_radius_+_per_stage"), spell.Get, spell: true);
            bursts[i] = spell.Impact(radius, stage * spell.Required("charged_blast_spell_damage_+%_final_per_stack"),
                stage * spell.Required("flameblast_ailment_damage_+%_final_per_stack"));
        }
        long burstTicks = TimeSpan.FromSeconds(3 * spell.Seconds).Ticks;
        return new(spell, burstTicks, bursts);
    }

    static async Task TickFlameblasts(ClientState conn, Area area, Box box, TimeSpan now, bool currentView)
    {
        var skill = area.FlameblastSkill ??= ReadFlameblast(conn);
        foreach (var blast in box.Flameblasts.ToArray())
        {
            if (!await PublishVesselHits(conn, area, blast)) return;
            // Count the first stage after one cast interval. The target stays at the vessel across runner handoff.
            // An awake observer joining during the cast gets its native action once. Retire it on the original
            // timeline; the protocol's action start supplies no elapsed-stage offset.
            if (currentView && blast.Landed < skill.Bursts.Length && now.Ticks < blast.Started + skill.Bursts.Length * skill.BurstTicks)
                await ShowVesselCast(conn, area, box, blast, skill.Spell);
            while (blast.Landed < skill.Bursts.Length)
            {
                long due = blast.Started + (blast.Landed + 1) * skill.BurstTicks;
                if (due > now.Ticks) break;
                if (!await LandVesselImpact(conn, area, box, blast, skill.Spell, skill.Bursts[blast.Landed], (box.X, box.Y), due)) return;
            }
            if (blast.Landed == skill.Bursts.Length)
            {
                if (blast.Actor != 0 && !await RemoveVesselActor(conn, area, box, blast)) return;
                box.Flameblasts.Remove(blast);
                Keep(conn, area);
            }
        }
    }
}
