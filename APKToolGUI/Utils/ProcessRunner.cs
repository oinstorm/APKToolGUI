using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace APKToolGUI.Utils
{
    public sealed class ProcessRunResult
    {
        public string StandardOutput { get; internal set; }
        public string StandardError { get; internal set; }
        public int ExitCode { get; internal set; }
        public int ProcessId { get; internal set; }
        public DateTime StartedAtUtc { get; internal set; }
        public DateTime ExitedAtUtc { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }
        public bool TimedOut { get; internal set; }
        public bool Cancelled { get; internal set; }
    }

    public static class ProcessRunner
    {
        public static Task<ProcessRunResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken, Action<int, DateTime> onStarted = null)
        {
            if (startInfo == null) throw new ArgumentNullException("startInfo");
            return Task.Run(() => Run(startInfo, timeout, cancellationToken, onStarted), CancellationToken.None);
        }

        private static ProcessRunResult Run(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken, Action<int, DateTime> onStarted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            if (startInfo.StandardOutputEncoding == null) startInfo.StandardOutputEncoding = Encoding.UTF8;
            if (startInfo.StandardErrorEncoding == null) startInfo.StandardErrorEncoding = Encoding.UTF8;

            var watch = Stopwatch.StartNew();
            using (var process = new Process { StartInfo = startInfo })
            {
                if (!process.Start()) throw new InvalidOperationException("The child process did not start.");
                var startedAt = DateTime.UtcNow;
                int processId = process.Id;
                if (onStarted != null)
                {
                    try { onStarted(processId, startedAt); }
                    catch (Exception ex) { Debug.WriteLine(ex); }
                }

                Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
                Task<string> standardError = process.StandardError.ReadToEndAsync();
                bool timedOut = false;
                bool cancelled = false;
                int timeoutMilliseconds = timeout <= TimeSpan.Zero || timeout.TotalMilliseconds >= Int32.MaxValue
                    ? Timeout.Infinite
                    : Math.Max(1, (int)timeout.TotalMilliseconds);

                using (cancellationToken.Register(() => TryKill(process)))
                {
                    bool exited = process.WaitForExit(timeoutMilliseconds);
                    cancelled = cancellationToken.IsCancellationRequested;
                    timedOut = !exited && !cancelled;
                    if (!exited || cancelled)
                    {
                        TryKill(process);
                        if (!process.WaitForExit(10000))
                            throw new TimeoutException("The owned child process did not exit after termination was requested.");
                    }
                }

                process.WaitForExit();
                Task.WaitAll(standardOutput, standardError);
                watch.Stop();
                return new ProcessRunResult
                {
                    StandardOutput = standardOutput.Result,
                    StandardError = standardError.Result,
                    ExitCode = process.ExitCode,
                    ProcessId = processId,
                    StartedAtUtc = startedAt,
                    ExitedAtUtc = DateTime.UtcNow,
                    Elapsed = watch.Elapsed,
                    TimedOut = timedOut,
                    Cancelled = cancelled
                };
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }   // already exited or disposed
            catch (System.ComponentModel.Win32Exception ex) { Debug.WriteLine(ex); }
        }
    }
}
