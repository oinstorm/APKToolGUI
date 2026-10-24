using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using APKToolGUI.Utils;

namespace APKToolGUI.Tests
{
    /// <summary>
    /// Dependency-free console harness. It links the request coordinator, the process runner and the
    /// diagnostic snapshot helpers straight from the application sources, so the checks run on a
    /// Windows build agent without adding NuGet test frameworks to the product.
    /// </summary>
    internal static class Program
    {
        private static int failures;
        private static int checks;

        private static int Main()
        {
            Console.WriteLine("APK Tool GUI component checks");
            Console.WriteLine();

            Run("coordinator deduplicates the same key", CoordinatorDeduplicatesSameKey);
            Run("coordinator cancels a superseded request", CoordinatorCancelsPreviousRequest);
            Run("coordinator keeps at most one active request", CoordinatorSingleActiveRequest);
            Run("coordinator rejects results of an outdated request", CoordinatorStaleResultIsNotCurrent);
            Run("coordinator cancellation surfaces as OperationCanceledException", CoordinatorCancellationPropagates);
            Run("process runner drains both output streams", RunnerDrainsBothStreams);
            Run("process runner reports exit code and pid", RunnerReportsExitCode);
            Run("process runner times out and kills its own child", RunnerTimesOut);
            Run("process runner cancels and kills its own child", RunnerCancels);
            Run("diagnostic snapshot keeps file names only", SnapshotSanitizesPaths);
            Run("diagnostic snapshot drops exited children", SnapshotDropsExitedChildren);
            Run("diagnostic snapshot states the idle-slowdown status", SnapshotStatesUnknownRootCause);

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "All " + checks + " checks passed."
                : failures + " of " + checks + " checks failed.");

            return failures == 0 ? 0 : 1;
        }

        #region Harness

