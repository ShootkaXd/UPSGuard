using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Management;

namespace UPSGuard.Service;

public sealed class Worker : BackgroundService
{
    private const string Source = nameof(Worker);

    private const string PipeName = "UPS_GUARD_PIPE_MACHINE";

    private const string COMMAND_BATT_ON = "BATT_ON";
    private const string COMMAND_BATT_OFF = "BATT_OFF";

    private const int HIBERNATE_DELAY_MS = 45_000;
    private const float BATTERY_THRESHOLD = 0.30f;
    private const int BATTERY_POLL_MS = 10_000;

    private readonly ServiceLogger _log;
    private readonly UpsGuardState _state;
    private readonly UserSessionLauncher _sessionLauncher;

    private readonly object _sync = new();

    private Timer? _hibernateTimer;
    private Timer? _batteryPollTimer;

    private bool _onBattery;
    private int _hibernateRequestedOnce;

    public Worker(
        ServiceLogger log,
        UpsGuardState state,
        UserSessionLauncher sessionLauncher)
    {
        _log = log;
        _state = state;
        _sessionLauncher = sessionLauncher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info(Source, "Worker started.");

        _state.SetHealthy();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _state.SetHealthy();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _state.SetUnhealthy(ex.Message);
                _log.Error(Source, "Worker loop failed", ex);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _state.SetStopping();

        lock (_sync)
        {
            CancelAll_NoLock("Worker stopping");
        }

        _log.Info(Source, "Worker stopped.");
    }

    public void BeginHibernateCountdown()
    {
        _log.Info(Source, $"BeginHibernateCountdown called. OnBattery={_onBattery}");

        var hasUnlockedUserSession = HasActiveUserSession();

        lock (_sync)
        {
            if (_onBattery)
            {
                _log.Warn(Source, "BATT_ON ignored because already on battery.");
                return;
            }

            _onBattery = true;
            _hibernateRequestedOnce = 0;

            if (!hasUnlockedUserSession)
            {
                _log.Warn(Source, "No unlocked active user session. Hibernate immediately without notification.");

                CancelAll_NoLock("No unlocked active user session");
                _onBattery = false;

                ThreadPool.QueueUserWorkItem(_ => DoHibernateOnce());
                return;
            }

            _hibernateTimer?.Dispose();

            _hibernateTimer = new Timer(_ =>
            {
                try
                {
                    _log.Warn(Source, $"Hibernate timer elapsed. DelayMs={HIBERNATE_DELAY_MS}");
                    DoHibernateOnce();
                }
                catch (Exception ex)
                {
                    _log.Error(Source, "Hibernate timer callback failed", ex);
                }
                finally
                {
                    lock (_sync)
                    {
                        CancelAll_NoLock("Hibernate timer elapsed");
                        _onBattery = false;
                    }
                }
            }, null, HIBERNATE_DELAY_MS, Timeout.Infinite);

            StartBatteryMonitoring_NoLock();
        }

        SendNotifyCommand(COMMAND_BATT_ON);
    }

    public void CancelHibernate()
    {
        _log.Info(Source, $"CancelHibernate called. OnBattery={_onBattery}");

        bool hadAny = false;

        lock (_sync)
        {
            _onBattery = false;

            if (_hibernateTimer != null)
            {
                _hibernateTimer.Dispose();
                _hibernateTimer = null;
                hadAny = true;
            }

            if (_batteryPollTimer != null)
            {
                _batteryPollTimer.Dispose();
                _batteryPollTimer = null;
                hadAny = true;
            }
        }

        if (hadAny)
        {
            _log.Info(Source, "Hibernate sequence cancelled because power restored.");

            if (HasActiveUserSession())
            {
                SendNotifyCommand(COMMAND_BATT_OFF);
            }
            else
            {
                _log.Warn(Source, "No unlocked active user session. Restore notification skipped.");
            }
        }
        else
        {
            _log.Warn(Source, "CancelHibernate called but no active timers found.");
        }
    }

    private void StartBatteryMonitoring_NoLock()
    {
        _batteryPollTimer?.Dispose();

        _batteryPollTimer = new Timer(_ =>
        {
            try
            {
                var pct = GetBatteryPercent();

                if (pct < 0f)
                {
                    _log.Warn(Source, "Battery percent unavailable.");
                    return;
                }

                _log.Info(Source,
                    $"Battery percent polled. Percent={pct:P2}, Threshold={BATTERY_THRESHOLD:P0}");

                if (pct < BATTERY_THRESHOLD)
                {
                    _log.Warn(Source,
                        $"Battery below threshold. Percent={pct:P2}, Threshold={BATTERY_THRESHOLD:P0}");

                    lock (_sync)
                    {
                        CancelAll_NoLock("Battery below threshold");
                        _onBattery = false;
                    }

                    DoHibernateOnce();
                }
            }
            catch (Exception ex)
            {
                _log.Error(Source, "Battery monitoring callback failed", ex);
            }
        }, null, dueTime: 0, period: BATTERY_POLL_MS);
    }

    private float GetBatteryPercent()
    {
        try
        {
            using var searcher =
                new ManagementObjectSearcher(
                    "SELECT EstimatedChargeRemaining FROM Win32_Battery");

            using var results = searcher.Get();

            foreach (ManagementObject obj in results)
            {
                var value = obj["EstimatedChargeRemaining"];

                if (value != null &&
                    int.TryParse(value.ToString(), out int percent))
                {
                    return percent / 100f;
                }
            }

            return -1f;
        }
        catch (Exception ex)
        {
            _log.Error(Source, "Failed to get battery percent", ex);
            return -1f;
        }
    }

    private bool HasActiveUserSession()
    {
        try
        {
            var hasUnlockedSession = _sessionLauncher.HasUnlockedActiveUserSession();

            if (hasUnlockedSession)
            {
                _log.Info(Source, "Unlocked active user session found.");
                return true;
            }

            _log.Warn(Source, "No unlocked active user session found.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(Source, "Failed to check unlocked active user session", ex);
            return false;
        }
    }

    private void SendNotifyCommand(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.None);

            client.Connect(1500);

            using var writer = new StreamWriter(client, Encoding.UTF8)
            {
                AutoFlush = true
            };

            writer.WriteLine(command);

            _log.Info(Source, $"Notify command sent: {command}");
        }
        catch (Exception ex)
        {
            _log.Error(Source, $"Failed to send notify command: {command}", ex);
        }
    }

    private void CancelAll_NoLock(string reason)
    {
        _log.Info(Source, $"CancelAll_NoLock. Reason={reason}");

        _hibernateTimer?.Dispose();
        _hibernateTimer = null;

        _batteryPollTimer?.Dispose();
        _batteryPollTimer = null;
    }

    private void DoHibernateOnce()
    {
        var previous = Interlocked.Exchange(ref _hibernateRequestedOnce, 1);

        if (previous != 0)
        {
            _log.Warn(Source, "Hibernate skipped because it was already requested.");
            return;
        }

        DoHibernate();
    }

    private void DoHibernate()
    {
        _log.Warn(Source, "Hibernate requested.");

        try
        {
            Process.Start("powercfg", "/h on");
        }
        catch (Exception ex)
        {
            _log.Error(Source, "powercfg /h on failed", ex);
        }

        try
        {
            Process.Start("shutdown", "/h");
        }
        catch (Exception ex)
        {
            _log.Error(Source, "shutdown /h failed", ex);
        }
    }
}