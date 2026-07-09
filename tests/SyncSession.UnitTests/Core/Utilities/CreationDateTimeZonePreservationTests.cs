using System;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using SyncSession.Core.Utilities;
using Xunit;

namespace SyncSession.UnitTests.Core.Utilities;

/// <summary>
/// Reproduction + characterization tests for the CreationDate timezone-shift bug (Session 40).
///
/// Root cause: EntityReflectionHelper.UnwrapJsonElement(JsonElement) - the untyped overload used
/// by both server temp-insert paths (MySqlServerDatabase / SqliteServerDatabase ->
/// UnwrapJsonElements) and by DataController.Query - coerces any ISO-8601 string via
/// `dto.UtcDateTime`, converting it to UTC using the SERVER's local offset.
///
/// Production server runs US Central (UTC-5), so every pushed DateTime is stored +5h.
///
/// Requirement: business dates (DVMApp History.CreationDate) must survive as pure, timezone-free
/// wall-clock values. ModifiedAtUtc is written client-side as DateTime.UtcNow and must be stored
/// as-is.
///
/// Tests in "The Bug" region are expected to FAIL until UnwrapJsonElement stops converting.
/// Tests in the characterization regions are expected to PASS both before and after the fix -
/// they are the regression guard proving the fix changes nothing else.
/// </summary>
public class CreationDateTimeZonePreservationTests
{
    // The exact wall-clock the veterinarian entered: 2026-07-06 7:30:15 PM.
    private static readonly DateTime EnteredWallClock = new(2026, 7, 6, 19, 30, 15, DateTimeKind.Unspecified);

    #region The Bug - these MUST FAIL against current code

    [Fact]
    public void NoOffsetString_OnCentralServer_PreservesWallClock()
    {
        // Production wire shape: client serializes via ToString("s") -> no offset, no 'Z'.
        // Server is US Central (UTC-5): TryGetDateTimeOffset assumes the server's local zone.
        using (ForceLocalTimeZone(-5))
        {
            var result = Unwrap("2026-07-06T19:30:15");

            WallClockOf(result).Should().Be(EnteredWallClock,
                "CreationDate must be stored exactly as entered, with no timezone conversion");
        }
    }

    [Fact]
    public void NoOffsetString_OnIndiaServer_PreservesWallClock()
    {
        // Zone-independence: the stored value must not depend on where the server runs.
        using (ForceLocalTimeZone(5.5))
        {
            var result = Unwrap("2026-07-06T19:30:15");

            WallClockOf(result).Should().Be(EnteredWallClock,
                "the stored wall-clock must not depend on the server's timezone");
        }
    }

    [Fact]
    public void ExplicitOffsetString_NormalizedToUtc()
    {
        // When the client DOES state a timezone, honor it. This is an intentional contract,
        // asserted by HttpPushSerializationTests: "offset datetime should be stored as UTC".
        // Only offset-LESS strings are wall-clock; the server must never invent an offset.
        var result = Unwrap("2026-07-06T19:30:15-05:00");

        WallClockOf(result).Should().Be(new DateTime(2026, 7, 7, 0, 30, 15),
            "an explicit offset was supplied, so the value normalizes to UTC");
    }

    [Fact]
    public void FractionalSeconds_PreservedWithoutShift()
    {
        using (ForceLocalTimeZone(-5))
        {
            var result = Unwrap("2026-07-06T19:30:15.1234567");

            WallClockOf(result).Should().Be(new DateTime(2026, 7, 6, 19, 30, 15).AddTicks(1234567),
                "sub-second precision must survive unshifted");
        }
    }

    [Fact]
    public void ModifiedAtUtc_ClientUtcNow_StoredAsIs()
    {
        // Client writes ModifiedAtUtc = DateTime.UtcNow (EntityManagerBase.cs:78), then serializes
        // with ToString("s") which DROPS the 'Z'. The server must store that UTC value as-is.
        // Production: true 2026-07-06 23:30:22Z was stored as 2026-07-07 04:30:22 (+5h) - i.e.
        // 3h34m AFTER the session that carried it committed (00:55:45Z). Impossible by construction.
        using (ForceLocalTimeZone(-5))
        {
            var result = Unwrap("2026-07-06T23:30:22");

            WallClockOf(result).Should().Be(new DateTime(2026, 7, 6, 23, 30, 22),
                "a client-supplied UTC timestamp must not be shifted again by the server");
        }
    }

    [Fact]
    public void ModifiedAtUtc_StoredValue_PrecedesSessionCommit()
    {
        // The decisive production anomaly, expressed as an invariant: a record's ModifiedAtUtc
        // can never be later than the CommittedAtUtc of the session that pushed it.
        var sessionCommittedAtUtc = new DateTime(2026, 7, 7, 0, 55, 45);

        using (ForceLocalTimeZone(-5))
        {
            var stored = WallClockOf(Unwrap("2026-07-06T23:30:22"));

            stored.Should().BeBefore(sessionCommittedAtUtc,
                "a record cannot be modified after the sync session that carried it committed");
        }
    }

