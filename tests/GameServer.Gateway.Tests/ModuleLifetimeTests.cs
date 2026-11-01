using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class ModuleLifetimeTests
{
    [Fact]
    public void StopDeactivatesBeforeCancellationAndReleasesEachOwnershipDespiteFailures()
    {
        var lifetime = new ModuleLifetime("phrecia");
        IModuleLifetime api = lifetime;
        var stopping = api.Stopping;
        var failures = new List<Exception>();
        var released = new List<string>();

        Assert.Equal("phrecia", api.Id);
        Assert.True(api.IsActive);
        Assert.False(stopping.IsCancellationRequested);

        var early = api.Own(new Resource(() => released.Add("early")));
        early.Dispose();
        early.Dispose();
        Assert.Equal(new[] { "early" }, released);

        var throwing = api.Own(new Resource(() =>
        {
            Assert.False(api.IsActive);
            Assert.True(stopping.IsCancellationRequested);
            released.Add("throwing");
            throw new InvalidOperationException("owned cleanup failed");
        }));
        var ordinary = api.Own(new Resource(() =>
        {
            Assert.False(api.IsActive);
            Assert.True(stopping.IsCancellationRequested);
            Assert.Contains("cancel", released);
            released.Add("ordinary");
        }));
        using var cancellation = stopping.Register(() =>
        {
            Assert.False(api.IsActive);
            released.Add("cancel");
            // Reentrant stop is inert, and ownership during cancellation disposes immediately.
            lifetime.Stop(failures.Add);
            using var during = api.Own(new Resource(() => released.Add("during cancellation")));
            Assert.Contains("during cancellation", released);
            throw new InvalidOperationException("cancellation callback failed");
        });

        lifetime.Stop(failures.Add);
        lifetime.Stop(failures.Add);
        ordinary.Dispose();
        throwing.Dispose();
        Assert.False(api.IsActive);
        Assert.True(api.Stopping.IsCancellationRequested);
        Assert.Equal(5, released.Count);
        Assert.Equal(5, released.Distinct().Count());
        Assert.Equal("cancel", released[1]);
        Assert.Equal(2, failures.Count);
        var cancelled = Assert.IsType<AggregateException>(failures[0]);
        Assert.IsType<InvalidOperationException>(Assert.Single(cancelled.InnerExceptions));
        Assert.IsType<InvalidOperationException>(failures[1]);

        var late = api.Own(new Resource(() => released.Add("late")));
        Assert.Equal("late", released[^1]);
        late.Dispose();
        Assert.Equal(6, released.Count);

        var replacement = new ModuleLifetime(api.Id);
        Assert.True(replacement.Generation > api.Generation);
        Assert.True(replacement.IsActive);
        Assert.False(api.IsActive);
        replacement.Stop(failures.Add);
    }

    sealed class Resource(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
