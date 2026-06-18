using System.Net;

namespace UPSGuard.Service;

public sealed class HealthIpAllowlist
{
    private readonly HashSet<IPAddress> _allowedIps;

    public HealthIpAllowlist()
    {
        _allowedIps = new HashSet<IPAddress>
        {
            Normalize(IPAddress.Loopback),                     // 127.0.0.1
            Normalize(IPAddress.IPv6Loopback),                // ::1

            Normalize(IPAddress.Parse("172.17.8.191")),
            Normalize(IPAddress.Parse("172.17.8.159")),
            Normalize(IPAddress.Parse("10.10.10.25"))
        };
    }

    public bool IsAllowed(IPAddress? ip)
    {
        if (ip is null)
            return false;

        return _allowedIps.Contains(Normalize(ip));
    }

    public IReadOnlyCollection<IPAddress> GetAll() => _allowedIps;

    private static IPAddress Normalize(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            return ip.MapToIPv4();

        return ip;
    }
}