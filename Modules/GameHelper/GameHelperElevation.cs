using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GuardCenter
{
    internal sealed class GameHelperElevationClient : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private NamedPipeServerStream pipe;
        private StreamReader reader;
        private StreamWriter writer;
        private string token;
        private bool elevationCancelled;
        private bool disposed;

        public bool IsConnected
        {
            get { return pipe != null && pipe.IsConnected; }
        }

        public bool ElevationCancelled
        {
            get { return elevationCancelled; }
        }

        public void ResetCancellation()
        {
            elevationCancelled = false;
        }

        public async Task<GameHelperElevationResponse> PostAsync(GameHelperElevationRequest request,
            bool allowStart, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (disposed)
                {
                    return GameHelperElevationResponse.Fail(995, "The elevated helper is closing.");
                }

                if (!IsConnected)
                {
                    if (!allowStart || elevationCancelled)
                    {
                        return GameHelperElevationResponse.Fail(1223,
                            "Administrator approval is required and was not granted.");
                    }

                    GameHelperElevationResponse connected = await ConnectAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!connected.Success)
                    {
                        return connected;
                    }
                }

                request.Type = "post";
                request.Token = token;
                await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
                string responseLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(responseLine))
                {
                    CloseConnection();
                    return GameHelperElevationResponse.Fail(109, "The elevated helper pipe closed.");
                }

                GameHelperElevationResponse response = JsonSerializer.Deserialize<GameHelperElevationResponse>(
                    responseLine, JsonOptions);
                return response ?? GameHelperElevationResponse.Fail(13, "The elevated helper returned invalid data.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                CloseConnection();
                return GameHelperElevationResponse.Fail(ex.HResult, ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<GameHelperElevationResponse> ConnectAsync(CancellationToken cancellationToken)
        {
            CloseConnection();
            string pipeName = "GuardCenter.GameHelper." + Guid.NewGuid().ToString("N");
            token = Guid.NewGuid().ToString("N");
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            string executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = AppPaths.InstalledExePath;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--game-helper-elevated --pipe " + Quote(pipeName) + " --token " + Quote(token),
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppPaths.Root,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                elevationCancelled = true;
                CloseConnection();
                return GameHelperElevationResponse.Fail(1223, "Administrator approval was canceled.");
            }
            catch (Exception ex)
            {
                CloseConnection();
                return GameHelperElevationResponse.Fail(ex.HResult, ex.Message);
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    CloseConnection();
                    return GameHelperElevationResponse.Fail(1460,
                        "The elevated helper did not connect within 20 seconds.");
                }
            }

            reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            return new GameHelperElevationResponse { Success = true };
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (gate.Wait(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    CloseConnection();
                }
                finally
                {
                    gate.Release();
                }
            }
            else
            {
                CloseConnection();
            }
        }

        private void CloseConnection()
        {
            if (writer != null)
            {
                writer.Dispose();
                writer = null;
            }
            if (reader != null)
            {
                reader.Dispose();
                reader = null;
            }
            if (pipe != null)
            {
                pipe.Dispose();
                pipe = null;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }

    internal static class GameHelperElevationHost
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        public static bool TryHandleCommandLine(string[] args)
        {
            if (!HasArg(args, "--game-helper-elevated"))
            {
                return false;
            }

            string pipeName = GetArgValue(args, "--pipe");
            string token = GetArgValue(args, "--token");
            if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(token) || !IsAdministrator())
            {
                AppLog.Write("Game Helper", "Rejected invalid elevated helper invocation.");
                return true;
            }

            RunAsync(pipeName, token).GetAwaiter().GetResult();
            return true;
        }

        private static async Task RunAsync(string pipeName, string token)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
                }

                using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    while (true)
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                        {
                            return;
                        }

                        GameHelperElevationResponse response;
                        try
                        {
                            GameHelperElevationRequest request = JsonSerializer.Deserialize<GameHelperElevationRequest>(
                                line, JsonOptions);
                            response = HandleRequest(request, token);
                        }
                        catch (Exception ex)
                        {
                            response = GameHelperElevationResponse.Fail(ex.HResult, ex.Message);
                        }

                        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions))
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        internal static GameHelperElevationResponse HandleRequest(GameHelperElevationRequest request,
            string expectedToken)
        {
            if (request == null || !string.Equals(request.Type, "post", StringComparison.Ordinal)
                || !string.Equals(request.Token, expectedToken, StringComparison.Ordinal)
                || request.ThreadId == 0 || request.FallbackHwnd == 0 || request.Layout == 0
                || string.IsNullOrWhiteSpace(request.ExpectedPath))
            {
                return GameHelperElevationResponse.Fail(87, "The elevated helper rejected an invalid request.");
            }

            uint actualThreadId;
            uint processId;
            IntPtr fallbackHwnd = new IntPtr(request.FallbackHwnd);
            if (!GameInputLanguageNative.TryGetWindowProcess(fallbackHwnd, out actualThreadId, out processId)
                || actualThreadId != request.ThreadId)
            {
                return GameHelperElevationResponse.Fail(1400, "The requested game window is no longer valid.");
            }

            string actualPath;
            int pathError;
            if (!GameInputLanguageNative.TryGetProcessPath(processId, out actualPath, out pathError)
                || !PathsEqual(actualPath, request.ExpectedPath))
            {
                return GameHelperElevationResponse.Fail(pathError == 0 ? 5 : pathError,
                    "The requested window does not belong to the configured game executable.");
            }

            int usedIndex;
            int error;
            bool success = GameInputLanguageNative.TryPostLayout(request.ThreadId, fallbackHwnd,
                new IntPtr(request.Layout), request.PreferredCandidateIndex, out usedIndex, out error);
            return new GameHelperElevationResponse
            {
                Success = success,
                Error = error,
                UsedCandidateIndex = usedIndex,
                Message = success ? string.Empty : "Posting the input-language request failed."
            };
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static bool HasArg(string[] args, string value)
        {
            return Array.FindIndex(args, delegate(string arg)
            {
                return string.Equals(arg, value, StringComparison.OrdinalIgnoreCase);
            }) >= 0;
        }

        private static string GetArgValue(string[] args, string key)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return string.Empty;
        }
    }

    internal sealed class GameHelperElevationRequest
    {
        public string Type;
        public string Token;
        public uint ThreadId;
        public long FallbackHwnd;
        public long Layout;
        public string ExpectedPath;
        public int PreferredCandidateIndex;
    }

    internal sealed class GameHelperElevationResponse
    {
        public bool Success;
        public int Error;
        public int UsedCandidateIndex = -1;
        public string Message = string.Empty;

        public static GameHelperElevationResponse Fail(int error, string message)
        {
            return new GameHelperElevationResponse
            {
                Success = false,
                Error = error,
                Message = message ?? string.Empty
            };
        }
    }
}
