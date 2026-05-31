using System;

namespace SyncSession.Core.Exceptions;

/// <summary>
/// Exception thrown when concurrent synchronization is detected for a device.
/// </summary>
public class ConcurrentSyncException : SyncException
{
    public Guid DeviceId { get; }

    public ConcurrentSyncException(Guid deviceId) 
        : base($"Concurrent synchronization detected for device {deviceId}")
    {
        DeviceId = deviceId;
    }

    public ConcurrentSyncException(Guid deviceId, string message) 
        : base(message)
    {
        DeviceId = deviceId;
    }

    public ConcurrentSyncException(Guid deviceId, string message, Exception innerException) 
        : base(message, innerException)
    {
        DeviceId = deviceId;
    }
}
