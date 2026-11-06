// Native current_momentum getter 0x1428da370 and increased attack-speed term 0x1426ff98d.
// The non-channelled extra count belongs to the next-use skill view; stored stacks remain separate.
static class MomentumSupport
{
    public static int Current(Func<string, int> get, bool channelled) =>
        get("base_current_momentum") + (channelled ? 0 : get("count_as_momentum_+_if_not_channelled"));

    public static int AttackSpeed(Func<string, int> get, bool channelled) =>
        Current(get, channelled) * get("support_momentum_attack_speed_+%_per_stack");
}
