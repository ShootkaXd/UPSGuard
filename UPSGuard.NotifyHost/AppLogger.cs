using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace UPSGuard.NotifyHost
{
    internal static class AppLogger
    {
        private const string DefaultComponentName = "UPSGuard.NotifyHost";

        private static readonly object _sync = new object();
        private static readonly Stopwatch _uptime = Stopwatch.StartNew();

        private static string? _sessionId;
        private static string? _logPath;
        private static string _componentName = DefaultComponentName;

        public static void Init(string componentName)
        {
            lock (_sync)
            {
                if (_sessionId != null)
                    return;

                _componentName = string.IsNullOrWhiteSpace(componentName)
                    ? DefaultComponentName
                    : componentName.Trim();

                _sessionId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}";

                try
                {
                    _logPath = BuildWritableLogPath(_componentName);

                    WriteSafeRaw("INFO", "Logger",
                        $"Logger initialized | Session={_sessionId} | LogPath={_logPath} | " +
                        $"Machine={Environment.MachineName} | User={Environment.UserName} | " +
                        $"OS={Environment.OSVersion} | 64bitOS={Environment.Is64BitOperatingSystem} | " +
                        $"64bitProc={Environment.Is64BitProcess} | CLR={Environment.Version}");
                }
                catch
                {
                    // Логгер не должен ронять NotifyHost.
                    // Даже если AppData/Temp недоступны — приложение должно продолжить работу.
                }
            }
        }

        public static void Info(string source, string message) =>
            Write("INFO", source, message);

        public static void Debug(string source, string message) =>
            Write("DEBUG", source, message);

        public static void Warn(string source, string message) =>
            Write("WARN", source, message);

        public static void Error(string source, string message) =>
            Write("ERROR", source, message);

        public static void Error(string source, string message, Exception ex) =>
            Write("ERROR", source, $"{message}{Environment.NewLine}{FormatException(ex)}");

        public static void State(string source, string stateName, string stateValue, string? details = null)
        {
            var msg = $"STATE {stateName}={stateValue}";
            if (!string.IsNullOrWhiteSpace(details))
                msg += $" | {details}";

            Write("STATE", source, msg);
        }

        public static void Event(string source, string eventName, string? details = null)
        {
            var msg = $"EVENT {eventName}";
            if (!string.IsNullOrWhiteSpace(details))
                msg += $" | {details}";

            Write("EVENT", source, msg);
        }

        private static void Write(string level, string source, string message)
        {
            try
            {
                if (_sessionId == null)
                    Init(DefaultComponentName);

                WriteSafeRaw(level, source, message);
            }
            catch
            {
                // Логгер не должен ронять приложение.
            }
        }

        private static void WriteSafeRaw(string level, string source, string message)
        {
            try
            {
                WriteRaw(level, source, message);
            }
            catch
            {
                // Ошибка записи лога не должна выходить наружу.
            }
        }

        private static void WriteRaw(string level, string source, string message)
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(_logPath))
                    _logPath = BuildWritableLogPath(_componentName);

                var pid = Environment.ProcessId;
                var tid = Environment.CurrentManagedThreadId;
                var uptimeMs = _uptime.ElapsedMilliseconds;

                var line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | " +
                    $"LVL={level,-5} | " +
                    $"PID={pid} | TID={tid} | " +
                    $"UPTIME_MS={uptimeMs} | " +
                    $"SRC={source} | " +
                    $"{message}";

                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }

        private static string BuildWritableLogPath(string componentName)
        {
            var safeComponent = SanitizeFileName(componentName);
            var safeUser = SanitizeFileName(Environment.UserName);

            int sessionId = GetCurrentSessionIdSafe();
            int pid = Environment.ProcessId;

            var fileName =
                $"{safeComponent}_{DateTime.Now:yyyyMMdd}_s{sessionId}_{safeUser}_{pid}.log";

            string[] candidateDirs =
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UPSGuard",
                    "Logs"),

                Path.Combine(
                    Path.GetTempPath(),
                    "UPSGuard",
                    "Logs")
            };

            foreach (var dir in candidateDirs)
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                try
                {
                    Directory.CreateDirectory(dir);

                    var testFile = Path.Combine(
                        dir,
                        $".write_test_{Environment.ProcessId}_{Guid.NewGuid():N}");

                    File.WriteAllText(testFile, "ok", Encoding.UTF8);

                    try
                    {
                        File.Delete(testFile);
                    }
                    catch
                    {
                        // Не критично.
                    }

                    return Path.Combine(dir, fileName);
                }
                catch
                {
                    // Пробуем следующий путь.
                }
            }

            return Path.Combine(Path.GetTempPath(), fileName);
        }

        private static int GetCurrentSessionIdSafe()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return process.SessionId;
            }
            catch
            {
                return -1;
            }
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);

            foreach (var ch in value)
            {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            return sb.ToString()
                .Replace('\\', '_')
                .Replace('/', '_')
                .Replace(':', '_')
                .Trim();
        }

        private static string FormatException(Exception ex)
        {
            var sb = new StringBuilder();

            int depth = 0;
            Exception? current = ex;

            while (current != null)
            {
                sb.AppendLine($"[EXCEPTION #{depth}] {current.GetType().FullName}: {current.Message}");

                if (!string.IsNullOrWhiteSpace(current.StackTrace))
                    sb.AppendLine(current.StackTrace);

                current = current.InnerException;
                depth++;

                if (current != null)
                    sb.AppendLine("---- INNER EXCEPTION ----");
            }

            return sb.ToString();
        }
    }
}