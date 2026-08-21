using System.ComponentModel.DataAnnotations;

namespace Ago.Platform.Messaging.RabbitMq;

/// <summary>
/// Bound from <c>Messaging:RabbitMq:*</c> (`naming-and-structure.md`'s naming example) and validated
/// at startup - a typo in a key must fail the pod, not silently disable durability.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "Messaging:RabbitMq";

    [Required]
    public string HostName { get; set; } = "";

    public int Port { get; set; } = 5672;

    [Required]
    public string UserName { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";

    public string VirtualHost { get; set; } = "/";

    public ushort PrefetchCount { get; set; } = 50;
}
