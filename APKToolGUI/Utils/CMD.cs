using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace APKToolGUI.Utils
{
    public class CMD
    {
        public static string ProcessStartWithOutput(string FileName, string Arguments)
        {
            return ProcessStartWithOutput(FileName, Arguments, Timeout.InfiniteTimeSpan, CancellationToken.None, null);
        }

        /// <summary>
        /// Runs a tool and returns its standard output. Both standard streams are drained
        /// concurrently so a chatty tool cannot block on a full pipe. On timeout or cancellation
        /// only the process started here is terminated, then awaited before returning.
        /// </summary>
        public static string ProcessStartWithOutput(string FileName, string Arguments, TimeSpan timeout,
            CancellationToken cancellationToken, Action<int, DateTime> onProcessStarted)
        {
            ProcessRunResult result = TryRun(FileName, Arguments, timeout, cancellationToken, onProcessStarted);
            if (result == null || result.TimedOut) return string.Empty;
            return (result.StandardOutput ?? string.Empty).Trim();
        }

        /// <summary>Same as <see cref="ProcessStartWithOutput(string,string,TimeSpan,CancellationToken,Action{int,DateTime})"/>
        /// but exposes the exit code, PID and timeout flag. Returns null when the tool could not start.</summary>
        public static ProcessRunResult TryRun(string FileName, string Arguments, TimeSpan timeout,
            CancellationToken cancellationToken, Action<int, DateTime> onProcessStarted)
        {
            Log.d("CMD: " + FileName + " " + Arguments);

            var startInfo = new ProcessStartInfo
            {
                FileName = FileName,
                Arguments = Arguments,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.GetEncoding("utf-8"),
                StandardErrorEncoding = Encoding.GetEncoding("utf-8"),
            };

            try
            {
                return ProcessRunner.RunAsync(startInfo, timeout, cancellationToken, onProcessStarted)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.WriteLine("Start: " + Path.GetFileName(FileName) + " failed: " + e.GetType().Name);
                return null;
            }
        }
    }
}