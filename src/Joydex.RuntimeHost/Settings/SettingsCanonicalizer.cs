using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Joydex.Contracts;
using Joydex.Core.Config;

namespace Joydex.RuntimeHost.Settings;

internal static class SettingsCanonicalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static SettingsBundle Normalize(SettingsBundle value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SettingsBundle(
            CompanionConfigNormalizer.Normalize(value.Companion),
            value.Voice.Normalize(),
            value.PebbleIndex.Normalize(),
            value.TaskAlerts.Normalize());
    }

    public static SettingsBundle Clone(SettingsBundle value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Normalize(value), JsonOptions);
        return JsonSerializer.Deserialize<SettingsBundle>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("A settings bundle could not be cloned.");
    }

    public static SettingsBundle Apply(SettingsBundle current, SettingsPatch patch) => Normalize(new SettingsBundle(
        patch.Companion ?? current.Companion,
        patch.Voice ?? current.Voice,
        patch.PebbleIndex ?? current.PebbleIndex,
        patch.TaskAlerts ?? current.TaskAlerts));

    public static string[] Validate(SettingsBundle value, IEnumerable<SettingsAggregateId> changed)
    {
        var errors = new List<string>();
        foreach (var aggregate in changed)
        {
            try
            {
                switch (aggregate)
                {
                    case SettingsAggregateId.Companion:
                        errors.AddRange(ConfigValidator.Validate(value.Companion));
                        break;
                    case SettingsAggregateId.Voice:
                        errors.AddRange(value.Voice.Validate());
                        break;
                    case SettingsAggregateId.PebbleIndex:
                        errors.AddRange(value.PebbleIndex.Validate());
                        break;
                    case SettingsAggregateId.TaskAlerts:
                        _ = value.TaskAlerts.Normalize();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(aggregate));
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                                                   or InvalidDataException
                                                   or NullReferenceException
                                                   or OverflowException)
            {
                errors.Add(exception.Message);
            }
        }

        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static SettingsAggregateId[] Changed(SettingsBundle current, SettingsBundle candidate) =>
        Enum.GetValues<SettingsAggregateId>()
            .Where(aggregate => !string.Equals(
                HashAggregate(current, aggregate),
                HashAggregate(candidate, aggregate),
                StringComparison.Ordinal))
            .ToArray();

    public static string HashBundle(SettingsBundle value) => Hash(value);

    public static string HashAggregate(SettingsBundle value, SettingsAggregateId aggregate) => aggregate switch
    {
        SettingsAggregateId.Companion => Hash(value.Companion),
        SettingsAggregateId.Voice => Hash(value.Voice),
        SettingsAggregateId.PebbleIndex => Hash(value.PebbleIndex),
        SettingsAggregateId.TaskAlerts => Hash(value.TaskAlerts),
        _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
    };

    public static string HashToken(string token) => Convert.ToHexString(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    public static SettingsBundle ReplaceAggregate(
        SettingsBundle current,
        SettingsBundle source,
        SettingsAggregateId aggregate) => Normalize(aggregate switch
        {
            SettingsAggregateId.Companion => current with { Companion = source.Companion },
            SettingsAggregateId.Voice => current with { Voice = source.Voice },
            SettingsAggregateId.PebbleIndex => current with { PebbleIndex = source.PebbleIndex },
            SettingsAggregateId.TaskAlerts => current with { TaskAlerts = source.TaskAlerts },
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
        });

    private static string Hash<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, JsonOptions)
            ?? throw new InvalidOperationException("A settings value could not be serialized.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, node);
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject jsonObject:
                writer.WriteStartObject();
                foreach (var property in jsonObject.OrderBy(property => property.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray jsonArray:
                writer.WriteStartArray();
                foreach (var item in jsonArray)
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer, JsonOptions);
                break;
        }
    }
}
