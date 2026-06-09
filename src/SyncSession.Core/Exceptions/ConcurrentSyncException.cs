using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Exception thrown when concurrent synchronization is detected for a device.
/// </summary>
public class ConcurrentSyncException : SyncException
{
    /// <summary>The device ID that triggered the concurrent sync conflict.</summary>
    public Guid DeviceId { get; }

    /// <inheritdoc cref="SyncException(string)"/>
    /// <param name="deviceId">Device that attempted concurrent synchronization.</param>
    public ConcurrentSyncException(Guid deviceId) 
        : base($"Concurrent synchronization detected for device {deviceId}")
    {
        DeviceId = deviceId;
    }

    /// <inheritdoc cref="SyncException(string)"/>
    /// <param name="deviceId">Device that attempted concurrent synchronization.</param>
    /// <param name="message">Custom error message.</param>
    public ConcurrentSyncException(Guid deviceId, string message) 
        : base(message)
    {
        DeviceId = deviceId;
    }

    /// <inheritdoc cref="SyncException(string, Exception)"/>
    /// <param name="deviceId">Device that attempted concurrent synchronization.</param>
    /// <param name="message">Custom error message.</param>
    /// <param name="innerException">Inner exception.</param>
    public ConcurrentSyncException(Guid deviceId, string message, Exception innerException) 
        : base(message, innerException)
    {
        DeviceId = deviceId;
    }
}