    [Fact]
    public void ZuluString_AlreadyPreserved_GuardAgainstRegression()
    {
        // A 'Z'-suffixed string has offset 0, so dto.UtcDateTime is already a no-op.
        // This PASSES today and must keep passing: it proves the fix doesn't break UTC inputs.
        // Note: the client does NOT send 'Z' (ToString("s") drops it) - which is why the bug bites.
        using (ForceLocalTimeZone(-5))
        {
            var result = Unwrap("2026-07-06T23:30:22Z");

            WallClockOf(result).Should().Be(new DateTime(2026, 7, 6, 23, 30, 22));
        }
    }

    #endregion

    #region Characterization - text values must pass through untouched (expected PASS)

    [Theory]
    // Does a date embedded in prose get coerced? It must not.
    [InlineData("saw mr so and so on 7/15/21 and we started him on bute")]
    [InlineData("Recheck 7/6/2026, owner will call")]
    [InlineData("7/6/2026")]              // US format - not ISO 8601
    [InlineData("07/06/2026 19:30")]      // US format with time
    [InlineData("2026-07-06 19:30:15")]   // space separator instead of 'T'
    [InlineData("Ultrasound Examination - Follow up")]
    [InlineData("usf")]
    [InlineData("hello")]
    [InlineData("not-a-date")]
    [InlineData("")]
    public void NonIso8601Strings_ReturnedUnchangedAsString(string value)
    {
        using (ForceLocalTimeZone(-5))
        {
            var result = Unwrap(value);

            result.Should().BeOfType<string>("free text must never be coerced to a DateTime");
            result.Should().Be(value, "the exact text must be preserved");
        }
    }

    [Fact]
    public void GuidString_ReturnedUnchangedAsString()
    {
        var guid = "40086b45-c616-4d89-9500-99c78a4663ea";

        using (ForceLocalTimeZone(-5))
        {
            Unwrap(guid).Should().Be(guid);
        }
    }

    #endregion

    #region Known limitation - a bare ISO-8601 date IS coerced (documents residual behavior)

    [Fact]
    public void BareIsoDateString_IsCoercedToDateTime_KnownLimitation()
    {
        // The untyped overload has no target type, so it must guess. A text column whose ENTIRE
        // value is a valid ISO-8601 date is indistinguishable from a real date and gets coerced.
        // This documents the behavior; it is NOT fixed by the timezone fix. Complete resolution
        // requires routing through the typed overload using column metadata (tracked separately).
        using (ForceLocalTimeZone(-5))
        {
            var result = Unwrap("2026-07-06");

            result.Should().NotBeOfType<string>(
                "documented limitation: a bare ISO date in a text column is coerced to DateTime");
        }
    }

    #endregion

    #region Non-string kinds unchanged (expected PASS)

    [Fact]
    public void NonStringKinds_Unchanged()
    {
        using (ForceLocalTimeZone(-5))
        {
            EntityReflectionHelper.UnwrapJsonElement(Parse("42")).Should().Be(42L);
            EntityReflectionHelper.UnwrapJsonElement(Parse("3.14")).Should().Be(3.14);
            EntityReflectionHelper.UnwrapJsonElement(Parse("true")).Should().Be(true);
            EntityReflectionHelper.UnwrapJsonElement(Parse("false")).Should().Be(false);
            EntityReflectionHelper.UnwrapJsonElement(Parse("null")).Should().BeNull();
        }
    }

    #endregion

    // -- helpers --------------------------------------------------------------

    private static JsonElement Parse(string rawJson)
        => JsonSerializer.Deserialize<JsonElement>(rawJson);

    /// <summary>Unwraps a JSON *string* value through the untyped overload under test.</summary>
    private static object? Unwrap(string stringValue)
        => EntityReflectionHelper.UnwrapJsonElement(
            Parse(JsonSerializer.Serialize(stringValue)));

    /// <summary>
    /// Interprets the result as a timezone-free wall-clock, independent of whether the
    /// implementation returns a DateTime or a raw ISO string (keeps these tests fix-agnostic).
    /// </summary>
    private static DateTime WallClockOf(object? result) => result switch
    {
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified),
        DateTimeOffset dto => dto.DateTime,
        string s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        _ => throw new InvalidOperationException(
            $"Unexpected unwrap result type: {result?.GetType().Name ?? "null"}")
    };

    /// <summary>
    /// Overrides TimeZoneInfo.Local for the scope with a fixed-offset zone, so that parsing of
    /// offset-less date strings is deterministic regardless of the host machine's timezone.
    /// </summary>
    private static IDisposable ForceLocalTimeZone(double offsetHours)
        => new LocalTimeZoneOverride(TimeZoneInfo.CreateCustomTimeZone(
            $"TEST_UTC{offsetHours}", TimeSpan.FromHours(offsetHours),
            "Test fixed-offset zone", "Test fixed-offset zone"));

    private sealed class LocalTimeZoneOverride : IDisposable
    {
        private readonly object _cachedData;
        private readonly FieldInfo _localField;
        private readonly TimeZoneInfo _original;

        public LocalTimeZoneOverride(TimeZoneInfo tz)
        {
            var cachedDataField = typeof(TimeZoneInfo).GetField(
                "s_cachedData", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("TimeZoneInfo.s_cachedData not found.");
            _cachedData = cachedDataField.GetValue(null)!;
            _localField = _cachedData.GetType().GetField(
                "_localTimeZone", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("CachedData._localTimeZone not found.");

            _original = TimeZoneInfo.Local;
            _localField.SetValue(_cachedData, tz);
        }

        public void Dispose() => _localField.SetValue(_cachedData, _original);
    }
}
