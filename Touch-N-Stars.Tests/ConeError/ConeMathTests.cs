using TouchNStars.ConeError.Domain;
using Xunit;

namespace TouchNStars.Tests.ConeError;

public class ConeMathTests
{
    [Theory]
    [InlineData(0.01, 23.99, 0.02)]
    [InlineData(23.99, 0.01, -0.02)]
    [InlineData(12.0, 11.5, 0.5)]
    [InlineData(5.0, 5.0, 0.0)]
    public void DeltaRaWrapsAcrossZeroHours(double solved, double mount, double expected)
    {
        Assert.Equal(expected, ConeMath.DeltaRaHours(solved, mount), 1e-9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(15.0)]
    [InlineData(30.0)]
    [InlineData(-25.0)]
    public void PairConeUsesHalfTheDifferenceOnTheSky(double declination)
    {
        // ΔRA_W − ΔRA_E of 1 minute of RA (15′ of RA) at δ: c = ½ · 15′ · cos δ.
        double cone = ConeMath.PairConeArcmin(0.5 / 60.0, -0.5 / 60.0, declination);

        Assert.Equal(7.5 * Math.Cos(declination * Math.PI / 180.0), cone, 1e-9);
    }

    [Fact]
    public void PairConeHandlesOffsetsStraddlingTheWrap()
    {
        double west = ConeMath.DeltaRaHours(0.001, 23.999);
        double east = ConeMath.DeltaRaHours(23.999, 0.001);

        Assert.Equal(0.5 * 0.004 * 900.0, ConeMath.PairConeArcmin(west, east, 0.0), 1e-9);
    }

    [Theory]
    [InlineData(5.0, 200.0, 0.2909)]
    [InlineData(-5.0, 200.0, 0.2909)]
    [InlineData(1.0, 150.0, 0.0436)]
    [InlineData(0.0, 200.0, 0.0)]
    public void ShimThicknessIsLTimesTanOfTheAbsoluteCone(double coneArcmin, double lengthMm, double expectedMm)
    {
        Assert.Equal(expectedMm, ConeMath.ShimThicknessMm(coneArcmin, lengthMm), 1e-4);
    }

    [Theory]
    [InlineData(0.3, ShimEnd.Front)]
    [InlineData(-0.3, ShimEnd.Rear)]
    [InlineData(0.0, ShimEnd.None)]
    public void ShimEndFollowsTheSign(double cone, ShimEnd expected)
    {
        Assert.Equal(expected, ConeMath.ShimEndFor(cone));
    }

    [Fact]
    public void ParallaxOfAOneKilometreLandmark()
    {
        // 250 mm at 1 km: atan(0.25 / 1000) = 0.8594′.
        Assert.Equal(0.8594, ConeMath.ParallaxArcmin(250.0, 1000.0), 1e-4);
        Assert.Throws<ArgumentOutOfRangeException>(() => ConeMath.ParallaxArcmin(250.0, 0.0));
    }

    [Theory]
    [InlineData(PierSideLabel.East, 0.1, -1.5)]
    [InlineData(PierSideLabel.East, -0.1, 1.5)]
    [InlineData(PierSideLabel.West, 0.1, 1.5)]
    [InlineData(PierSideLabel.West, -0.1, -1.5)]
    [InlineData(PierSideLabel.East, 23.9, 1.5)]
    public void PastMeridianIsPositiveOnTheCounterweightUpSide(PierSideLabel side, double ha, double expected)
    {
        Assert.Equal(expected, ConeMath.PastMeridianDegrees(side, ha), 1e-9);
    }
}

public class ConeStatisticsTests
{
    private static List<double> Noisy(double value, double sigma, int count, int seed)
    {
        var random = new Random(seed);
        var list = new List<double>();
        for (int i = 0; i < count; i++)
        {
            // Box-Muller
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            list.Add(value + sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        return list;
    }

    [Theory]
    [InlineData(4.0, 1)]
    [InlineData(-4.0, 2)]
    [InlineData(1.5, 3)]
    [InlineData(-0.8, 4)]
    public void MeanOfNoisyPairsRecoversTheInjectedCone(double cone, int seed)
    {
        ConeSummary summary = ConeStatistics.Summarize(Noisy(cone, 0.3, 8, seed));

        Assert.Equal(8, summary.Used);
        Assert.InRange(summary.Mean, cone - 3.0 * summary.StdError, cone + 3.0 * summary.StdError);
        Assert.True(summary.Significant);
        Assert.Equal(ConeMath.ShimEndFor(cone), ConeMath.ShimEndFor(summary.Mean));
    }

    [Fact]
    public void RejectsAnOutlierAndKeepsTheSign()
    {
        var values = new List<double> { 3.1, 2.9, 3.0, 3.2, 2.8, -9.0 };

        ConeSummary summary = ConeStatistics.Summarize(values);

        Assert.Equal(new[] { 5 }, summary.RejectedIndices);
        Assert.Equal(5, summary.Used);
        Assert.Equal(3.0, summary.Mean, 1e-9);
        Assert.Equal(ShimEnd.Front, ConeMath.ShimEndFor(summary.Mean));
    }

    [Fact]
    public void DoesNotRejectWithFewerThanFourValues()
    {
        ConeSummary summary = ConeStatistics.Summarize(new List<double> { 3.0, 3.1, 12.0 });

        Assert.Empty(summary.RejectedIndices);
        Assert.Equal(3, summary.Used);
    }

    [Fact]
    public void DoesNotRejectNearlyIdenticalValues()
    {
        ConeSummary summary = ConeStatistics.Summarize(new List<double> { 2.0, 2.0, 2.0, 2.05, 1.97 });

        Assert.Empty(summary.RejectedIndices);
    }

    [Fact]
    public void ASingleValueStandsAsMeasuredWithoutASpread()
    {
        ConeSummary summary = ConeStatistics.Summarize(new List<double> { 2.0 });

        Assert.Equal(ConeQuality.Single, summary.Quality);
        Assert.True(summary.Significant);
        Assert.Equal(2.0, summary.Mean);
        Assert.True(double.IsNaN(summary.StdDev));
        Assert.True(double.IsNaN(summary.StdError));
    }

    [Fact]
    public void NoValueIsInsufficient()
    {
        ConeSummary summary = ConeStatistics.Summarize(new List<double>());

        Assert.Equal(ConeQuality.Insufficient, summary.Quality);
        Assert.False(summary.Significant);
    }

    [Theory]
    [InlineData(0.4, 0.2, ConeQuality.Good)]
    [InlineData(1.2, 0.5, ConeQuality.Fair)]
    [InlineData(2.0, 1.4, ConeQuality.Fair)]
    [InlineData(4.0, 2.0, ConeQuality.Poor)]
    public void QualityBands(double stdDev, double stdError, ConeQuality expected)
    {
        Assert.Equal(expected, ConeStatistics.Rate(stdDev, stdError));
    }

    [Fact]
    public void SmallConeWithinTheNoiseIsNotSignificant()
    {
        ConeSummary summary = ConeStatistics.Summarize(new List<double> { 0.3, -0.4, 0.5, -0.2, 0.1 });

        Assert.False(summary.Significant);
    }

    [Fact]
    public void MedianOfEvenAndOddCounts()
    {
        Assert.Equal(2.0, ConeStatistics.Median(new List<double> { 3.0, 1.0, 2.0 }));
        Assert.Equal(2.5, ConeStatistics.Median(new List<double> { 4.0, 1.0, 2.0, 3.0 }));
        Assert.True(double.IsNaN(ConeStatistics.Median(new List<double>())));
    }
}
