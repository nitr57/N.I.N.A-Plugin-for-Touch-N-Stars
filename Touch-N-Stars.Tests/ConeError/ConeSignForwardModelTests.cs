using TouchNStars.ConeError.Domain;
using Xunit;

namespace TouchNStars.Tests.ConeError;

/// <summary>
/// The acceptance tests of the sign convention: a physical tube tilt is injected into the
/// independent <see cref="ConeForwardModel"/>, the estimator (<see cref="ConeMath"/>) must recover
/// its size and pick the end the model says is closer to the saddle.
/// </summary>
public class ConeSignForwardModelTests
{
    private const double MountingPointsMm = 200.0;

    private static double Estimate(ConeForwardModel world, double declination, double haEast, double haWest)
    {
        double dRaEast = world.DeltaRaHours(PierSideLabel.East, haEast, declination);
        double dRaWest = world.DeltaRaHours(PierSideLabel.West, haWest, declination);
        return ConeMath.PairConeArcmin(dRaWest, dRaEast, declination);
    }

    [Theory]
    [InlineData(5.0, 50.0, 0.0)]
    [InlineData(-5.0, 50.0, 0.0)]
    [InlineData(12.0, 50.0, 15.0)]
    [InlineData(-12.0, 50.0, 30.0)]
    [InlineData(3.0, -35.0, 0.0)]
    [InlineData(-3.0, -35.0, -20.0)]
    [InlineData(8.0, 10.0, 20.0)]
    [InlineData(-0.5, 64.0, 10.0)]
    public void RecoversTheInjectedTiltAndTheEndCloserToTheSaddle(double tiltArcmin, double latitude, double declination)
    {
        var world = new ConeForwardModel { LatitudeDegrees = latitude, TiltAwayFromSaddleArcmin = tiltArcmin };

        double cone = Estimate(world, declination, haEast: -0.01, haWest: 0.01);

        Assert.Equal(-tiltArcmin, cone, 0.01);
        Assert.Equal(world.ExpectedShimEnd(), ConeMath.ShimEndFor(cone));
        Assert.Equal(world.ExpectedShimMm(MountingPointsMm), ConeMath.ShimThicknessMm(cone, MountingPointsMm), 1e-3);
    }

    [Fact]
    public void ObjectiveTiltedTowardTheSaddleIsShimmedAtTheFront()
    {
        var world = new ConeForwardModel { TiltAwayFromSaddleArcmin = -6.0 };

        double cone = Estimate(world, 0.0, -0.02, 0.02);

        Assert.True(cone > 0.0);
        Assert.Equal(ShimEnd.Front, world.ExpectedShimEnd());
        Assert.Equal(ShimEnd.Front, ConeMath.ShimEndFor(cone));
    }

    [Fact]
    public void ObjectiveTiltedAwayFromTheSaddleIsShimmedAtTheRear()
    {
        var world = new ConeForwardModel { TiltAwayFromSaddleArcmin = 6.0 };

        double cone = Estimate(world, 0.0, -0.02, 0.02);

        Assert.True(cone < 0.0);
        Assert.Equal(ShimEnd.Rear, world.ExpectedShimEnd());
        Assert.Equal(ShimEnd.Rear, ConeMath.ShimEndFor(cone));
    }

    [Theory]
    [InlineData(50.0, 5.0, -4.0, 30.0, -10.0, 0.02)]
    [InlineData(-30.0, 5.0, -4.0, 30.0, -10.0, 0.02)]
    [InlineData(50.0, 20.0, -15.0, 30.0, -25.0, 0.2)]
    public void PolarMisalignmentAndIndexErrorsCancelWhenThePairIsSymmetricAboutTheMeridian(
        double latitude,
        double polarX,
        double polarY,
        double indexRa,
        double indexDec,
        double tolerance)
    {
        // A sync / RA index offset cancels exactly, polar misalignment cancels to first order. A Dec
        // index error puts the two sides at different true declinations, so the tan δ term of the polar
        // misalignment leaves a small second-order residual: 0.15′ for 20′ × 25′, 0.01′ for 5′ × 10′.
        var world = new ConeForwardModel
        {
            LatitudeDegrees = latitude,
            TiltAwayFromSaddleArcmin = 4.0,
            PolarAboutXArcmin = polarX,
            PolarAboutYArcmin = polarY,
            IndexRaArcmin = indexRa,
            IndexDecArcmin = indexDec
        };

        double cone = Estimate(world, 10.0, haEast: -0.02, haWest: 0.02);

        Assert.Equal(-4.0, cone, tolerance);
        Assert.Equal(ShimEnd.Rear, ConeMath.ShimEndFor(cone));
    }

