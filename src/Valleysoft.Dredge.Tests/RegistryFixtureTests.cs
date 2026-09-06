namespace Valleysoft.Dredge.Tests;

public sealed class RegistryFixtureTests
{
    [Fact]
    public async Task InitializeAsync_DoesNotStartRegistry()
    {
        int startCount = 0;
        RegistryFixture fixture = new(_ =>
        {
            startCount++;
            return Task.FromResult(
                new RegistryFixture.RegistryInstance(
                    new TrackingAsyncDisposable(),
                    "localhost:5000"));
        });

        await fixture.InitializeAsync();
        await fixture.DisposeAsync();

        Assert.Equal(0, startCount);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ConcurrentCallsStartAndDisposeRegistryOnce()
    {
        int startCount = 0;
        TrackingAsyncDisposable container = new();
        RegistryFixture fixture = new(async _ =>
        {
            Interlocked.Increment(ref startCount);
            await Task.Yield();
            return new(container, "localhost:5000");
        });

        await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => fixture.EnsureInitializedAsync()));
        await fixture.DisposeAsync();

        Assert.Equal(1, startCount);
        Assert.Equal(1, container.DisposeCount);
        Assert.Equal("localhost:5000", fixture.Registry);
        Assert.Equal(new Uri("http://localhost:5000/"), fixture.BaseUri);
    }

    [Fact]
    public async Task DisposeAsync_AfterInitializationFailureDeletesFixtureDirectory()
    {
        string? fixtureDirectory = null;
        RegistryFixture fixture = new(configPath =>
        {
            fixtureDirectory = Path.GetDirectoryName(configPath);
            return Task.FromException<RegistryFixture.RegistryInstance>(
                new InvalidOperationException("Registry failed to start."));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            fixture.EnsureInitializedAsync);
        await fixture.DisposeAsync();

        Assert.NotNull(fixtureDirectory);
        Assert.False(Directory.Exists(fixtureDirectory));
    }

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        private int disposeCount;

        public int DisposeCount => disposeCount;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
