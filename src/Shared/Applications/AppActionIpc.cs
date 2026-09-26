using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GuardCenter
{
    internal sealed class AppActionIpcServer : IDisposable
    {
        public const string PipeName = "GuardCenter.AppAction";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        private readonly Action<AppActionRequest> onRequest;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private Task serverTask;
        private bool disposed;

        public AppActionIpcServer(Action<AppActionRequest> onRequest)
        {
            this.onRequest = onRequest;
        }

        public void Start()
        {
            serverTask = Task.Run((Action)RunLoop);
        }

        public void Dispose()
        {
            disposed = true;
            cts.Cancel();
            try
            {
                if (serverTask != null)
                {
                    serverTask.Wait(500);
                }
            }
            catch
            {
            }
            cts.Dispose();
        }

        private void RunLoop()
        {
            while (!disposed && !cts.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        pipe.WaitForConnectionAsync(cts.Token).GetAwaiter().GetResult();
                        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                        {
                            writer.AutoFlush = true;
                            string payload = reader.ReadLine();
                            AppActionRequest request = JsonSerializer.Deserialize<AppActionRequest>(payload ?? string.Empty, JsonOptions);
                            if (request != null && IsAllowed(request))
                            {
                                onRequest(request);
                                writer.WriteLine("OK");
                            }
                            else
                            {
                                writer.WriteLine("REJECTED");
                            }
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    if (disposed)
                    {
                        return;
                    }
                }
            }
        }

        internal static bool IsAllowed(AppActionRequest request)
        {
            if (request == null)
            {
                return false;
            }

            return request.Action == AppActionType.Manage
                && !string.IsNullOrWhiteSpace(request.TargetPath);
        }
    }

    internal static class AppActionIpcClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        public static bool TrySend(AppActionRequest request, int timeoutMs, out string error)
        {
            error = string.Empty;
            try
            {
                using (var pipe = new NamedPipeClientStream(".", AppActionIpcServer.PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMs);
                    using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                    {
                        writer.AutoFlush = true;
                        writer.WriteLine(JsonSerializer.Serialize(request, JsonOptions));
                        string response = reader.ReadLine();
                        if (string.Equals(response, "OK", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        error = response ?? "No response.";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