    [Fact]
    public void NonPerpendicularityVanishesAtDeclinationZero()
    {
        var world = new ConeForwardModel { NonPerpendicularityArcmin = 10.0 };

        Assert.Equal(0.0, Estimate(world, 0.0, -0.01, 0.01), 0.01);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(20.0)]
    [InlineData(30.0)]
    public void NonPerpendicularityGrowsWithDeclination(double declination)
    {
        var world = new ConeForwardModel { NonPerpendicularityArcmin = 10.0 };

        double cone = Math.Abs(Estimate(world, declination, -0.01, 0.01));

        // The measured value is CH + NP·sin δ: the residual of NP grows with sin δ (a documented limitation).
        Assert.Equal(10.0 * Math.Sin(declination * Math.PI / 180.0), cone, 0.05);
    }

    [Fact]
    public void InvertedDriverLabelsFlipTheAdviceWhichIsWhyTheUserConfirmsTheSideOnce()
    {
        var honest = new ConeForwardModel { TiltAwayFromSaddleArcmin = 5.0 };
        var inverted = new ConeForwardModel { TiltAwayFromSaddleArcmin = 5.0, InvertedLabels = true };

        double good = Estimate(honest, 0.0, -0.01, 0.01);
        double bad = Estimate(inverted, 0.0, -0.01, 0.01);

        Assert.Equal(-good, bad, 1e-3);
        Assert.NotEqual(inverted.ExpectedShimEnd(), ConeMath.ShimEndFor(bad));
    }

    [Theory]
    [InlineData(5.0, 1.0)]
    [InlineData(-7.0, 2.0)]
    [InlineData(2.0, 0.0)]
    public void DaytimeEstimateRecoversTheTiltAfterTheParallaxCorrection(double tiltArcmin, double landmarkKm)
    {
        const double offsetMm = 250.0;
        var world = new ConeForwardModel
        {
            LatitudeDegrees = 50.0,
            TiltAwayFromSaddleArcmin = tiltArcmin,
            AxisOffsetMm = landmarkKm > 0 ? offsetMm : 0.0
        };

        // A landmark 2° above the horizon, 1° west of due south.
        Vector3 landmark = ConeForwardModel.Direction(0.02, -38.0);
        double distance = landmarkKm > 0 ? landmarkKm * 1000.0 : 1e12;

        (double haEast, double decEast) = world.CentreOnLandmark(PierSideLabel.East, landmark, distance);
        (double haWest, _) = world.CentreOnLandmark(PierSideLabel.West, landmark, distance);

        double measured = ConeMath.ManualConeArcmin(haWest, haEast, decEast);
        double parallax = landmarkKm > 0 ? ConeMath.ParallaxArcmin(offsetMm, distance) : 0.0;
        double corrected = measured + parallax;

        Assert.Equal(-tiltArcmin, corrected, 0.01);
        Assert.Equal(world.ExpectedShimEnd(), ConeMath.ShimEndFor(corrected));
        if (landmarkKm > 0)
        {
            // Uncorrected, a 1 km landmark would bias the estimate by about 0.86′ toward "shim the rear".
            Assert.True(measured < corrected);
        }
    }

    [Fact]
    public void DaytimeAndPlateSolveEstimatesAgree()
    {
        var world = new ConeForwardModel { TiltAwayFromSaddleArcmin = -4.0 };
        Vector3 landmark = ConeForwardModel.Direction(0.01, -30.0);

        (double haEast, double dec) = world.CentreOnLandmark(PierSideLabel.East, landmark, 1e12);
        (double haWest, _) = world.CentreOnLandmark(PierSideLabel.West, landmark, 1e12);

        double daytime = ConeMath.ManualConeArcmin(haWest, haEast, dec);
        double night = Estimate(world, -30.0, -0.01, 0.01);

        Assert.Equal(night, daytime, 0.01);
    }
}
