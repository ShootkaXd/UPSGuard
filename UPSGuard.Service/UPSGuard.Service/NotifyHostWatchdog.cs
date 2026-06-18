using System.Diagnostics;

namespace UPSGuard.Service;

public sealed class NotifyHostWatchdog : BackgroundService
{
    private const int CheckIntervalSeconds = 5;
    private const int MissingFileDelaySeconds = 10;
    private const int NoSessionDelaySeconds = 5;

    private const int PostLaunchCheckSeconds = 2;
    private const int FailureWindowSeconds = 60;
    private const int MaxFailuresInWindow = 3;
    private const int BackoffSeconds = 60;

    private readonly UserSessionLauncher _launcher;
    private readonly ServiceLogger _log;
    private readonly string _notifyHostPath;
    private readonly string _workingDir;

    private readonly Dictionary<int, LaunchFailureState> _failureStates = new();

    public NotifyHostWatchdog(
        UserSessionLauncher launcher,
        ServiceLogger log,
        UpsGuardState state,
        IHostEnvironment env)
    {
        _launcher = launcher;
        _log = log;

        _notifyHostPath = Path.Combine(AppContext.BaseDirectory, "UPSGuard.NotifyHost.exe");
        _workingDir = Path.GetDirectoryName(_notifyHostPath) ?? AppContext.BaseDirectory;

        _log.Info(nameof(NotifyHostWatchdog),
            $"Constructor called. NotifyHostPath={_notifyHostPath}, WorkingDir={_workingDir}");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info(nameof(NotifyHostWatchdog), "ExecuteAsync entered");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(_notifyHostPath))
                {
                    _log.Warn(nameof(NotifyHostWatchdog), $"NotifyHost file missing: {_notifyHostPath}");
                    await Task.Delay(TimeSpan.FromSeconds(MissingFileDelaySeconds), stoppingToken);
                    continue;
                }

                var sessions = _launcher.GetActiveUserSessions();

                if (sessions.Count == 0)
                {
                    _log.Info(nameof(NotifyHostWatchdog), "No active user sessions found.");
                    await Task.Delay(TimeSpan.FromSeconds(NoSessionDelaySeconds), stoppingToken);
                    continue;
                }

                _log.Info(nameof(NotifyHostWatchdog), $"Active sessions: {string.Join(", ", sessions)}");

                foreach (var sessionId in sessions)
                {
                    await ProcessSessionAsync(sessionId, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(nameof(NotifyHostWatchdog), "Watchdog loop failed", ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), stoppingToken);
        }

        _log.Info(nameof(NotifyHostWatchdog), "Watchdog stopped.");
    }

    private async Task ProcessSessionAsync(int sessionId, CancellationToken stoppingToken)
    {
        try
        {
            if (IsInBackoff(sessionId))
                return;

            var running = _launcher.IsProcessRunningInSession("UPSGuard.NotifyHost", sessionId);

            if (running)
            {
                ResetFailures(sessionId);
                return;
            }

            _log.Warn(nameof(NotifyHostWatchdog),
                $"NotifyHost is not running in session {sessionId}. Launching...");

            var args = $"--session-id {sessionId}";

            if (!_launcher.TryLaunchInSession(_notifyHostPath, args, _workingDir, sessionId, out var pid))
            {
                _log.Warn(nameof(NotifyHostWatchdog),
                    $"Failed to launch NotifyHost in session {sessionId}");

                RegisterFailure(sessionId, "CreateProcessAsUser returned false");
                return;
            }

            _log.Info(nameof(NotifyHostWatchdog),
                $"NotifyHost started. PID={pid}, Session={sessionId}");

            var alive = await VerifyProcessAliveAsync(pid, sessionId, stoppingToken);

            if (alive)
            {
                _log.Info(nameof(NotifyHostWatchdog),
                    $"NotifyHost launch verified. PID={pid}, Session={sessionId}");

                ResetFailures(sessionId);
            }
            else
            {
                RegisterFailure(sessionId, $"Process exited or disappeared immediately. PID={pid}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(nameof(NotifyHostWatchdog),
                $"Session loop failed for session {sessionId}", ex);

            RegisterFailure(sessionId, ex.Message);
        }
    }

    private async Task<bool> VerifyProcessAliveAsync(
        int pid,
        int sessionId,
        CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(PostLaunchCheckSeconds), stoppingToken);

            using var process = Process.GetProcessById(pid);

            if (process.HasExited)
            {
                _log.Warn(nameof(NotifyHostWatchdog),
                    $"NotifyHost exited immediately. PID={pid}, Session={sessionId}, ExitCode={process.ExitCode}");

                return false;
            }

            if (process.SessionId != sessionId)
            {
                _log.Warn(nameof(NotifyHostWatchdog),
                    $"NotifyHost started in unexpected session. PID={pid}, ExpectedSession={sessionId}, ActualSession={process.SessionId}");

                return false;
            }

            return true;
        }
        catch (ArgumentException)
        {
            _log.Warn(nameof(NotifyHostWatchdog),
                $"NotifyHost process disappeared after launch. PID={pid}, Session={sessionId}");

            return false;
        }
        catch (InvalidOperationException)
        {
            _log.Warn(nameof(NotifyHostWatchdog),
                $"NotifyHost process is unavailable after launch. PID={pid}, Session={sessionId}");

            return false;
        }
    }

    private bool IsInBackoff(int sessionId)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_failureStates.TryGetValue(sessionId, out var state))
            return false;

        if (state.BackoffUntilUtc <= now)
            return false;

        _log.Warn(nameof(NotifyHostWatchdog),
            $"NotifyHost launch suppressed by backoff. Session={sessionId}, BackoffUntilUtc={state.BackoffUntilUtc:O}");

        return true;
    }

    private void RegisterFailure(int sessionId, string reason)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_failureStates.TryGetValue(sessionId, out var state))
        {
            state = new LaunchFailureState
            {
                WindowStartUtc = now
            };

            _failureStates[sessionId] = state;
        }

        if ((now - state.WindowStartUtc).TotalSeconds > FailureWindowSeconds)
        {
            state.WindowStartUtc = now;
            state.FailureCount = 0;
        }

        state.FailureCount++;

        _log.Warn(nameof(NotifyHostWatchdog),
            $"NotifyHost launch failure. Session={sessionId}, Count={state.FailureCount}, Reason={reason}");

        if (state.FailureCount >= MaxFailuresInWindow)
        {
            state.BackoffUntilUtc = now.AddSeconds(BackoffSeconds);
            state.FailureCount = 0;
            state.WindowStartUtc = now;

            _log.Warn(nameof(NotifyHostWatchdog),
                $"NotifyHost crash-loop detected. Session={sessionId}. Backoff={BackoffSeconds}s");
        }
    }

    private void ResetFailures(int sessionId)
    {
        if (_failureStates.Remove(sessionId))
        {
            _log.Info(nameof(NotifyHostWatchdog),
                $"NotifyHost failure state reset. Session={sessionId}");
        }
    }

    private sealed class LaunchFailureState
    {
        public int FailureCount { get; set; }
        public DateTimeOffset WindowStartUtc { get; set; }
        public DateTimeOffset BackoffUntilUtc { get; set; }
    }
}