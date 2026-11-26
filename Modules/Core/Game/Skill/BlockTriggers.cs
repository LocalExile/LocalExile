public static partial class Game
{
    // Only equipment grants can trigger their own socketed spells. Keep the negative result for ordinary gear.
    sealed record BlockTrigger(WornGrantedSkills.Grant Grant, PlayerSkillSource[] Sockets)
    {
        public int Next;
    }
    sealed record BlockEquipment(IReadOnlyList<WornGrantedSkills.Grant> Grants, ItemEffectRules Rules, BlockTrigger[] Triggers);
    static readonly RuleTable<Player, BlockEquipment> BlockEquipmentByPlayer = new();

    static BlockTrigger[] EquippedBlockTriggers(Player player, ItemEffectRules rules)
    {
        var grants = AllWornGrantedSkillsOf(player);
        if (BlockEquipmentByPlayer.TryGetValue(player, out var cached) && ReferenceEquals(grants, cached.Grants)
            && rules == cached.Rules) return cached.Triggers;
        var skills = SkillsFor(player);
        var triggers = new List<BlockTrigger>();
        foreach (var grant in grants)
        {
            if (!grant.Support || !rules.TriggersOnBlock(skills.EffectStats(grant.Effect, grant.Level))) continue;
            var sockets = PlayerSkillSourcesNow(player)
                .Where(s => s.SourceGem is not null && s.Identity.Host == grant.Host).OrderBy(s => s.Identity.Socket).ToArray();
            if (sockets.Length != 0) triggers.Add(new(grant, sockets));
        }
        var equipment = new BlockEquipment(grants, rules, [.. triggers]);
        BlockEquipmentByPlayer.AddOrUpdate(player, equipment);
        return equipment.Triggers;
    }

    static void CastSocketedSpellOnBlock(Player player, double now, SpawnedMonster? attacker)
    {
        var rules = ItemEffectRules.Current;
        if (!rules.HasBlockRules || !CurrentGameWork || player.Closed || player.LeavingArea || player.Paused
            || player.PlayerLife == 0 || player.PendingTransfer is not null) return;
        foreach (var trigger in EquippedBlockTriggers(player, rules))
        {
            if (InTown(player)) return;
            var state = LinkedTriggersOf(player);
            for (int i = 0; i < trigger.Sockets.Length; i++)
            {
                int index = (trigger.Next + i) % trigger.Sockets.Length;
                var source = trigger.Sockets[index];
                if (now < state.ReadyAt.GetValueOrDefault(source.Effect)) continue;
                var gem = SupportedSkill(player, source.Id);
                if (!gem.RequirementsMet || gem.Host != trigger.Grant.Host || gem.SourceGem is null
                    || gem.Supports.FirstOrDefault(s => s.Gem.Effect == trigger.Grant.Effect) is not { } support
                    || !TriggeredSpellExecutable(player, source.Id, gem)
                    || GemSkillInstanceDisabled(player, source.Effect, source.Id, triggered: true) is not null) continue;
                var session = ProjectilesFor(player);
                bool Current() => session.Active && CurrentGameWork && player.ProjectileActive(state.Epoch)
                    && ReferenceEquals(state.Rules, CombatLifetime) && ReferenceEquals(rules, ItemEffectRules.Current)
                    && !player.Paused && !player.LeavingArea && player.PendingTransfer is null && player.PlayerLife > 0
                    && SameTriggerSource(gem, SupportedSkill(player, source.Id));
                var here = player.Walk.Position(now);
                var aim = attacker?.Walk.Position(now) ?? (here.X + 20, here.Y);
                if (!TryReleaseAutomaticPayload(player, session, source.Id, gem, support, aim, Current, now,
                    "socketed spell on block", out _, triggerTarget: attacker?.ObjectId ?? 0)) continue;
                trigger.Next = (index + 1) % trigger.Sockets.Length;
                break;
            }
        }
    }
}
