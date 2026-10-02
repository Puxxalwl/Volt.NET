namespace Volt;

/// <summary>
/// M6: marks a form class for Volt binder generation. The class must be
/// <c>partial</c>; the source generator extends it with a reflection-free,
/// AOT-safe binder + validator wired from POST fields and validation attributes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class VoltFormAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class VoltRequiredAttribute : Attribute
{
    /// <summary>Custom message (default: "required").</summary>
    public string Message { get; set; } = "required";
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class VoltEmailAttribute : Attribute
{
    /// <summary>Custom message (default: "must be an email address").</summary>
    public string Message { get; set; } = "must be an email address";
}

/// <summary>Numeric range check (inclusive).</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class VoltRangeAttribute : Attribute
{
    public VoltRangeAttribute(long min, long max) { Min = min; Max = max; }
    public long Min { get; }
    public long Max { get; }
    /// <summary>Custom message (default: "must be between {min} and {max}").</summary>
    public string? Message { get; set; }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class VoltMaxLengthAttribute : Attribute
{
    public VoltMaxLengthAttribute(int length) { Length = length; }
    public int Length { get; }
    /// <summary>Custom message (default: "too long").</summary>
    public string Message { get; set; } = "too long";
}

/// <summary>One validation error of a submitted form.</summary>
public readonly record struct VoltFormError(string Field, string Message);

/// <summary>
/// Implemented by generated form binders. <see cref="VoltBind"/> fills the
/// instance from the submitted POST fields and validates it — no reflection,
/// Native AOT safe.
/// </summary>
public interface IVoltForm
{
    /// <summary>Bind + validate; null errors means the form is valid.</summary>
    static abstract IVoltForm? VoltBind(IReadOnlyList<(string Name, string Value)> fields, out List<VoltFormError>? errors);
}
