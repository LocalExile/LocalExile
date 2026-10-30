namespace GameServer.Gateway.Tests;

public class DefaultWandContactTests : IDisposable
{
    private const string Ao = "Metadata/Characters/Int/Int.ao";
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"wand-contacts-{Guid.NewGuid():N}");
    private static WeaponProfile Wand(bool shield = false, bool dual = false) =>
        new(shield, dual, false, false, false, Wand: true);

    public DefaultWandContactTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "animation-contacts.json"), """
            { "Metadata/Characters/Int/Int.ao": {
              "amd": "witch.amd", "act": "witch.act", "animations": { "attack1_wand": [600,300] },
              "defaultWand": {
                "Wand": [
                  {"animation":"attack1_wand","index":31,"length":600,"contact":300,"weight":1,"position":[5.1,-82.0,-94.0]},
                  {"animation":"attack2_wand","index":36,"length":600,"contact":233,"weight":1},
                  {"animation":"attack3_wand","index":39,"length":600,"contact":200,"weight":1},
                  {"animation":"attack4_wand","index":42,"length":600,"contact":366,"weight":1}
                ],
                "WandShield": [
                  {"animation":"attack1_wand_shield","index":32,"length":600,"contact":300,"weight":1},
                  {"animation":"attack2_wand_shield","index":37,"length":600,"contact":233,"weight":1},
                  {"animation":"attack3_wand_shield","index":40,"length":600,"contact":200,"weight":1}
                ],
                "WandWand": [
                  {"animation":"attack1_wand_wand","index":33,"length":600,"contact":300,"weight":1}
                ]
              }
            } }
            """);
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void The_selected_clip_carries_its_release_point_when_one_is_recorded()
    {
        var contacts = new AnimationContacts(folder);
        Assert.True(contacts.TryDefaultWandContact(Ao, Wand(), 65535, out var first));
        Assert.Equal("attack1_wand", first.Animation);
        Assert.Equal(new ContactPoint(5.1f, -82.0f, -94.0f), first.Point);
        Assert.Equal(12, first.Point!.Value.HeightSteps);
        Assert.True(contacts.TryDefaultWandContact(Ao, Wand(), 4097, out var fourth));
        Assert.Equal("attack4_wand", fourth.Animation);
        Assert.Null(fourth.Point);
    }

    [Theory]
    [InlineData(4097, "attack4_wand", 42u, 366)]
    [InlineData(4098, "attack2_wand", 36u, 233)]
    [InlineData(4099, "attack2_wand", 36u, 233)]
    [InlineData(4100, "attack4_wand", 42u, 366)]
    [InlineData(65535, "attack1_wand", 31u, 300)]
    public void Fresh_action_sequence_selects_the_native_clip_and_its_contact(int sequence, string name,
        uint index, int contact)
    {
        Assert.True(new AnimationContacts(folder).TryDefaultWandContact(Ao, Wand(), (ushort)sequence, out var selected));
        Assert.Equal(name, selected.Animation);
        Assert.Equal(index, selected.AnimationIndex);
        Assert.Equal(600, selected.LengthMs);
        Assert.Equal(contact, selected.ContactMs);
        Assert.Equal("Wand", selected.Stance);
        Assert.Equal(contact / 600.0, selected.ContactFraction, 8);
    }

    [Fact]
    public void Equipped_stance_selects_its_own_ACT_pool()
    {
        var contacts = new AnimationContacts(folder);
        Assert.True(contacts.TryDefaultWandContact(Ao, Wand(shield: true), 4097, out var shield));
        Assert.Equal("WandShield", shield.Stance);
        Assert.Equal("attack1_wand_shield", shield.Animation);
        Assert.True(contacts.TryDefaultWandContact(Ao, Wand(dual: true), 4097, out var dual));
        Assert.Equal("WandWand", dual.Stance);
        Assert.Equal("attack1_wand_wand", dual.Animation);
    }

    [Fact]
    public void Missing_legacy_metadata_or_nonwand_does_not_claim_exact_selection()
    {
        var contacts = new AnimationContacts(folder);
        Assert.False(contacts.TryDefaultWandContact("absent.ao", Wand(), 1, out _));
        Assert.False(contacts.TryDefaultWandContact(Ao, new(false, false, false, true, false), 1, out _));
        File.WriteAllText(Path.Combine(folder, "animation-contacts.json"), """
            { "Metadata/Characters/Int/Int.ao": { "animations": {"attack1_wand":[600,300]} } }
            """);
        contacts = new AnimationContacts(folder);
        Assert.False(contacts.TryDefaultWandContact(Ao, Wand(), 1, out _));
        Assert.Equal(0.5, contacts.AttackContact(Ao, Wand()));
        File.Delete(Path.Combine(folder, "animation-contacts.json"));
        Assert.False(new AnimationContacts(folder).TryDefaultWandContact(Ao, Wand(), 1, out _));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"animation\":\"x\",\"index\":0,\"length\":0,\"contact\":0,\"weight\":1}]")]
    [InlineData("[{\"animation\":\"x\",\"index\":0,\"length\":600,\"contact\":601,\"weight\":1}]")]
    [InlineData("[{\"animation\":\"x\",\"index\":0,\"length\":600,\"contact\":200,\"weight\":2}]")]
    public void Present_invalid_or_unsupported_pool_is_rejected(string pool)
    {
        File.WriteAllText(Path.Combine(folder, "animation-contacts.json"),
            "{\"" + Ao + "\":{\"animations\":{},\"defaultWand\":{\"Wand\":" + pool + "}}}");
        Assert.Throws<InvalidDataException>(() => new AnimationContacts(folder)
            .TryDefaultWandContact(Ao, Wand(), 1, out _));
    }
}
