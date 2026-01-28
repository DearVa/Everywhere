using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

public class JsonElementMessagePackFormatter : IMessagePackFormatter<JsonElement>
{
    public void Serialize(ref MessagePackWriter writer, JsonElement value, MessagePackSerializerOptions options)
    {
        writer.Write(value.GetRawText());
    }

    public JsonElement Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var json = reader.ReadString() ?? "{}";
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}