using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bifrost.Contracts.Internal.Events;

public sealed class NanosecondStringConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            return str is not null ? long.Parse(str) : null;
        }

        return reader.GetInt64();
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value.ToString());
    }
}
