namespace GameServer.Gateway.Tests;

public class SkillBarLayoutTests
{
    const string Attack = "PlayerMelee";
    static Func<string, bool> Having(params string[] skills) => e => e == Attack || e.StartsWith("Action/") || skills.Contains(e);

    [Fact]
    public void A_new_bar_gets_the_default_attack_on_the_left_mouse_button_and_the_gem_skill_on_the_right()
    {
        var added = SkillBarLayout.Fill(new Dictionary<int, string>(), Attack, ["HolyStrike"], Having("HolyStrike"));

        Assert.Equal([(0, Attack), (2, "HolyStrike")], added);
    }

}
