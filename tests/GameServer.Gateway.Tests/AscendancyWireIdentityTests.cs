using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AscendancyWireIdentityTests
{
    [Fact]
    public void Every_extracted_choice_has_an_unambiguous_family_and_native_identity()
    {
        string? folder = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, AscendancyCatalog.GraphFile))) { folder = candidate; break; }
        }
        if (folder is null) return; // The repository supports running without proprietary client data.
        var catalog = new AscendancyCatalog(folder);
        Assert.Equal(40, catalog.Definitions.Count);
        var identities = new HashSet<(AscendancyFamily Family, byte Packed)>();
        foreach (var definition in catalog.Definitions)
        {
            Assert.InRange(definition.ClassNo, (byte)1, (byte)3);
            byte packed = CharacterClassByte.Encode(definition.BaseClass, definition.ClassNo);
            Assert.True(identities.Add((definition.Identity.Family, packed)), definition.Name);
            Assert.Equal(definition.BaseClass, CharacterClassByte.BaseClass(packed));
            Assert.Equal(definition.ClassNo, CharacterClassByte.AscendancyClassNo(packed));
            var start = Assert.Single(definition.Nodes.Values, n => n.IsStart);
            Assert.Equal(definition.StartNode, start.Id);
            Assert.Equal(0, start.PointCost);
            Assert.All(definition.Nodes.Values, node => Assert.All(node.Adjacent,
                next => Assert.True(definition.Nodes.ContainsKey(next), $"{definition.Name}: {node.Id} -> {next}")));
            if (definition.Identity.Family == AscendancyFamily.Phrecia)
            {
                var ordinary = Assert.Single(catalog.Definitions, other => other.Identity.Family == AscendancyFamily.Normal &&
                    other.Identity.Id == definition.Identity.Id);
                Assert.Equal(ordinary.BaseClass, definition.BaseClass);
                Assert.Equal(ordinary.ClassNo, definition.ClassNo);
                Assert.Equal(ordinary.StartNode, definition.StartNode);
                Assert.NotEqual(ordinary.Name, definition.Name);
            }
        }
        for (byte baseClass = 0; baseClass <= 6; baseClass++)
            Assert.Equal(baseClass == 0 ? 4 : 6, catalog.Choices(baseClass).Count());
        Assert.Single(catalog.Definitions, d => d.Identity.Family == AscendancyFamily.Phrecia && d.BaseClass == 0);
    }
}
