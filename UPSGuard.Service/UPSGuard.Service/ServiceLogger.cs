using System.Text;

namespace UPSGuard.Service;

public sealed class ServiceLogger
{
    private readonly object _sync = new();
    private readonly string _path;

    public ServiceLogger()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "UPSGuard");

        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "UPSGuard.Service.log");
    }

    public void Info(string source, string message) => Write("INFO", source, message);
    public void Warn(string source, string message) => Write("WARN", source, message);
    public void Error(string source, string message) => Write("ERROR", source, message);
    public void Error(string source, string message, Exception ex) => Write("ERROR", source, $"{message}{Environment.NewLine}{ex}");

    private void Write(string level, string source, string message)
    {
        try
        {
            lock (_sync)
            {
                var line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | " +
                    $"LVL={level,-5} | " +
                    $"PID={Environment.ProcessId} | " +
                    $"TID={Environment.CurrentManagedThreadId} | " +
                    $"SRC={source} | " +
                    $"{message}";

                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}