using NINA.Astrometry;
using NINA.Equipment.Interfaces.Mediator;
using TouchNStars.ConeError.Adapters;
using TouchNStars.ConeError.Session;
using Xunit;

namespace TouchNStars.Tests.ConeError;

public class ConeAdapterTests
{
    [Fact]
    public async Task TheNinaMountAdapterSlewsFlipsStopsAndTracksButNeverSyncs()
    {
        var calls = new List<string>();
        Coordinates? slewTarget = null;
        Coordinates? flipTarget = null;
        ITelescopeMediator telescope = InterfaceFake.Create<ITelescopeMediator>((method, args) =>
        {
            calls.Add(method.Name);
            if (method.Name == nameof(ITelescopeMediator.SlewToCoordinatesAsync))
            {
                slewTarget = (Coordinates)args![0]!;
            }

            if (method.Name == nameof(ITelescopeMediator.MeridianFlip))
            {
                flipTarget = (Coordinates)args![0]!;
            }

            return null;
        });
        var mount = new NinaConeMount(telescope);

        await mount.SlewAsync(12.5, 3.0, CancellationToken.None);
        await mount.FlipAsync(12.5, 3.0, CancellationToken.None);
        mount.Stop();
        mount.SetTracking(false);
        mount.Snapshot();

        Assert.DoesNotContain(nameof(ITelescopeMediator.Sync), calls);
        Assert.Contains(nameof(ITelescopeMediator.StopSlew), calls);
        Assert.Equal(Epoch.JNOW, slewTarget!.Epoch);
        Assert.Equal(12.5, slewTarget.RA, 1e-9);
        Assert.Equal(3.0, slewTarget.Dec, 1e-9);
        Assert.Equal(Epoch.JNOW, flipTarget!.Epoch);
        Assert.Equal(12.5, flipTarget.RA, 1e-9);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void SetTrackingReportsWhetherTrackingIsNowAsRequested(bool requested, bool stateAfter, bool expected)
    {
        // ITelescopeMediator.SetTrackingEnabled returns the tracking state after the call (TelescopeVM)
        ITelescopeMediator telescope = InterfaceFake.Create<ITelescopeMediator>((method, _) => method.Name switch
        {
            nameof(ITelescopeMediator.SetTrackingEnabled) => stateAfter,
            nameof(ITelescopeMediator.GetInfo) => new NINA.Equipment.Equipment.MyTelescope.TelescopeInfo { Connected = true },
            _ => null
        });

        Assert.Equal(expected, new NinaConeMount(telescope).SetTracking(requested));
    }

    [Fact]
    public void SetTrackingOnADisconnectedMountNeverSucceeds()
    {
        // TelescopeVM answers false when disconnected, which would read as "tracking off: done"
        ITelescopeMediator telescope = InterfaceFake.Create<ITelescopeMediator>((method, _) =>
            method.Name == nameof(ITelescopeMediator.SetTrackingEnabled) ? false : null);

        Assert.False(new NinaConeMount(telescope).SetTracking(false));
    }

    [Fact]
    public async Task ARefusedSlewIsAnError()
    {
        ITelescopeMediator telescope = InterfaceFake.Create<ITelescopeMediator>((method, _) =>
            method.Name == nameof(ITelescopeMediator.SlewToCoordinatesAsync) ? Task.FromResult(false) : null);

        ConeErrorException ex = await Assert.ThrowsAsync<ConeErrorException>(() => new NinaConeMount(telescope).SlewAsync(1.0, 2.0, CancellationToken.None));

        Assert.Equal("SlewFailed", ex.Code);
    }

    [Fact]
    public async Task AFlipTheMountDidNotCompleteIsAnError()
    {
        ITelescopeMediator telescope = InterfaceFake.Create<ITelescopeMediator>((method, _) =>
            method.Name == nameof(ITelescopeMediator.MeridianFlip) ? Task.FromResult(false) : null);

        ConeErrorException ex = await Assert.ThrowsAsync<ConeErrorException>(() => new NinaConeMount(telescope).FlipAsync(1.0, 2.0, CancellationToken.None));

        Assert.Equal("FlipFailed", ex.Code);
    }

    [Fact]
    public async Task AFlipThatThrowsIsAnErrorWithTheDriverMessage()
    {
        ITelescopeMediator telescope = InterfaceFake.Create<ITelescopeMediator>((method, _) =>
            method.Name == nameof(ITelescopeMediator.MeridianFlip) ? Task.FromException<bool>(new InvalidOperationException("side of pier not settable")) : null);

        ConeErrorException ex = await Assert.ThrowsAsync<ConeErrorException>(() => new NinaConeMount(telescope).FlipAsync(1.0, 2.0, CancellationToken.None));

        Assert.Equal("FlipFailed", ex.Code);
        Assert.Contains("side of pier not settable", ex.Message);
    }

    [Fact]
    public async Task ACancelledFlipStaysACancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ITelescopeMediator telescope = InterfaceFake.Create<ITelescopeMediator>((method, _) =>
            method.Name == nameof(ITelescopeMediator.MeridianFlip) ? Task.FromCanceled<bool>(cts.Token) : null);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NinaConeMount(telescope).FlipAsync(1.0, 2.0, cts.Token));
    }
}

public class ConeErrorStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cone-error-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SettingsRoundTripAndAreClamped()
    {
        var store = new ConeErrorStore(directory);

        store.SaveSettings(new ConeErrorSettings { MinAltitudeDegrees = 95.0, Repetitions = 40, DovetailMm = 210.0, ExposureSeconds = 500.0, VerifiedPierLabels = { "a", "a", " " } });
        ConeErrorSettings loaded = store.LoadSettings();

        Assert.Equal(80.0, loaded.MinAltitudeDegrees);
        Assert.Equal(10, loaded.Repetitions);
        Assert.Equal(210.0, loaded.DovetailMm);
        Assert.Null(loaded.ExposureSeconds);
        Assert.Equal(new[] { "a" }, loaded.VerifiedPierLabels);
    }

    [Fact]
    public void DefaultsWithoutAFile()
    {
        ConeErrorSettings settings = new ConeErrorStore(directory).LoadSettings();

        Assert.Equal(20.0, settings.MinAltitudeDegrees);
        Assert.Equal(3, settings.Repetitions);
        Assert.Null(settings.DovetailMm);
    }

    [Fact]
    public void KeepsTheLastFiftyResultsAndFindsThePreviousOne()
    {
        var store = new ConeErrorStore(directory);
        DateTime t0 = new(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 55; i++)
        {
            store.AddResult(new ConeResultRecord { Id = $"r{i}", Mode = "auto", ProfileId = "p", MountName = "m", TimestampUtc = t0.AddHours(i), ConeArcmin = i });
        }

        store.AddResult(new ConeResultRecord { Id = "other", Mode = "auto", ProfileId = "p", MountName = "other mount", TimestampUtc = t0.AddHours(100) });
        var current = new ConeResultRecord { Id = "now", Mode = "auto", ProfileId = "p", MountName = "m", TimestampUtc = t0.AddHours(200) };

        Assert.Equal(ConeErrorStore.MaxResults, store.LoadResults().Count);
        Assert.Equal("r54", store.PreviousResult(current).Id);
        Assert.DoesNotContain(store.LoadResults(), r => r.Id == "r0");
    }
}