        private static void Run(string name, Action check)
        {
            checks++;
            try
            {
                check();
                Console.WriteLine("PASS  " + name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAIL  " + name + " :: " + ex.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(message + " (expected " + expected + ", got " + actual + ")");
        }

        private static string SystemTool(string name)
        {
            string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string path = Path.Combine(system32, name);
            return File.Exists(path) ? path : name;
        }

        private static bool ProcessExists(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId)) return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        #endregion

        #region Coordinator

        private static void CoordinatorDeduplicatesSameKey()
        {
            var coordinator = new LatestRequestCoordinator<string>();
            int runs = 0;
            var gate = new ManualResetEventSlim(false);

            Func<long, CancellationToken, Task<string>> operation = (id, token) =>
            {
                Interlocked.Increment(ref runs);
                gate.Wait(TimeSpan.FromSeconds(10));
                return Task.FromResult("first");
            };

            RequestHandle<string> first = coordinator.Start(@"C:\apk\app.apk", operation);
            RequestHandle<string> second = coordinator.Start(@"c:\APK\APP.APK", operation);

            AssertEqual(first.Id, second.Id, "the same file must reuse the running request");
            Assert(ReferenceEquals(first.Completion, second.Completion), "the running request completion must be reused");

            gate.Set();
            first.Completion.Wait(TimeSpan.FromSeconds(10));
            AssertEqual(1, runs, "the operation must run once");
        }

        private static void CoordinatorCancelsPreviousRequest()
        {
            var coordinator = new LatestRequestCoordinator<string>();
            var started = new ManualResetEventSlim(false);

            RequestHandle<string> first = coordinator.Start("a.apk", (id, token) =>
            {
                started.Set();
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                token.ThrowIfCancellationRequested();
                return Task.FromResult("a");
            });

            Assert(started.Wait(TimeSpan.FromSeconds(10)), "first request must start");

            RequestHandle<string> second = coordinator.Start("b.apk", (id, token) => Task.FromResult("b"));

            Assert(first.Token.IsCancellationRequested, "the previous request must be cancelled");
            Assert(second.Completion.Wait(TimeSpan.FromSeconds(10)), "the new request must complete");
            AssertEqual("b", second.Completion.Result, "the new request must produce its own result");
            Assert(first.Completion.IsCanceled || first.Completion.IsFaulted,
                "the cancelled request must not report success");
        }

        private static void CoordinatorSingleActiveRequest()
        {
            var coordinator = new LatestRequestCoordinator<string>();
            int maximum = 0;
            var gate = new ManualResetEventSlim(false);

            Func<long, CancellationToken, Task<string>> operation = (id, token) =>
            {
                maximum = Math.Max(maximum, coordinator.ActiveCount);
                while (!gate.IsSet && !token.IsCancellationRequested) Thread.Sleep(5);
                token.ThrowIfCancellationRequested();
                return Task.FromResult("x");
            };

            List<RequestHandle<string>> handles = new List<RequestHandle<string>>();
            for (int i = 0; i < 5; i++)
                handles.Add(coordinator.Start("file-" + i + ".apk", operation));

            gate.Set();
            foreach (RequestHandle<string> handle in handles)
            {
                try { handle.Completion.Wait(TimeSpan.FromSeconds(15)); }
                catch (AggregateException) { }
            }

            Assert(maximum <= 1, "requests must not run side by side, observed " + maximum);
            Assert(coordinator.ActiveCount == 0, "no request may stay active after completion");
        }

        private static void CoordinatorStaleResultIsNotCurrent()
        {
            var coordinator = new LatestRequestCoordinator<string>();
            var gate = new ManualResetEventSlim(false);

            RequestHandle<string> first = coordinator.Start("old.apk", (id, token) =>
            {
                while (!gate.IsSet && !token.IsCancellationRequested) Thread.Sleep(5);
                token.ThrowIfCancellationRequested();
                return Task.FromResult("old");
            });
            RequestHandle<string> second = coordinator.Start("new.apk", (id, token) => Task.FromResult("new"));

            gate.Set();
            try { first.Completion.Wait(TimeSpan.FromSeconds(10)); }
            catch (AggregateException) { }
            second.Completion.Wait(TimeSpan.FromSeconds(10));

            Assert(!coordinator.IsCurrent(first.Id), "an outdated request must not be reported as current");
            Assert(coordinator.IsCurrent(second.Id), "the newest request must be current");
        }

        private static void CoordinatorCancellationPropagates()
        {
            var coordinator = new LatestRequestCoordinator<string>();
            var started = new ManualResetEventSlim(false);

            RequestHandle<string> handle = coordinator.Start("cancel.apk", (id, token) =>
            {
                started.Set();
                // Cancellation surfaces only through the token, as a real operation would observe.
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                token.ThrowIfCancellationRequested();
                return Task.FromResult("never");
            });

            Assert(started.Wait(TimeSpan.FromSeconds(10)), "request must start");
            coordinator.CancelCurrent();

            try
            {
                handle.Completion.Wait(TimeSpan.FromSeconds(10));
                throw new Exception("cancelled request must not complete successfully");
            }
            catch (AggregateException ex)
            {
                Assert(ex.InnerExceptions.Any(e => e is OperationCanceledException),
                    "expected a cancellation, got " + ex.InnerException);
            }
        }

        #endregion

        #region Process runner

        private static ProcessStartInfo LongRunningPing(int seconds)
        {
            return new ProcessStartInfo
            {
                FileName = SystemTool("ping.exe"),
                Arguments = "-n " + seconds + " 127.0.0.1",
            };
        }

        private static void RunnerDrainsBothStreams()
        {
            const int lines = 20000;
            var startInfo = new ProcessStartInfo
            {
                FileName = SystemTool("cmd.exe"),
                Arguments = "/c \"for /L %i in (1,1," + lines + ") do @(echo out-%i & echo err-%i 1>&2)\"",
            };

            ProcessRunResult result = ProcessRunner.RunAsync(startInfo, TimeSpan.FromMinutes(2), CancellationToken.None)
                .GetAwaiter().GetResult();

            int outLines = result.StandardOutput.Split('\n').Count(l => l.Trim().Length > 0);
            int errLines = result.StandardError.Split('\n').Count(l => l.Trim().Length > 0);

            Assert(outLines >= lines, "standard output was truncated: " + outLines);
            Assert(errLines >= lines, "standard error was truncated: " + errLines);
            Assert(result.StandardOutput.Contains("out-" + lines), "standard output lost its last line");
            Assert(result.StandardError.Contains("err-" + lines), "standard error lost its last line");
        }

        private static void RunnerReportsExitCode()
        {
            ProcessRunResult result = ProcessRunner.RunAsync(
                new ProcessStartInfo { FileName = SystemTool("cmd.exe"), Arguments = "/c exit 3" },
                TimeSpan.FromMinutes(1), CancellationToken.None).GetAwaiter().GetResult();

            AssertEqual(3, result.ExitCode, "exit code must be reported");
            Assert(result.ProcessId > 0, "process id must be reported");
            Assert(result.StartedAtUtc <= result.ExitedAtUtc, "start and exit times must be ordered");
            Assert(!result.TimedOut && !result.Cancelled, "a normal run must not be flagged");
        }

        private static void RunnerTimesOut()
        {
            ProcessRunResult result = ProcessRunner.RunAsync(LongRunningPing(60), TimeSpan.FromSeconds(2), CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert(result.TimedOut, "the run must be reported as timed out");
            Assert(!ProcessExists(result.ProcessId), "the owned child must be terminated and gone");
        }

        private static void RunnerCancels()
        {
            var cancellation = new CancellationTokenSource();
            int processId = 0;
            Task<ProcessRunResult> run = ProcessRunner.RunAsync(LongRunningPing(60), TimeSpan.FromMinutes(2),
                cancellation.Token, (pid, startedAt) => processId = pid);

            SpinWait.SpinUntil(() => processId != 0, TimeSpan.FromSeconds(10));
            Assert(processId != 0, "the child process must be reported as started");
            cancellation.Cancel();

            ProcessRunResult result = run.GetAwaiter().GetResult();
            Assert(result.Cancelled, "the run must be reported as cancelled");
            Assert(!ProcessExists(processId), "the owned child must be gone after cancellation");
            cancellation.Dispose();
        }

        #endregion

        #region Diagnostics

        private static void SnapshotSanitizesPaths()
        {
            AssertEqual("app.apk", DiagnosticSnapshot.SanitizeFileName(@"C:\Users\someone\private\app.apk"),
                "only the file name may be kept");

            var input = new DiagnosticSnapshotInput
            {
                AppVersion = "3.4.0.0",
                BuildConfiguration = "Release",
                Commit = "abc1234",
                ProcessStartLocalTime = DateTime.Now.AddMinutes(-5),
                ActiveApkInfoRequests = 1,
                ApkInfoStage = "resolving icon",
                LastApkFileName = @"C:\Users\someone\private\Cafeland v2.82.1.apk",
                UiDispatchDelayMilliseconds = 12.5,
                SignatureStage = "running",
                ChildProcesses = new List<ChildProcessRecord>
                {
                    new ChildProcessRecord { ProcessId = 4242, Stage = "signature verify", StartedAtUtc = DateTime.UtcNow.AddSeconds(-3) },
                },
            };

            string text = DiagnosticSnapshot.Build(input);
            Assert(text.Contains("Cafeland v2.82.1.apk"), "the file name should be present");
            Assert(!text.Contains(@"C:\Users\someone\private"), "folder layout must not be written out");
            Assert(text.Contains("abc1234"), "the commit should be present");
            Assert(text.Contains("12.5"), "the UI dispatch delay should be present");
            Assert(text.Contains("PID 4242"), "owned child processes should be listed");
        }

        private static void SnapshotDropsExitedChildren()
        {
            var tracker = new ChildProcessTracker();
            Process child = Process.Start(new ProcessStartInfo
            {
                FileName = SystemTool("cmd.exe"),
                Arguments = "/c exit 0",
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            tracker.Register(child.Id, "aapt", child.StartTime.ToUniversalTime());
            child.WaitForExit();
            AssertEqual(0, tracker.Snapshot().Count, "an exited child must not be reported");
        }

        private static void SnapshotStatesUnknownRootCause()
        {
            string text = DiagnosticSnapshot.Build(new DiagnosticSnapshotInput { AppVersion = "3.4.0.0" });
            Assert(text.Contains("has not been reproduced"), "the snapshot must state that the slowdown is unconfirmed");
            Assert(!text.Contains("fixed"), "the snapshot must not claim the slowdown is fixed");
        }

        #endregion
    }
}