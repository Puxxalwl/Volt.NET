using Volt;

namespace Volt.Demo;

[VoltForm]
public partial class SignupForm
{
    [VoltRequired, VoltEmail]
    public string Email { get; set; } = "";
}
