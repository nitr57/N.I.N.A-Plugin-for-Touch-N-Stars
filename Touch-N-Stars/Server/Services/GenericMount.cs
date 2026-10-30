using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.INDI.Model;

namespace TouchNStars.Server.Services;

/// <summary>
/// Manual slewing of a mount that is neither pins' native OnStepX driver nor an INDI mount, e.g. an ASCOM Alpaca
/// mount: it is moved through NINA's telescope mediator at the driver's AxisRates. The chosen rate is kept in the
/// device's own Primary/SecondaryMovingRate, which NINA's pad uses too. Callers try the OnStepX and INDI mounts first.
/// </summary>
public static class GenericMount
{
    private static readonly object _lock = new object();
    private static string _rateSetForDeviceId;

    /// <summary>
    /// The mount NINA has connected when it is not pins' native OnStepX driver; null otherwise, or when
    /// <paramref name="device"/> names another mount.
    /// </summary>
    public static ITelescope Connected(string device = null)
    {
        var mediator = TouchNStars.Mediators?.Telescope;
        if (mediator?.GetInfo()?.Connected != true)
        {
            return null;
        }

        if (mediator.GetDevice() is not ITelescope mount || mount is OnStepXTelescope)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(device) && device != mount.Name && device != mount.DisplayName)
        {
            return null;
        }

