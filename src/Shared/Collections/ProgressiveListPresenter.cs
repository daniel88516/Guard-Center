using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UiControls = Wpf.Ui.Controls;

namespace GuardCenter
{
    internal sealed class ProgressiveListPresenter<T> : IDisposable
    {
        private const double NearBottomDistance = 120;
        private readonly ProgressiveListState<T> state;
        private readonly Func<T, UIElement> createItem;
        private readonly Action<int, int> countChanged;
        private readonly bool autoFillViewport;
        private readonly ObservableCollection<UIElement> visualItems = new ObservableCollection<UIElement>();
        private readonly ListBox listBox;
        private readonly UiControls.ProgressRing progressRing;
        private readonly Grid root;
        private readonly ScrollChangedEventHandler scrollChangedHandler;
        private bool waitForScrollAway;
        private bool disposed;

        public ProgressiveListPresenter(ProgressiveListState<T> state, Func<T, UIElement> createItem,
            Action<int, int> countChanged, bool autoFillViewport = true)
        {
            this.state = state;
            this.createItem = createItem;
            this.countChanged = countChanged;
            this.autoFillViewport = autoFillViewport;

            root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

            listBox = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemsSource = visualItems
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(listBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetCanContentScroll(listBox, true);
            VirtualizingStackPanel.SetIsVirtualizing(listBox, true);
            VirtualizingStackPanel.SetVirtualizationMode(listBox, VirtualizationMode.Recycling);
            listBox.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);

            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            itemStyle.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
            listBox.ItemContainerStyle = itemStyle;
            scrollChangedHandler = OnScrollChanged;
            listBox.AddHandler(ScrollViewer.ScrollChangedEvent, scrollChangedHandler);
            listBox.Loaded += OnLoaded;
            Grid.SetRow(listBox, 0);
            root.Children.Add(listBox);

            progressRing = new UiControls.ProgressRing
            {
                Width = 18,
                Height = 18,
                IsIndeterminate = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false
            };
            Grid.SetRow(progressRing, 1);
            root.Children.Add(progressRing);
        }

        public FrameworkElement Element
        {
            get { return root; }
        }

        public ListBox ListBox
        {
            get { return listBox; }
        }

        public double VerticalOffset
        {
            get
            {
                ScrollViewer viewer = FindDescendant<ScrollViewer>(listBox);
                return viewer == null ? 0 : viewer.VerticalOffset;
            }
        }

        public void RestoreVerticalOffset(double offset)
        {
            listBox.Dispatcher.BeginInvoke(new Action(delegate
            {
                ScrollViewer viewer = FindDescendant<ScrollViewer>(listBox);
                if (viewer != null)
                {
                    viewer.ScrollToVerticalOffset(Math.Max(0, offset));
                }
            }), DispatcherPriority.Loaded);
        }

        public void LoadMore()
        {
            LoadMoreAsync();
        }

        public bool HasMore
        {
            get { return state.HasMore; }
        }

        public void Reset(IList<T> source, Func<T, string, bool> matches, Comparison<T> comparison,
            bool scrollToTop)
        {
            if (disposed)
            {
                return;
            }

            state.Reset(source, matches, comparison);
            visualItems.Clear();
            List<T> first = state.GetVisibleItems();
            for (int i = 0; i < first.Count; i++)
            {
                UIElement visual = createItem(first[i]);
                if (visual != null)
                {
                    visualItems.Add(visual);
                }
            }
            progressRing.Opacity = 0;
            waitForScrollAway = false;
            NotifyCountChanged();
            if (scrollToTop && visualItems.Count > 0)
            {
                listBox.ScrollIntoView(visualItems[0]);
            }
            if (autoFillViewport)
                listBox.Dispatcher.BeginInvoke(new Action(EnsureViewportFilled), DispatcherPriority.Background);
        }

        public bool EnsureVisible(Predicate<T> predicate, Predicate<UIElement> visualPredicate)
        {
            if (!state.EnsureVisible(predicate))
            {
                return false;
            }

            List<T> desired = state.GetVisibleItems();
            for (int i = visualItems.Count; i < desired.Count; i++)
            {
                UIElement visual = createItem(desired[i]);
                if (visual != null)
                {
                    visualItems.Add(visual);
                }
            }
            NotifyCountChanged();
            if (visualPredicate != null)
            {
                for (int i = 0; i < visualItems.Count; i++)
                {
                    if (visualPredicate(visualItems[i]))
                    {
                        listBox.ScrollIntoView(visualItems[i]);
                        break;
                    }
                }
            }
            return true;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            state.CancelPendingBatch();
            listBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, scrollChangedHandler);
            listBox.Loaded -= OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureViewportFilled();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (disposed || e.ExtentHeight <= 0)
            {
                return;
            }

            double remaining = e.ExtentHeight - e.ViewportHeight - e.VerticalOffset;
            if (remaining > NearBottomDistance)
            {
                waitForScrollAway = false;
                return;
            }

            if (!waitForScrollAway && e.VerticalChange > 0)
            {
                LoadMoreAsync();
            }
        }

        private async void LoadMoreAsync()
        {
            ProgressiveBatch<T> batch;
            if (disposed || !state.TryBeginNextBatch(out batch))
            {
                return;
            }

            progressRing.Opacity = 1;
            await root.Dispatcher.InvokeAsync(delegate { }, DispatcherPriority.Background);
            if (disposed)
            {
                return;
            }

            List<T> added = state.CompleteBatch(batch);
            waitForScrollAway = true;
            for (int i = 0; i < added.Count; i++)
            {
                UIElement visual = createItem(added[i]);
                if (visual != null)
                {
                    visualItems.Add(visual);
                }
            }
            progressRing.Opacity = 0;
            NotifyCountChanged();
            if (autoFillViewport)
                await root.Dispatcher.InvokeAsync(EnsureViewportFilled, DispatcherPriority.Background);
        }

        private void EnsureViewportFilled()
        {
            if (!autoFillViewport || disposed || !state.HasMore || state.IsLoading)
            {
                return;
            }

            ScrollViewer viewer = FindDescendant<ScrollViewer>(listBox);
            if (viewer == null || viewer.ViewportHeight <= 0)
            {
                return;
            }
            if (viewer.ExtentHeight <= viewer.ViewportHeight + 1)
            {
                LoadMoreAsync();
            }
        }

        private void NotifyCountChanged()
        {
            if (countChanged != null)
            {
                countChanged(state.VisibleCount, state.TotalItems);
            }
        }

        private static TElement FindDescendant<TElement>(DependencyObject rootObject)
            where TElement : DependencyObject
        {
            if (rootObject == null)
            {
                return null;
            }
            int count = VisualTreeHelper.GetChildrenCount(rootObject);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(rootObject, i);
                TElement match = child as TElement;
                if (match != null)
                {
                    return match;
                }
                match = FindDescendant<TElement>(child);
                if (match != null)
                {
                    return match;
                }
            }
            return null;
        }
    }
}
