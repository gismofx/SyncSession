using System;
using System.ComponentModel.DataAnnotations;

namespace SyncSession.Core.Validation;

/// <summary>
/// Validates that a <see cref="Guid"/> property is not <see cref="Guid.Empty"/>.
/// </summary>
/// <remarks>
/// The standard <c>[Required]</c> attribute does not work for <see cref="Guid"/> because it is a value
/// type that defaults to <see cref="Guid.Empty"/> rather than <c>null</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public class RequiredGuidAttribute : ValidationAttribute
{
    public RequiredGuidAttribute()
        : base("The {0} field is required and cannot be empty.")
    {
    }

    public override bool IsValid(object? value)
    {
        if (value is null)
            return false;

        if (value is Guid guidValue)
            return guidValue != Guid.Empty;

        return false;
    }
}