        return mount;
    }

    /// <summary>
    /// Why the mount can't move <paramref name="axis"/> now, from the mediator's cached state; null when it can.
    /// NINA's MoveAxis only logs and notifies these, so the socket checks them first to answer with an error.
    /// </summary>
    public static string Refusal(TelescopeAxes axis)
    {
        var info = TouchNStars.Mediators?.Telescope?.GetInfo();
        if (info?.Connected != true)
        {
            return "No mount is currently connected";
        }

        if (info.AtPark)
        {
            return "The mount is parked";
        }

        if (axis == TelescopeAxes.Primary && !info.CanMovePrimaryAxis)
        {
            return "The mount cannot move its primary axis";
        }

        if (axis == TelescopeAxes.Secondary && !info.CanMoveSecondaryAxis)
        {
            return "The mount cannot move its secondary axis";
        }

        return null;
    }

    /// <summary>
    /// Moves <paramref name="axis"/> at the selected rate in the direction of <paramref name="sign"/>, or stops it at 0.
    /// The mediator applies the profile's axis reversal.
    /// </summary>
    public static void Move(ITelescope mount, TelescopeAxes axis, int sign)
    {
        var mediator = TouchNStars.Mediators.Telescope;
        if (sign == 0)
        {
            // a stop on a parked mount, or of an axis it can't move, would only raise one of NINA's warnings
            var info = mediator.GetInfo();
            bool canMove = axis == TelescopeAxes.Primary
                ? info?.CanMovePrimaryAxis == true
                : info?.CanMoveSecondaryAxis == true;
            if (info?.AtPark != true && canMove)
            {
                mediator.MoveAxis(axis, 0);
            }

            return;
        }

        double rate = axis == TelescopeAxes.Primary ? mount.PrimaryMovingRate : mount.SecondaryMovingRate;
        mediator.MoveAxis(axis, sign * rate);
    }

    /// <summary>
    /// The primary axis' AxisRates as a slew rate capability: one real range is continuous, anything else (fixed rates
    /// or ranges with gaps between them) is one discrete step per range at its maximum, so the slider never offers a
    /// rate the driver would snap away from. None when the driver reports no rates or can't move either axis.
    /// </summary>
    public static SlewRateCapability Capability(ITelescope mount)
    {
        var ranges = AxisRates(mount);
        if (ranges.Count == 0 || (!mount.CanMovePrimaryAxis && !mount.CanMoveSecondaryAxis))
        {
            return SlewRateCapability.None();
        }

        double? current = SelectedRate(mount);
        if (ranges.Count == 1 && ranges[0].Max > ranges[0].Min)
        {
            var (min, max) = ranges[0];
            return new SlewRateCapability
            {
                Kind = SlewRateKind.Continuous,
                PropertyName = "ASCOM_AXIS_RATES",
                Min = min,
                Max = max,
                Step = (max - min) / 200,
                Unit = "°/s",
                CurrentValue = current
            };
        }

        var steps = DiscreteRates(ranges);
        var options = new List<SlewRateOption>(steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            options.Add(new SlewRateOption
            {
                Index = i,
                Name = i.ToString(CultureInfo.InvariantCulture),
                Label = SiderealLabel(steps[i]),
                IsSelected = current.HasValue && Math.Abs(current.Value - steps[i]) < 1e-9,
                EstimatedRateDps = steps[i]
            });
        }

        return new SlewRateCapability { Kind = SlewRateKind.Discrete, PropertyName = "ASCOM_AXIS_RATES", Options = options };
    }

    /// <summary>
    /// Selects the rate of discrete step <paramref name="index"/>; false when the mount's rates are not discrete or the
    /// index is out of range.
    /// </summary>
    public static bool TrySelectIndex(ITelescope mount, int index, out string error)
    {
        var capability = Capability(mount);
        if (capability.Kind != SlewRateKind.Discrete)
        {
            error = "This mount does not use discrete slew rates; send 'value' instead";
            return false;
        }

        if (index < 0 || index >= capability.Options.Count)
        {
            error = $"Slew rate index must be 0-{capability.Options.Count - 1}";
            return false;
        }

        SelectRate(mount, capability.Options[index].EstimatedRateDps.Value);
        error = null;
        return true;
    }

    /// <summary>Selects a rate in °/s; false when the mount's rate is not continuous.</summary>
    public static bool TrySelectValue(ITelescope mount, double value, out string error)
    {
        var capability = Capability(mount);
        if (capability.Kind != SlewRateKind.Continuous)
        {
            error = "This mount does not use a continuous slew rate; send 'index' instead";
            return false;
        }

        SelectRate(mount, Math.Clamp(value, capability.Min, capability.Max));
        error = null;
        return true;
    }

    private static void SelectRate(ITelescope mount, double rate)
    {
        lock (_lock)
        {
            // AscomTelescope snaps a new rate relative to the previous one and jumps to the maximum while that is
            // still unset, so read each rate once first; an axis that can't move may have no AxisRates at all
            if (mount.CanMovePrimaryAxis)
            {
                _ = mount.PrimaryMovingRate;
                mount.PrimaryMovingRate = rate;
            }

            if (mount.CanMoveSecondaryAxis)
            {
                _ = mount.SecondaryMovingRate;
                mount.SecondaryMovingRate = rate;
            }

            _rateSetForDeviceId = mount.Id;
        }
    }

    /// <summary>
    /// The rate a client selected for this mount, or null before one did, so the client restores its own stored
    /// choice instead of the driver's default (the maximum).
    /// </summary>
    private static double? SelectedRate(ITelescope mount)
    {
        lock (_lock)
        {
            if (_rateSetForDeviceId != mount.Id)
            {
                return null;
            }
        }

        return mount.CanMovePrimaryAxis ? mount.PrimaryMovingRate : mount.SecondaryMovingRate;
    }

    /// <summary>
    /// The live AxisRates of the first axis that can move, falling back to the snapshot NINA took at connect; ordered
    /// by rate.
    /// </summary>
    private static List<(double Min, double Max)> AxisRates(ITelescope mount)
    {
        var axis = mount.CanMovePrimaryAxis || !mount.CanMoveSecondaryAxis ? TelescopeAxes.Primary : TelescopeAxes.Secondary;
        IList<(double, double)> rates = mount.GetAxisRates(axis);
        if (rates == null || rates.Count == 0)
        {
            var info = TouchNStars.Mediators.Telescope.GetInfo();
            rates = axis == TelescopeAxes.Primary ? info?.PrimaryAxisRates : info?.SecondaryAxisRates;
        }

        return (rates ?? Array.Empty<(double, double)>())
            .Select(r => (Min: Math.Min(r.Item1, r.Item2), Max: Math.Max(r.Item1, r.Item2)))
            .Where(r => r.Max > 0)
            .OrderBy(r => r.Max)
            .ToList();
    }

    private static List<double> DiscreteRates(List<(double Min, double Max)> ranges)
    {
        var rates = new List<double>(ranges.Count);
        foreach (var (_, max) in ranges)
        {
            if (rates.Count == 0 || Math.Abs(rates[^1] - max) > 1e-9)
            {
                rates.Add(max);
            }
        }

        return rates;
    }

    /// <summary>A rate in °/s as a multiple of the sidereal rate, the way mounts label their fixed rates.</summary>
    private static string SiderealLabel(double degreesPerSecond)
    {
        double multiple = degreesPerSecond * 3600 / AstroUtil.SIDEREAL_RATE_ARCSECONDS_PER_SECOND;
        string format = multiple < 10 ? "0.##" : "0";
        return multiple.ToString(format, CultureInfo.InvariantCulture) + "×";
    }
}
