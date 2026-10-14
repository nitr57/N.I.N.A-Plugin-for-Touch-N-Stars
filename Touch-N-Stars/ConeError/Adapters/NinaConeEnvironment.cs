using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using TouchNStars.ConeError.Domain;
using TouchNStars.ConeError.Session;
using TouchNStars.Server.Services;

namespace TouchNStars.ConeError.Adapters;

/// <summary>The cone error ports bound to NINA's mediators and profile.</summary>
public sealed class NinaConeEnvironment : IConeEnvironment
{
    public NinaConeEnvironment(Mediators mediators)
    {
        if (mediators == null)
        {
            throw new ConeErrorException("NotReady", "The plugin is still starting.");
        }

        Mount = new NinaConeMount(mediators.Telescope);
        Imager = new NinaConeImager(mediators.Imaging, mediators.Camera, mediators.Profile, mediators.PlateSolverFactory);
        Driver = new NinaMountDriverInspector(mediators.Telescope);
        Observatory = new NinaObservatory(mediators);
        Sun = new MeeusSunProvider(() => mediators.Profile.ActiveProfile.AstrometrySettings.Longitude);
    }

    public IConeMount Mount { get; }
    public IConeImager Imager { get; }
    public IMountDriverInspector Driver { get; }
    public IObservatory Observatory { get; }
    public IClock Clock => SystemClock.Instance;
    public ISunProvider Sun { get; }

    public static PierSideLabel? Label(PierSide side) => side switch
    {
        PierSide.pierEast => PierSideLabel.East,
        PierSide.pierWest => PierSideLabel.West,
        _ => null
    };
}

/// <summary>Mount through <see cref="ITelescopeMediator"/>; live values from the device, positions of date. No sync.</summary>
public sealed class NinaConeMount : IConeMount
{
    private readonly ITelescopeMediator telescope;

    public NinaConeMount(ITelescopeMediator telescope)
    {
        this.telescope = telescope;
    }

    public MountSnapshot Snapshot()
    {
        TelescopeInfo info = telescope.GetInfo();
        if (info == null || !info.Connected)
        {
            return new MountSnapshot(false, false, false, false, null, 0, 0, 0, 0, 0, info?.Name, DateTime.UtcNow);
        }

        var device = telescope.GetDevice() as ITelescope;
        Coordinates position = telescope.GetCurrentPosition()?.Transform(Epoch.JNOW) ?? info.Coordinates?.Transform(Epoch.JNOW);
        return new MountSnapshot(
            true,
            device?.AtPark ?? info.AtPark,
            device?.TrackingEnabled ?? info.TrackingEnabled,
            device?.Slewing ?? info.Slewing,
            NinaConeEnvironment.Label(device?.SideOfPier ?? info.SideOfPier),
            device?.SiderealTime ?? info.SiderealTime,
            position?.RA ?? info.RightAscension,
            position?.Dec ?? info.Declination,
            info.SiteLatitude,
            info.SiteLongitude,
            info.Name,
            DateTime.UtcNow);
    }

    public async Task SlewAsync(double rightAscensionHours, double declinationDegrees, CancellationToken token)
    {
        var target = new Coordinates(Angle.ByHours(rightAscensionHours), Angle.ByDegree(declinationDegrees), Epoch.JNOW);
        bool ok;
        try
        {
            ok = await telescope.SlewToCoordinatesAsync(target, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ConeErrorException("SlewFailed", $"The mount refused the slew: {ex.Message}");
        }

        token.ThrowIfCancellationRequested();
        if (!ok)
        {
            throw new ConeErrorException("SlewFailed", "The mount refused the slew or did not finish it.");
        }
    }

    public void Stop() => telescope.StopSlew();

    public bool SetTracking(bool enabled) => telescope.SetTrackingEnabled(enabled);
}

/// <summary>Capture through NINA's imaging mediator and solve with the profile's plate solver; never CenteringSolver (it syncs).</summary>
public sealed class NinaConeImager : IConeImager
{
    private readonly IImagingMediator imaging;
    private readonly ICameraMediator camera;
    private readonly IProfileService profile;
    private readonly IPlateSolverFactory factory;

    public NinaConeImager(IImagingMediator imaging, ICameraMediator camera, IProfileService profile, IPlateSolverFactory factory)
    {
        this.imaging = imaging;
        this.camera = camera;
        this.profile = profile;
        this.factory = factory;
    }

