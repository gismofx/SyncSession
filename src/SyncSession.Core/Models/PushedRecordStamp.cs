using System;

namespace SyncSession.Core.Models;

/// <summary>
/// Identifies one local row exactly as it was when a push read it: its id and the
/// <c>ModifiedAtUtc</c> it carried at that moment.
/// </summary>
/// <remarks>
/// After the server commits the push, the row is marked clean only if it still carries this
/// <see cref="ModifiedAtUtc"/>. A row saved again while the push was in flight has a newer value,
/// so it stays dirty and goes out on the next push instead of being forgotten.
/// </remarks>
/// <param name="Id">The row's primary key.</param>
/// <param name="ModifiedAtUtc">The row's <c>ModifiedAtUtc</c> when the push read it.</param>
public readonly record struct PushedRecordStamp(Guid Id, DateTime? ModifiedAtUtc);
