using System.Text.Json;
using System.Text.Json.Serialization;

namespace Game.Api.Messaging;

/// <summary>How match events are written to and read from the queue: enums by name, so a message
/// stays readable in the RabbitMQ console and survives enum members being reordered.</summary>
public static class MatchEventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