    public async Task<SolveOutcome> CaptureAndSolveAsync(double exposureSeconds, double hintRaHours, double hintDecDegrees, CancellationToken token)
    {
        if (factory == null)
        {
            return SolveOutcome.Failed("no plate solver is available");
        }

        IPlateSolveSettings pss = profile.ActiveProfile.PlateSolveSettings;
        var sequence = new CaptureSequence
        {
            Binning = new BinningMode(pss.Binning, pss.Binning),
            Gain = pss.Gain,
            ExposureTime = exposureSeconds,
            Offset = -1,
            FilterType = pss.Filter,
            ImageType = CaptureSequence.ImageTypes.SNAPSHOT
        };

        DateTime started = DateTime.UtcNow;
        IRenderedImage image = await imaging.CaptureAndPrepareImage(sequence, new PrepareImageParameters(true, false), token, null);
        if (image?.RawImageData == null)
        {
            return SolveOutcome.Failed("no image");
        }

        DateTime mid = image.RawImageData.MetaData?.Image?.ExposureMidPoint ?? DateTime.MinValue;
        if (mid == DateTime.MinValue)
        {
            mid = started.AddSeconds(exposureSeconds / 2.0);
        }

        mid = mid.Kind == DateTimeKind.Local ? mid.ToUniversalTime() : DateTime.SpecifyKind(mid, DateTimeKind.Utc);

        var parameter = new PlateSolveParameter
        {
            Binning = pss.Binning,
            Coordinates = new Coordinates(Angle.ByHours(hintRaHours), Angle.ByDegree(hintDecDegrees), Epoch.JNOW),
            DownSampleFactor = pss.DownSampleFactor,
            FocalLength = profile.ActiveProfile.TelescopeSettings.FocalLength,
            MaxObjects = pss.MaxObjects,
            PixelSize = profile.ActiveProfile.CameraSettings.PixelSize,
            Regions = pss.Regions,
            SearchRadius = pss.SearchRadius,
            DisableNotifications = true,
            BlindFailoverEnabled = pss.BlindFailoverEnabled
        };

        IImageSolver solver = factory.GetImageSolver(factory.GetPlateSolver(pss), factory.GetBlindSolver(pss));
        PlateSolveResult result = await solver.Solve(image.RawImageData, parameter, null, token);
        if (result == null || !result.Success || result.Coordinates == null)
        {
            return SolveOutcome.Failed("no solution");
        }

        Coordinates ofDate = result.Coordinates.Transform(Epoch.JNOW, mid);
        return new SolveOutcome(true, ofDate.RA, ofDate.Dec, mid, null);
    }

    public void AbortExposure() => camera?.AbortExposure();
}

/// <summary>Profile and observatory state for the preconditions.</summary>
public sealed class NinaObservatory : IObservatory
{
    private readonly Mediators mediators;

    public NinaObservatory(Mediators mediators)
    {
        this.mediators = mediators;
    }

    public ProfileFacts Profile()
    {
        IProfile p = mediators.Profile.ActiveProfile;
        IPlateSolveSettings pss = p.PlateSolveSettings;
        var horizon = p.AstrometrySettings.Horizon;
        Func<double, double> horizonAt = horizon == null ? null : az => horizon.GetAltitude(az);
        return new ProfileFacts(
            p.Id.ToString(),
            p.Name,
            p.AstrometrySettings.Latitude,
            p.AstrometrySettings.Longitude,
            horizonAt,
            p.MeridianFlipSettings.MaxMinutesAfterMeridian,
            p.TelescopeSettings.SettleTime,
            pss.ExposureTime,
            p.TelescopeSettings.FocalLength,
            pss.PlateSolverType.ToString(),
            SolverProblem(pss));
    }

    /// <summary>The field has no internet: only a local solver works.</summary>
    private static string SolverProblem(IPlateSolveSettings pss)
    {
        switch (pss.PlateSolverType)
        {
            case PlateSolverEnum.ASTAP:
                string location = string.IsNullOrWhiteSpace(pss.ASTAPLocation) ? "/usr/local/bin/astap_cli" : pss.ASTAPLocation;
                return File.Exists(location) ? null : $"ASTAP was not found at {location}. Install ASTAP and a star database (D50 or D05) through the PINS settings.";
            case PlateSolverEnum.ASTROMETRY_NET:
                return pss.AstrometryURL != null && pss.AstrometryURL.Contains("nova.astrometry.net", StringComparison.OrdinalIgnoreCase)
                    ? "The plate solver is set to nova.astrometry.net, which needs internet. Select ASTAP."
                    : null;
            default:
                return $"The plate solver {pss.PlateSolverType} is not available on PINS. Select ASTAP.";
        }
    }

    public ObservatoryState State()
    {
        var camera = mediators.Camera?.GetInfo();
        bool guiding = false;
        try
        {
            var guider = InternalGuiderService.Instance.GetConnectedGuider(out _);
            guiding = guider != null && InternalGuiderStates.IsGuiding(guider.State);
        }
        catch (Exception ex)
        {
            Logger.Debug($"ConeError: reading the internal guider state failed: {ex.Message}");
        }

        return new ObservatoryState(
            camera?.Connected == true,
            camera?.IsExposing == true,
            ImagingActivity.IsSequenceRunning(),
            guiding);
    }
}
