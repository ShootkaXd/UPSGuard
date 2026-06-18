using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text;

namespace UPSGuard.SignalClient
{
    internal class Program
    {
        private const string PipeName = "UPS_GUARD_PIPE_MACHINE";

        private const string COMMAND_BATT_ON = "BATT_ON";
        private const string COMMAND_BATT_OFF = "BATT_OFF";

        private const string DEFAULT_COMMAND = COMMAND_BATT_ON;

        private const int PIPE_CONNECT_TIMEOUT_MS = 5000;


        private static string LogPath()
        {
            try
            {
                var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                             ?? AppDomain.CurrentDomain.BaseDirectory;

                var testFile = Path.Combine(baseDir, ".write_test");
                File.WriteAllText(testFile, "ok");
                File.Delete(testFile);

                return Path.Combine(baseDir, "UPSGuard.SignalClient.log");
            }
            catch
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UPSGuard");

                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "UPSGuard.SignalClient.log");
            }
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(
                    LogPath(),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static string? ResolveCommand(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                Log("Empty args, command ignored.");
                return null;
            }

            string trimmed = raw.Trim();

            string upper = trimmed.ToUpperInvariant();
            if (upper == COMMAND_BATT_ON || upper == COMMAND_BATT_OFF)
            {
                Log($"Using direct command: {upper}");
                return upper;
            }

            string lower = trimmed.ToLowerInvariant();

            if (lower.Contains("on battery") ||
                lower.Contains("onbat") ||
                lower.Contains("discharging"))
            {
                Log($"Mapped UPS state '{trimmed}' -> {COMMAND_BATT_ON}");
                return COMMAND_BATT_ON;
            }

            if (lower.Contains("on line") ||
                lower.Contains("online") ||
                lower.Contains("utility") ||
                lower.Contains("line power") ||
                lower.Contains("charging"))
            {
                Log($"Mapped UPS state '{trimmed}' -> {COMMAND_BATT_OFF}");
                return COMMAND_BATT_OFF;
            }

            Log($"Unknown UPS state '{trimmed}', command ignored.");
            return null;
        }

        private static int Main(string[] args)
        {
            string raw = args != null && args.Length > 0
                ? string.Join(" ", args)
                : "";

            string? command = ResolveCommand(raw);

            if (string.IsNullOrWhiteSpace(command))
            {
                Log("No valid command resolved. Exit without sending.");
                return 10;
            }

            try
            {
                Log($"Starting. User={WindowsIdentity.GetCurrent().Name}, Pipe={PipeName}, Command={command}");

                using (var client = new NamedPipeClientStream(
                           ".",
                           PipeName,
                           PipeDirection.Out,
                           PipeOptions.None,
                           TokenImpersonationLevel.Identification))
                {
                    client.Connect(PIPE_CONNECT_TIMEOUT_MS);

                    using (var w = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true))
                    {
                        w.NewLine = "\r\n";
                        w.WriteLine(command);
                        w.Flush();
                    }
                }

                Log("Command sent OK.");
                return 0;
            }
            catch (TimeoutException)
            {
                Log("Timeout: NotifyHost не запущен (пайп недоступен) или не успел ответить.");
                return 1;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log("Access denied to pipe (ACL/учётка сервера): " + ex.Message);
                return 4;
            }
            catch (IOException ex)
            {
                Log("Pipe not found / IO error: " + ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Log("Unknown error: " + ex);
                return 3;
            }
        }
    }
}
