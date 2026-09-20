using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GuardCenter
{
    internal static class SliderMouseWheelBehavior
    {
        private static bool initialized;

        public static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(Slider_PreviewMouseWheel), true);
            initialized = true;
        }

        internal static double GetStep(double tickFrequency, double smallChange)
        {
            if (IsFinitePositive(tickFrequency))
            {
                return tickFrequency;
            }
            if (IsFinitePositive(smallChange))
            {
                return smallChange;
            }
            return 1;
        }

        internal static double CalculateNextValue(double value, double minimum, double maximum,
            double tickFrequency, double smallChange, int delta)
        {
            if (delta == 0 || !IsFinite(value) || !IsFinite(minimum) || !IsFinite(maximum)
                || maximum < minimum)
            {
                return value;
            }

            double step = GetStep(tickFrequency, smallChange);
            double detentRatio = Math.Abs((double)delta) / Mouse.MouseWheelDeltaForOneLine;
            int detents = Math.Max(1, (int)Math.Round(detentRatio, MidpointRounding.AwayFromZero));
            double direction = delta > 0 ? 1 : -1;
            double next = value + (direction * step * detents);
            next = Math.Max(minimum, Math.Min(maximum, next));

            // Keep common fractional steps stable instead of accumulating binary floating-point noise.
            return Math.Round(next, 12, MidpointRounding.AwayFromZero);
        }

        private static void Slider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var slider = sender as Slider;
            if (slider == null || e.Delta == 0 || !slider.IsEnabled || !slider.IsMouseOver)
            {
                return;
            }

            slider.Value = CalculateNextValue(slider.Value, slider.Minimum, slider.Maximum,
                slider.TickFrequency, slider.SmallChange, e.Delta);

            // Consume the wheel even at a boundary so the parent page does not suddenly scroll.
            e.Handled = true;
        }

        private static bool IsFinitePositive(double value)
        {
            return IsFinite(value) && value > 0;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
