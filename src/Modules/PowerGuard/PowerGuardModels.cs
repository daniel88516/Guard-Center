using System;

namespace GuardCenter
{
    internal enum PowerGuardDurationKind
    {
        UntilManual,
        ThirtyMinutes,
        OneHour,
        TwoHours,
        Custom
    }

    internal sealed class PowerGuardState
    {
        public PowerGuardState(bool isEnabled, bool keepDisplayOn,
            PowerGuardDurationKind durationKind, TimeSpan selectedDuration, TimeSpan customDuration,
            DateTimeOffset? endAtUtc, TimeSpan? remaining, string statusText, string lastError)
        {
            IsEnabled = isEnabled;
            KeepDisplayOn = keepDisplayOn;
            DurationKind = durationKind;
            SelectedDuration = selectedDuration;
            CustomDuration = customDuration;
            EndAtUtc = endAtUtc;
            Remaining = remaining;
            StatusText = statusText ?? string.Empty;
            LastError = lastError ?? string.Empty;
        }

        public bool IsEnabled { get; private set; }
        public bool KeepDisplayOn { get; private set; }
        public PowerGuardDurationKind DurationKind { get; private set; }
        public TimeSpan SelectedDuration { get; private set; }
        public TimeSpan CustomDuration { get; private set; }
        public DateTimeOffset? EndAtUtc { get; private set; }
        public TimeSpan? Remaining { get; private set; }
        public string StatusText { get; private set; }
        public string LastError { get; private set; }

        public bool HasTimer
        {
            get { return IsEnabled && DurationKind != PowerGuardDurationKind.UntilManual && EndAtUtc.HasValue; }
        }
    }

    internal static class PowerGuardDurations
    {
        public static readonly TimeSpan MinimumCustom = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan MaximumCustom = TimeSpan.FromDays(30);

        public static TimeSpan Get(PowerGuardDurationKind kind, TimeSpan customDuration)
        {
            if (kind == PowerGuardDurationKind.ThirtyMinutes)
            {
                return TimeSpan.FromMinutes(30);
            }
            if (kind == PowerGuardDurationKind.OneHour)
            {
                return TimeSpan.FromHours(1);
            }
            if (kind == PowerGuardDurationKind.TwoHours)
            {
                return TimeSpan.FromHours(2);
            }
            if (kind == PowerGuardDurationKind.Custom)
            {
                return ClampCustom(customDuration);
            }

            return TimeSpan.Zero;
        }

        public static TimeSpan ClampCustom(TimeSpan value)
        {
            if (value < MinimumCustom)
            {
                return MinimumCustom;
            }
            if (value > MaximumCustom)
            {
                return MaximumCustom;
            }
            return TimeSpan.FromMinutes(Math.Round(value.TotalMinutes));
        }

        public static TimeSpan ClampRemaining(TimeSpan value, TimeSpan maximum)
        {
            if (value <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }
            if (value >= maximum)
            {
                return maximum;
            }
            return value;
        }
    }
}
