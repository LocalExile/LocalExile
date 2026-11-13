using GameServer.Core;

namespace GameServer.Gateway.Tests;

public sealed class ClientTableCacheKeyTests
{
    [Fact]
    public void Derived_cache_key_follows_table_overrides_and_restores_outer_scope()
    {
        var original = ClientTables.CacheKey;
        using (ClientTables.Use(ClientTable.FromRows("outer", [])))
        {
            var outer = ClientTables.CacheKey;
            Assert.NotEqual(original, outer);
            Assert.Same(outer, ClientTables.CacheKey);
            using (ClientTables.Use(ClientTable.FromRows("inner", [])))
                Assert.NotEqual(outer, ClientTables.CacheKey);
            Assert.Same(outer, ClientTables.CacheKey);
        }
        Assert.Equal(original, ClientTables.CacheKey);
    }
}
