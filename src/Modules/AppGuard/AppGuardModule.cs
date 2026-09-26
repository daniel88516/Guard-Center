using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GuardCenter
{
    internal sealed class AppGuardModule : IDisposable
    {
        private readonly AppCatalogService catalogService;
        private readonly AppActionService actionService;
        private readonly object stateLock = new object();
        private readonly List<AppCatalogItem> apps = new List<AppCatalogItem>();
        private readonly Dictionary<string, AppCatalogItem> importedApps =
            new Dictionary<string, AppCatalogItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AppCatalogDetail> detailCache =
            new Dictionary<string, AppCatalogDetail>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<AppCatalogItem> detailPrefetchQueue = new Queue<AppCatalogItem>();
        private readonly HashSet<string> detailPrefetchIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource refreshCts;
        private CancellationTokenSource detailCts;
        private int detailPrefetchGeneration;
        private bool detailPrefetchWorkerRunning;
        private bool loading;
        private bool disposed;
        private string lastStatus = "App Guard ready.";
        private string lastError = string.Empty;

        public event EventHandler StatusChanged;
        public event EventHandler AppsChanged;

        public AppGuardModule()
            : this(new AppCatalogService(), new AppActionService())
        {
        }

        public AppGuardModule(AppCatalogService catalogService, AppActionService actionService)
        {
            this.catalogService = catalogService;
            this.actionService = actionService;
        }

        public string StatusText
        {
            get
            {
                lock (stateLock)
                {
                    return lastStatus;
                }
            }
        }

        public bool IsLoading
        {
            get
            {
                lock (stateLock)
                {
                    return loading;
                }
            }
        }

        public string LastError
        {
            get
            {
                lock (stateLock)
                {
                    return lastError;
                }
            }
        }

        public void Start()
        {
            RefreshAsync();
        }

        public void RefreshAsync()
        {
            if (disposed)
            {
                return;
            }

            CancellationTokenSource old;
            lock (stateLock)
            {
                old = refreshCts;
                refreshCts = new CancellationTokenSource();
                CancelDetailPrefetchNoLock();
                loading = true;
                lastError = string.Empty;
                lastStatus = "Loading application catalog.";
            }
            if (old != null)
            {
                old.Cancel();
                old.Dispose();
            }
            RaiseStatusChanged();

            CancellationTokenSource current = refreshCts;
            Task.Run(delegate
            {
                try
                {
                    List<AppCatalogItem> loaded = catalogService.GetInstalledApps();
                    if (current.IsCancellationRequested || disposed)
                    {
                        return;
                    }

                    lock (stateLock)
                    {
                        MergeImportedAppsNoLock(loaded);
                        apps.Clear();
                        apps.AddRange(loaded);
                        detailCache.Clear();
                        loading = false;
                        lastStatus = "App Guard found " + loaded.Count + " app" + (loaded.Count == 1 ? "." : "s.");
                    }
                    RaiseAppsChanged();
                    RaiseStatusChanged();
                }
                catch (Exception ex)
                {
                    if (current.IsCancellationRequested || disposed)
                    {
                        return;
                    }

                    lock (stateLock)
                    {
                        loading = false;
                        lastError = ex.Message;
                        lastStatus = "App Guard catalog failed: " + ex.Message;
                    }
                    RaiseAppsChanged();
                    RaiseStatusChanged();
                }
            });
        }

        public List<AppCatalogItem> GetApps()
        {
            lock (stateLock)
            {
                var result = new List<AppCatalogItem>();
                for (int i = 0; i < apps.Count; i++)
                {
                    result.Add(apps[i].Clone());
                }

                return result;
            }
        }

        public AppCatalogItem FindOrImport(string targetPath)
        {
            lock (stateLock)
            {
                AppCatalogItem existing = FindByPathNoLock(targetPath);
                if (existing != null)
                {
                    return existing.Clone();
                }
            }

            AppCatalogItem imported = catalogService.FindOrImport(null, targetPath);
            if (imported == null)
            {
                return null;
            }

            lock (stateLock)
            {
                AppCatalogItem existing = FindByIdNoLock(imported.Id);
                if (existing != null)
                {
                    return existing.Clone();
                }

                imported.OriginalOrder = apps.Count;
                apps.Add(imported);
                importedApps[imported.Id] = imported.Clone();
                lastStatus = "Imported " + imported.Name + ".";
            }
            RaiseAppsChanged();
            RaiseStatusChanged();
            return imported.Clone();
        }

        public AppCatalogItem FindById(string id)
        {
            lock (stateLock)
            {
                AppCatalogItem item = FindByIdNoLock(id);
                return item == null ? null : item.Clone();
            }
        }

        public bool TryGetDetail(string appId, out AppCatalogDetail detail)
        {
            lock (stateLock)
            {
                if (string.IsNullOrWhiteSpace(appId))
                {
                    detail = null;
                    return false;
                }
                return detailCache.TryGetValue(appId, out detail);
            }
        }

        public void LoadDetailAsync(AppCatalogItem app, Action<AppCatalogDetail> callback)
        {
            LoadDetailAsync(app, callback, false);
        }

        public void LoadDetailAsync(AppCatalogItem app, Action<AppCatalogDetail> callback,
            bool forceRefresh)
        {
            if (app == null || callback == null || disposed)
            {
                return;
            }

            AppCatalogDetail cached = null;
            lock (stateLock)
            {
                if (!forceRefresh && detailCache.TryGetValue(app.Id, out cached))
                {
                }
                else
                {
                    if (detailCts != null)
                    {
                        detailCts.Cancel();
                        detailCts.Dispose();
                    }
                    detailCts = new CancellationTokenSource();
                }
            }

            if (cached != null)
            {
                callback(cached);
                return;
            }

            CancellationTokenSource current = detailCts;
            Task.Run(delegate
            {
                AppCatalogDetail detail;
                try
                {
                    detail = actionService.LoadDetail(app);
                }
                catch (Exception ex)
                {
                    detail = CreateDetailError(app, ex);
                }
                if (disposed || current.IsCancellationRequested)
                {
                    return;
                }

                lock (stateLock)
                {
                    detailCache[app.Id] = detail;
                }

                callback(detail);
            });
        }

        public void PrefetchDetails(IList<AppCatalogItem> batch)
        {
            if (batch == null || batch.Count == 0 || disposed)
            {
                return;
            }

            int generation;
            bool startWorker = false;
            lock (stateLock)
            {
                if (disposed)
                {
                    return;
                }
                for (int i = 0; i < batch.Count; i++)
                {
                    AppCatalogItem app = batch[i];
                    if (app == null || string.IsNullOrWhiteSpace(app.Id)
                        || detailCache.ContainsKey(app.Id) || detailPrefetchIds.Contains(app.Id))
                    {
                        continue;
                    }

                    detailPrefetchQueue.Enqueue(app.Clone());
                    detailPrefetchIds.Add(app.Id);
                }

                generation = detailPrefetchGeneration;
                if (detailPrefetchQueue.Count > 0 && !detailPrefetchWorkerRunning)
                {
                    detailPrefetchWorkerRunning = true;
                    startWorker = true;
                }
            }

            if (startWorker)
            {
                Task.Run(delegate { RunDetailPrefetchWorker(generation); });
            }
        }

        public void InvalidateDetail(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                return;
            }
            lock (stateLock)
            {
                detailCache.Remove(appId);
            }
        }

        public void CancelDetailLoad()
        {
            lock (stateLock)
            {
                if (detailCts != null)
                {
                    detailCts.Cancel();
                    detailCts.Dispose();
                    detailCts = null;
                }
            }
        }

        public AppActionResult Execute(AppCatalogItem app, AppActionType action)
        {
            AppActionResult result = actionService.Execute(app, action);
            lock (stateLock)
            {
                lastStatus = result.Message;
                if (app != null)
                {
                    detailCache.Remove(app.Id);
                }
            }
            RaiseStatusChanged();
            return result;
        }

        public AppActionResult CopyPathToClipboard(AppCatalogItem app)
        {
            if (app == null || string.IsNullOrWhiteSpace(app.TargetPath))
            {
                return AppActionResult.Fail("No application path is available.");
            }

            System.Windows.Clipboard.SetText(app.TargetPath);
            lock (stateLock)
            {
                lastStatus = "Copied path for " + app.Name + ".";
            }
            RaiseStatusChanged();
            return AppActionResult.Ok("Copied path.");
        }

        public void Dispose()
        {
            disposed = true;
            CancelDetailLoad();
            lock (stateLock)
            {
                CancelDetailPrefetchNoLock();
                if (refreshCts != null)
                {
                    refreshCts.Cancel();
                    refreshCts.Dispose();
                    refreshCts = null;
                }
            }
        }

        private void RunDetailPrefetchWorker(int generation)
        {
            while (true)
            {
                var batch = new List<AppCatalogItem>();
                lock (stateLock)
                {
                    if (disposed || generation != detailPrefetchGeneration)
                    {
                        return;
                    }

                    while (batch.Count < 100 && detailPrefetchQueue.Count > 0)
                    {
                        batch.Add(detailPrefetchQueue.Dequeue());
                    }
                    if (batch.Count == 0)
                    {
                        detailPrefetchWorkerRunning = false;
                        return;
                    }
                }

                List<AppCatalogDetail> details;
                try
                {
                    details = actionService.LoadDetails(batch);
                }
                catch (Exception ex)
                {
                    details = new List<AppCatalogDetail>();
                    for (int i = 0; i < batch.Count; i++)
                    {
                        details.Add(CreateDetailError(batch[i], ex));
                    }
                }

                lock (stateLock)
                {
                    if (disposed || generation != detailPrefetchGeneration)
                    {
                        return;
                    }

                    for (int i = 0; i < details.Count; i++)
                    {
                        AppCatalogDetail detail = details[i];
                        if (detail == null || detail.App == null
                            || string.IsNullOrWhiteSpace(detail.App.Id))
                        {
                            continue;
                        }
                        detailCache[detail.App.Id] = detail;
                    }
                    for (int i = 0; i < batch.Count; i++)
                    {
                        if (batch[i] != null)
                        {
                            detailPrefetchIds.Remove(batch[i].Id);
                        }
                    }
                }
            }
        }

        private void CancelDetailPrefetchNoLock()
        {
            detailPrefetchGeneration = unchecked(detailPrefetchGeneration + 1);
            detailPrefetchQueue.Clear();
            detailPrefetchIds.Clear();
            detailPrefetchWorkerRunning = false;
        }

        private static AppCatalogDetail CreateDetailError(AppCatalogItem app, Exception ex)
        {
            return new AppCatalogDetail
            {
                App = app,
                Loaded = true,
                Error = ex == null ? "Unable to load application details." : ex.Message
            };
        }

        private AppCatalogItem FindByPathNoLock(string targetPath)
        {
            string id = AppIdentityService.CreateExecutableId(targetPath);
            return FindByIdNoLock(id);
        }

        private AppCatalogItem FindByIdNoLock(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            for (int i = 0; i < apps.Count; i++)
            {
                if (string.Equals(apps[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return apps[i];
                }
            }

            return null;
        }

        private void MergeImportedAppsNoLock(List<AppCatalogItem> loaded)
        {
            if (loaded == null || importedApps.Count == 0)
            {
                return;
            }

            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < loaded.Count; i++)
            {
                existingIds.Add(loaded[i].Id);
            }

            foreach (AppCatalogItem imported in importedApps.Values)
            {
                if (imported == null || existingIds.Contains(imported.Id))
                {
                    continue;
                }

                AppCatalogItem clone = imported.Clone();
                clone.OriginalOrder = loaded.Count;
                loaded.Add(clone);
                existingIds.Add(clone.Id);
            }
        }

        private void RaiseStatusChanged()
        {
            EventHandler handler = StatusChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void RaiseAppsChanged()
        {
            EventHandler handler = AppsChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
