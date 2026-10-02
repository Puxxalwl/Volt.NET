using Volt;

namespace Volt.E2E.Components;

/// <summary>M6 dogfood: [VoltForm] gets a generated reflection-free binder + validator.</summary>
[VoltForm]
public sealed partial class NewsletterForm
{
    [VoltRequired(Message = "email is required"), VoltEmail]
    public string Email { get; set; } = "";

    [VoltRange(1, 5)]
    public int? Rating { get; set; }

    public bool Subscribe { get; set; }

    [VoltMaxLength(20)]
    public string? Name { get; set; }
}
