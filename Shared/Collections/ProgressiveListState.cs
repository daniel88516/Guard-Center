using System;
using System.Collections.Generic;

namespace GuardCenter
{
    internal sealed class ListPresentationSettings
    {
        public int BatchSize = 20;
        public string SortKey = "name";
        public bool SortDescending;
    }

    internal sealed class ProgressiveListState<T>
    {
        private readonly ListPresentationSettings settings;
        private readonly int[] allowedBatchSizes;
        private readonly Func<T, string> identitySelector;
        private readonly List<T> allItems = new List<T>();
        private readonly List<T> visibleItems = new List<T>();
        private readonly HashSet<string> visibleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int generation;
        private int consumedCount;
        private bool loading;

        public ProgressiveListState(ListPresentationSettings settings, Func<T, string> identitySelector)
            : this(settings, identitySelector, new int[] { 10, 20, 50, 100 })
        {
        }

        public ProgressiveListState(ListPresentationSettings settings, Func<T, string> identitySelector,
            int[] allowedBatchSizes)
        {
            this.settings = settings ?? new ListPresentationSettings();
            this.identitySelector = identitySelector;
            this.allowedBatchSizes = allowedBatchSizes == null || allowedBatchSizes.Length == 0
                ? new int[] { 10, 20, 50, 100 }
                : (int[])allowedBatchSizes.Clone();
            this.settings.BatchSize = NormalizeBatchSize(this.settings.BatchSize);
        }

        public string SearchText = string.Empty;

        public int BatchSize
        {
            get { return settings.BatchSize; }
            set { settings.BatchSize = NormalizeBatchSize(value); }
        }

        public string SortKey
        {
            get { return string.IsNullOrWhiteSpace(settings.SortKey) ? "name" : settings.SortKey; }
            set { settings.SortKey = string.IsNullOrWhiteSpace(value) ? "name" : value; }
        }

        public bool SortDescending
        {
            get { return settings.SortDescending; }
            set { settings.SortDescending = value; }
        }

        public int[] AllowedBatchSizes
        {
            get { return (int[])allowedBatchSizes.Clone(); }
        }

        public int TotalItems
        {
            get { return allItems.Count; }
        }

        public int VisibleCount
        {
            get { return visibleItems.Count; }
        }

        public bool HasMore
        {
            get { return consumedCount < allItems.Count; }
        }

        public bool IsLoading
        {
            get { return loading; }
        }

        public void Reset(IList<T> source, Func<T, string, bool> matches, Comparison<T> comparison)
        {
            generation++;
            loading = false;
            allItems.Clear();
            visibleItems.Clear();
            visibleIds.Clear();
            consumedCount = 0;

            var filtered = new List<IndexedItem>();
            string query = SearchText == null ? string.Empty : SearchText.Trim();
            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    T item = source[i];
                    if (matches == null || matches(item, query))
                    {
                        filtered.Add(new IndexedItem(item, i));
                    }
                }
            }

            if (comparison != null && !string.Equals(SortKey, "original", StringComparison.OrdinalIgnoreCase))
            {
                filtered.Sort(delegate(IndexedItem left, IndexedItem right)
                {
                    int value = comparison(left.Item, right.Item);
                    if (value == 0)
                    {
                        value = left.Index.CompareTo(right.Index);
                    }
                    return SortDescending ? -value : value;
                });
            }

            var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < filtered.Count; i++)
            {
                string identity = GetIdentity(filtered[i].Item, filtered[i].Index);
                if (sourceIds.Add(identity))
                {
                    allItems.Add(filtered[i].Item);
                }
            }
            AddRange(0, Math.Min(BatchSize, allItems.Count));
        }

        public List<T> GetVisibleItems()
        {
            return new List<T>(visibleItems);
        }

        public bool TryBeginNextBatch(out ProgressiveBatch<T> batch)
        {
            batch = null;
            if (loading || !HasMore)
            {
                return false;
            }

            loading = true;
            int start = consumedCount;
            int count = Math.Min(BatchSize, allItems.Count - start);
            var items = new List<T>();
            for (int i = 0; i < count; i++)
            {
                items.Add(allItems[start + i]);
            }
            batch = new ProgressiveBatch<T>(generation, start, items);
            return true;
        }

        public List<T> CompleteBatch(ProgressiveBatch<T> batch)
        {
            var added = new List<T>();
            if (batch == null || batch.Generation != generation)
            {
                return added;
            }

            loading = false;
            consumedCount = Math.Max(consumedCount, batch.StartIndex + batch.Items.Count);
            for (int i = 0; i < batch.Items.Count; i++)
            {
                T item = batch.Items[i];
                if (TryAddVisible(item, batch.StartIndex + i))
                {
                    added.Add(item);
                }
            }
            return added;
        }

        public void CancelPendingBatch()
        {
            generation++;
            loading = false;
        }

        public void ClearSearch()
        {
            SearchText = string.Empty;
            CancelPendingBatch();
        }

        public bool EnsureVisible(Predicate<T> predicate)
        {
            if (predicate == null)
            {
                return false;
            }
            for (int i = 0; i < allItems.Count; i++)
            {
                if (!predicate(allItems[i]))
                {
                    continue;
                }
                int required = Math.Min(allItems.Count,
                    ((i / Math.Max(1, BatchSize)) + 1) * Math.Max(1, BatchSize));
                AddRange(consumedCount, required - consumedCount);
                return true;
            }
            return false;
        }

        private void AddRange(int start, int count)
        {
            for (int i = 0; i < count && start + i < allItems.Count; i++)
            {
                TryAddVisible(allItems[start + i], start + i);
            }
            consumedCount = Math.Max(consumedCount, Math.Min(allItems.Count, start + count));
        }

        private bool TryAddVisible(T item, int sourceIndex)
        {
            string identity = GetIdentity(item, sourceIndex);
            if (!visibleIds.Add(identity))
            {
                return false;
            }
            visibleItems.Add(item);
            return true;
        }

        private string GetIdentity(T item, int sourceIndex)
        {
            string identity = identitySelector == null ? "#" + sourceIndex : identitySelector(item);
            return string.IsNullOrWhiteSpace(identity) ? "#" + sourceIndex : identity;
        }

        private bool IsAllowedBatchSize(int value)
        {
            for (int i = 0; i < allowedBatchSizes.Length; i++)
            {
                if (allowedBatchSizes[i] == value)
                {
                    return true;
                }
            }
            return false;
        }

        private int NormalizeBatchSize(int value)
        {
            return IsAllowedBatchSize(value) ? value : allowedBatchSizes[0];
        }

        private sealed class IndexedItem
        {
            public readonly T Item;
            public readonly int Index;

            public IndexedItem(T item, int index)
            {
                Item = item;
                Index = index;
            }
        }
    }

    internal sealed class ProgressiveBatch<T>
    {
        public readonly int Generation;
        public readonly int StartIndex;
        public readonly List<T> Items;

        public ProgressiveBatch(int generation, int startIndex, List<T> items)
        {
            Generation = generation;
            StartIndex = startIndex;
            Items = items ?? new List<T>();
        }
    }
}
