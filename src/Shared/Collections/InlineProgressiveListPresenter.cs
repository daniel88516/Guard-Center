using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UiControls = Wpf.Ui.Controls;

namespace GuardCenter
{
    internal sealed class InlineProgressiveListPresenter<T> : IDisposable
    {
        private const double NearViewportDistance = 140;
        private readonly ProgressiveListState<T> state;
        private readonly Func<T, UIElement> createItem;
        private readonly Action<int, int> countChanged;
        private readonly Action<IList<T>> itemsMaterialized;
        private readonly ScrollViewer pageScrollViewer;
        private readonly ObservableCollection<UIElement> visualItems = new ObservableCollection<UIElement>();
        private readonly StackPanel root;
        private readonly ItemsControl itemsControl;
        private readonly UiControls.ProgressRing progressRing;
        private bool disposed;

        public InlineProgressiveListPresenter(ProgressiveListState<T> state,
            Func<T, UIElement> createItem, Action<int, int> countChanged,
            ScrollViewer pageScrollViewer, Action<IList<T>> itemsMaterialized = null)
        {
            this.state = state;
            this.createItem = createItem;
            this.countChanged = countChanged;
            this.pageScrollViewer = pageScrollViewer;
            this.itemsMaterialized = itemsMaterialized;

            root = new StackPanel();
            itemsControl = new ItemsControl
            {
                ItemsSource = visualItems,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            root.Children.Add(itemsControl);

            progressRing = new UiControls.ProgressRing
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 6, 0, 6),
                IsIndeterminate = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            root.Children.Add(progressRing);

            if (pageScrollViewer != null)
            {
                pageScrollViewer.ScrollChanged += PageScrollViewer_ScrollChanged;
            }
        }

        public FrameworkElement Element
        {
            get { return root; }
        }

        public void Reset(IList<T> source, Func<T, string, bool> matches,
            Comparison<T> comparison)
        {
            if (disposed)
            {
                return;
            }

            state.Reset(source, matches, comparison);
            visualItems.Clear();
            List<T> visible = state.GetVisibleItems();
            AddVisuals(visible);
            NotifyItemsMaterialized(visible);
            progressRing.Visibility = Visibility.Collapsed;
            NotifyCountChanged();
        }

        public bool EnsureVisible(Predicate<T> predicate, Predicate<UIElement> visualPredicate)
        {
            if (disposed || !state.EnsureVisible(predicate))
            {
                return false;
            }

            List<T> desired = state.GetVisibleItems();
            var added = new List<T>();
            for (int i = visualItems.Count; i < desired.Count; i++)
            {
                UIElement visual = createItem(desired[i]);
                if (visual != null)
                {
                    visualItems.Add(visual);
                    added.Add(desired[i]);
                }
            }
            NotifyItemsMaterialized(added);

            NotifyCountChanged();
            if (visualPredicate != null)
            {
                for (int i = 0; i < visualItems.Count; i++)
                {
                    if (!visualPredicate(visualItems[i]))
                    {
                        continue;
                    }

                    FrameworkElement target = visualItems[i] as FrameworkElement;
                    if (target != null)
                    {
                        root.Dispatcher.BeginInvoke(new Action(target.BringIntoView),
                            DispatcherPriority.Loaded);
                    }
                    break;
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
            if (pageScrollViewer != null)
            {
                pageScrollViewer.ScrollChanged -= PageScrollViewer_ScrollChanged;
            }
        }

        private void PageScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (disposed || e.VerticalChange <= 0)
            {
                return;
            }
            EnsureNextBatchIsNearViewport();
        }

        private void EnsureNextBatchIsNearViewport()
        {
            if (disposed || pageScrollViewer == null || !root.IsLoaded
                || !state.HasMore || state.IsLoading || pageScrollViewer.ViewportHeight <= 0)
            {
                return;
            }

            Point bottom = itemsControl.TranslatePoint(
                new Point(0, itemsControl.ActualHeight), pageScrollViewer);
            if (bottom.Y >= -NearViewportDistance
                && bottom.Y <= pageScrollViewer.ViewportHeight + NearViewportDistance)
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

            progressRing.Visibility = Visibility.Visible;
            await root.Dispatcher.InvokeAsync(delegate { }, DispatcherPriority.Background);
            if (disposed)
            {
                return;
            }

            List<T> completed = state.CompleteBatch(batch);
            AddVisuals(completed);
            NotifyItemsMaterialized(completed);
            progressRing.Visibility = Visibility.Collapsed;
            NotifyCountChanged();
        }

        private void AddVisuals(IList<T> items)
        {
            if (items == null)
            {
                return;
            }
            for (int i = 0; i < items.Count; i++)
            {
                UIElement visual = createItem(items[i]);
                if (visual != null)
                {
                    visualItems.Add(visual);
                }
            }
        }

        private void NotifyCountChanged()
        {
            if (countChanged != null)
            {
                countChanged(state.VisibleCount, state.TotalItems);
            }
        }

        private void NotifyItemsMaterialized(IList<T> items)
        {
            if (itemsMaterialized != null && items != null && items.Count > 0)
            {
                itemsMaterialized(items);
            }
        }
    }
}
