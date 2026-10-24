using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace APKToolGUI.Utils
{
    /// <summary>
    /// One child process spawned by this application while handling an APK Info request.
    /// Only process metadata is stored, never command lines containing user paths.
    /// </summary>
    public sealed class ChildProcessRecord
    {
        public int ProcessId { get; set; }
        public string Stage { get; set; }
        public DateTime StartedAtUtc { get; set; }
    }

    /// <summary>
    /// Tracks child processes created by this application so a diagnostic snapshot can list
    /// which PIDs it still owns. Never enumerates or touches processes started by others.
    /// </summary>
    public sealed class ChildProcessTracker
    {
        private readonly object sync = new object();
        private readonly Dictionary<int, ChildProcessRecord> records = new Dictionary<int, ChildProcessRecord>();

        public void Register(int processId, string stage, DateTime startedAtUtc)
        {
            lock (sync)
                records[processId] = new ChildProcessRecord { ProcessId = processId, Stage = stage, StartedAtUtc = startedAtUtc };
        }

        public void Unregister(int processId)
        {
            lock (sync)
                records.Remove(processId);
        }

        public int Count
        {
            get { lock (sync) return records.Count; }
        }

        public IList<ChildProcessRecord> Snapshot()
        {
            lock (sync)
            {
                foreach (int processId in records.Keys.ToList())
                {
                    if (!IsStillRunning(records[processId])) records.Remove(processId);
                }
                return records.Values.OrderBy(r => r.ProcessId).ToList();
            }
        }

        // Guards against PID reuse: the process must still exist and its start time must match.
        private static bool IsStillRunning(ChildProcessRecord record)
        {
            try
            {
                using (Process process = Process.GetProcessById(record.ProcessId))
                {
                    if (process.HasExited) return false;
                    return Math.Abs((process.StartTime.ToUniversalTime() - record.StartedAtUtc).TotalSeconds) < 2;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }
    }

    /// <summary>Input for a diagnostic snapshot. Paths are reduced to file names before output.</summary>
    public sealed class DiagnosticSnapshotInput
    {
        public string AppVersion { get; set; }
        public string BuildConfiguration { get; set; }
        public string Commit { get; set; }
        public DateTime ProcessStartLocalTime { get; set; }
        public int ActiveApkInfoRequests { get; set; }
        public string ApkInfoStage { get; set; }
        public string LastApkFileName { get; set; }
        public double? UiDispatchDelayMilliseconds { get; set; }
        public string SignatureStage { get; set; }
        public IList<ChildProcessRecord> ChildProcesses { get; set; }
    }

    public static class DiagnosticSnapshot
    {
        /// <summary>Keeps only the file name so a shared snapshot does not leak folder layout.</summary>
        public static string SanitizeFileName(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "";
            try { return Path.GetFileName(path.Trim()); }
            catch (ArgumentException) { return ""; }
        }

        public static string Build(DiagnosticSnapshotInput input)
        {
            if (input == null) throw new ArgumentNullException("input");

            var sb = new StringBuilder();
            sb.AppendLine("APK Tool GUI diagnostic snapshot");
            sb.AppendLine("Created (local): " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("App version: " + (input.AppVersion ?? "unknown"));
            sb.AppendLine("Build configuration: " + (input.BuildConfiguration ?? "unknown"));
            sb.AppendLine("Source commit: " + (input.Commit ?? "unknown"));
            sb.AppendLine("Process start (local): " + (input.ProcessStartLocalTime == default(DateTime) ? "unknown" : input.ProcessStartLocalTime.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.AppendLine("Process uptime: " + FormatUptime(input.ProcessStartLocalTime));
            sb.AppendLine("Processor count: " + Environment.ProcessorCount);
            sb.AppendLine("OS: " + Environment.OSVersion.VersionString);
            sb.AppendLine("64-bit process: " + Environment.Is64BitProcess);
            sb.AppendLine();

            AppendProcessResources(sb);
            sb.AppendLine();
            AppendApkInfoState(sb, input);
            sb.AppendLine();
            AppendChildProcesses(sb, input.ChildProcesses);
            sb.AppendLine();
            AppendNotes(sb);
            return sb.ToString();
        }

        private static string FormatUptime(DateTime processStartLocalTime)
        {
            if (processStartLocalTime == default(DateTime)) return "unknown";
            TimeSpan uptime = DateTime.Now - processStartLocalTime;
            if (uptime < TimeSpan.Zero) return "unknown";
            return String.Format("{0:00}:{1:00}:{2:00}", (int)uptime.TotalHours, uptime.Minutes, uptime.Seconds);
        }

        private static void AppendProcessResources(StringBuilder sb)
        {
            sb.AppendLine("[Process resources]");
            try
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    current.Refresh();
                    sb.AppendLine("Managed memory (heap, bytes): " + GC.GetTotalMemory(false));
                    sb.AppendLine("Private memory (bytes): " + current.PrivateMemorySize64);
                    sb.AppendLine("Working set (bytes): " + current.WorkingSet64);
                    sb.AppendLine("Paged memory (bytes): " + current.PagedMemorySize64);
                    try { sb.AppendLine("Handle count: " + current.HandleCount); }
                    catch (Exception ex) { sb.AppendLine("Handle count: unavailable (" + ex.GetType().Name + ")"); }
                    try { sb.AppendLine("Thread count: " + current.Threads.Count); }
                    catch (Exception ex) { sb.AppendLine("Thread count: unavailable (" + ex.GetType().Name + ")"); }
                    sb.AppendLine("GDI objects: " + GdiUsage());
                    sb.AppendLine("USER objects: " + UserUsage());
                    sb.AppendLine("Total GC collections (gen0/1/2): " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Unavailable: " + ex.GetType().Name);
            }
        }

        private static void AppendApkInfoState(StringBuilder sb, DiagnosticSnapshotInput input)
        {
            sb.AppendLine("[APK Info]");
            sb.AppendLine("Active requests: " + input.ActiveApkInfoRequests);
            sb.AppendLine("Current stage: " + (input.ApkInfoStage ?? "idle"));
            sb.AppendLine("Last file: " + (String.IsNullOrWhiteSpace(input.LastApkFileName) ? "(none)" : SanitizeFileName(input.LastApkFileName)));
            sb.AppendLine("Last UI dispatch delay (ms): " + (input.UiDispatchDelayMilliseconds.HasValue
                ? input.UiDispatchDelayMilliseconds.Value.ToString("0.0")
                : "not measured"));
            sb.AppendLine("Signature verification: " + (input.SignatureStage ?? "idle"));
        }

        private static void AppendChildProcesses(StringBuilder sb, IList<ChildProcessRecord> children)
        {
            sb.AppendLine("[Child processes owned by this app]");
            if (children == null || children.Count == 0)
            {
                sb.AppendLine("(none)");
                return;
            }

            foreach (ChildProcessRecord child in children)
            {
                TimeSpan running = DateTime.UtcNow - child.StartedAtUtc;
                if (running < TimeSpan.Zero) running = TimeSpan.Zero;
                sb.AppendLine(String.Format("PID {0} stage={1} running={2:0.0}s", child.ProcessId, child.Stage ?? "unknown", running.TotalSeconds));
            }
        }

        private static void AppendNotes(StringBuilder sb)
        {
            sb.AppendLine("[Notes]");
            sb.AppendLine("- Only file names are recorded; folder layout and APK contents are not included.");
            sb.AppendLine("- Long-idle UI slowdown after more than one hour has not been reproduced in this build,");
            sb.AppendLine("  so its root cause is still unknown. This snapshot only helps to compare resource use,");
            sb.AppendLine("  active requests, owned child processes and UI dispatch latency over time.");
            sb.AppendLine("- Review the file before sharing it: it describes this machine's process state.");
        }

        private static string GdiUsage()
        {
            try { return GetGuiResources(Process.GetCurrentProcess().Handle, 0).ToString(); }
            catch (Exception) { return "unavailable"; }
        }

        private static string UserUsage()
        {
            try { return GetGuiResources(Process.GetCurrentProcess().Handle, 1).ToString(); }
            catch (Exception) { return "unavailable"; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    }
}