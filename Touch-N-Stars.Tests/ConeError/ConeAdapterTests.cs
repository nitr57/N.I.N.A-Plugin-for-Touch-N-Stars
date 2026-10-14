using NINA.Astrometry;
using NINA.Equipment.Interfaces.Mediator;
using NINA.INDI.Protocol;
using TouchNStars.ConeError.Adapters;
using TouchNStars.ConeError.Session;
using Xunit;

namespace TouchNStars.Tests.ConeError;

public class ConeAdapterTests
{
    [Fact]
    public async Task TheNinaMountAdapterSlewsStopsAndTracksButNeverSyncs()
    {
        var calls = new List<string>();
        Coordinates? slewTarget = null;
        ITelescopeMediator telescope = InterfaceFake.Create<ITelescopeMediator>((method, args) =>
        {
            calls.Add(method.Name);
            if (method.Name == nameof(ITelescopeMediator.SlewToCoordinatesAsync))
            {
                slewTarget = (Coordinates)args![0]!;
            }

            return null;
        });
        var mount = new NinaConeMount(telescope);

        await mount.SlewAsync(12.5, 3.0, CancellationToken.None);
        mount.Stop();
        mount.SetTracking(false);
        mount.Snapshot();

        Assert.DoesNotContain(nameof(ITelescopeMediator.Sync), calls);
        Assert.Contains(nameof(ITelescopeMediator.StopSlew), calls);
        Assert.Contains(nameof(ITelescopeMediator.SetTrackingEnabled), calls);
        Assert.Equal(Epoch.JNOW, slewTarget!.Epoch);
        Assert.Equal(12.5, slewTarget.RA, 1e-9);
        Assert.Equal(3.0, slewTarget.Dec, 1e-9);
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
    public void IndiOnStepLimitsAreConvertedFromMinutesOfTime()
    {
        var snapshot = new INDIDeviceSnapshot
        {
            Device = "LX200 OnStep",
            Properties =
            {
                Property(NinaMountDriverInspector.IndiMinutesPastMeridian, "Ok", ("East", 20.0), ("West", -8.0)),
                Property(NinaMountDriverInspector.IndiElevationLimit, "Ok", ("minAlt", -5.0), ("maxAlt", 85.0))
            }
        };

        MountLimits limits = NinaMountDriverInspector.IndiOnStepLimits(snapshot);

        Assert.Equal(5.0, limits.PastMeridianEastDegrees);
        Assert.Equal(-2.0, limits.PastMeridianWestDegrees);
        Assert.Equal(-5.0, limits.HorizonDegrees);
        Assert.Equal(85.0, limits.OverheadDegrees);
        Assert.Equal("INDI OnStep", limits.Source);
    }

    [Fact]
    public void UnconfirmedIndiLimitsAreIgnored()
    {
        var snapshot = new INDIDeviceSnapshot
        {
            Properties = { Property(NinaMountDriverInspector.IndiMinutesPastMeridian, "Alert", ("East", 20.0), ("West", 20.0)) }
        };

        MountLimits limits = NinaMountDriverInspector.IndiOnStepLimits(snapshot);

        Assert.Null(limits.PastMeridianEastDegrees);
        Assert.Null(limits.HorizonDegrees);
    }

    [Theory]
    [InlineData(new long[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 }, PointingModelState.None)]
    [InlineData(new long[] { 12, -40, 0, 0, 185, 0, 0, 0, 0 }, PointingModelState.Active)]
    [InlineData(new long[] { 0, 0, 0, 0, 0, 0, 0, 3, 0 }, PointingModelState.Active)]
    public void OnStepModelTermsAreClassified(long[] terms, PointingModelState expected)
    {
        PointingModelInfo info = OnStepModelProbe.Classify(terms);

        Assert.Equal(expected, info.State);
        Assert.Equal(terms[4], info.ConeTermArcsec);
    }

    [Fact]
    public void ModelProbeOverASerialIndiConnectionIsUnknown()
    {
        ITelescopeMediator telescope = InterfaceFake.Create<ITelescopeMediator>((method, _) =>
            method.Name == nameof(ITelescopeMediator.SendCommandString) ? throw new NotImplementedException("only over TCP") : null);

        Assert.Equal(PointingModelState.Unknown, OnStepModelProbe.Probe(telescope).State);
    }

    [Fact]
    public void ModelProbeReadsTheConeTerm()
    {
        ITelescopeMediator telescope = InterfaceFake.Create<ITelescopeMediator>((method, args) =>
            method.Name == nameof(ITelescopeMediator.SendCommandString) ? ((string)args![0]! == ":GX04#" ? "-210" : "0") : null);

        PointingModelInfo info = OnStepModelProbe.Probe(telescope);

        Assert.Equal(PointingModelState.Active, info.State);
        Assert.Equal(-210, info.ConeTermArcsec);
    }

    private static INDIPropertySnapshot Property(string name, string state, params (string Name, double Value)[] elements)
    {
        var property = new INDIPropertySnapshot { Name = name, State = state, Type = "number" };
        foreach ((string element, double value) in elements)
        {
            property.Elements.Add(new INDIElementSnapshot { Name = element, Value = value });
        }

        return property;
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

        store.SaveSettings(new ConeErrorSettings { SunExclusionDegrees = 5.0, Repetitions = 40, DovetailMm = 210.0, StrictPath = true });
        ConeErrorSettings loaded = store.LoadSettings();

        Assert.Equal(ConeErrorSettings.MinSunExclusionDegrees, loaded.SunExclusionDegrees);
        Assert.Equal(10, loaded.Repetitions);
        Assert.Equal(210.0, loaded.DovetailMm);
        Assert.True(loaded.StrictPath);
    }

    [Fact]
    public void DefaultsWithoutAFile()
    {
        ConeErrorSettings settings = new ConeErrorStore(directory).LoadSettings();

        Assert.Equal(30.0, settings.SunExclusionDegrees);
        Assert.Equal(5, settings.Repetitions);
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

    [Fact]
    public void JournalRoundTripAndClear()
    {
        var store = new ConeErrorStore(directory);

        store.SaveJournal(new RestoreJournal { Mode = "manual", PreferredPierSide = "West", Tracking = true });
        RestoreJournal journal = store.LoadJournal();
        store.ClearJournal();

        Assert.Equal("West", journal.PreferredPierSide);
        Assert.True(journal.Tracking);
        Assert.Null(store.LoadJournal());
    }
}
