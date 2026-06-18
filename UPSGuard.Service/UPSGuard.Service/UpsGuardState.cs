namespace UPSGuard.Service;

public sealed class UpsGuardState
{
    private readonly object _sync = new();

    private bool _isStarted;
    private bool _isHealthy = true;
    private string? _lastError;
    private DateTimeOffset _lastUpdateUtc = DateTimeOffset.UtcNow;

    public bool IsStarted
    {
        get
        {
            lock (_sync) return _isStarted;
        }
        set
        {
            lock (_sync) _isStarted = value;
        }
    }

    public bool IsHealthy
    {
        get
        {
            lock (_sync) return _isHealthy;
        }
        set
        {
            lock (_sync) _isHealthy = value;
        }
    }

    public string? LastError
    {
        get
        {
            lock (_sync) return _lastError;
        }
        set
        {
            lock (_sync) _lastError = value;
        }
    }

    public DateTimeOffset LastUpdateUtc
    {
        get
        {
            lock (_sync) return _lastUpdateUtc;
        }
        set
        {
            lock (_sync) _lastUpdateUtc = value;
        }
    }

    public void SetHealthy()
    {
        lock (_sync)
        {
            _isStarted = true;
            _isHealthy = true;
            _lastError = null;
            _lastUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetUnhealthy(string? error)
    {
        lock (_sync)
        {
            _isStarted = true;
            _isHealthy = false;
            _lastError = error;
            _lastUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    public void SetStopping()
    {
        lock (_sync)
        {
            _isHealthy = false;
            _lastError = "Service stopping";
            _lastUpdateUtc = DateTimeOffset.UtcNow;
        }
    }
}