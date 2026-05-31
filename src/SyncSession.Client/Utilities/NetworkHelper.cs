using System;
using System.Net.NetworkInformation;

namespace SyncSession.Client.Utilities;

/// <summary>
/// Helper for checking network connectivity.
/// </summary>
public class NetworkHelper
{
    /// <summary>
    /// Returns <c>true</c> if any network interface is available.
    /// </summary>
    public bool IsNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the specified host is reachable via ICMP ping.
    /// </summary>
    /// <param name="host">Host to ping (default: <c>8.8.8.8</c>).</param>
    /// <param name="timeout">Timeout in milliseconds (default: 3000).</param>
    public bool IsInternetReachable(string host = "8.8.8.8", int timeout = 3000)
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(host, timeout);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}
