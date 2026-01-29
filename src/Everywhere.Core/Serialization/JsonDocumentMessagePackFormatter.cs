using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

public class JsonDocumentMessagePackFormatter : IMessagePackFormatter<JsonDocument?>
{
    public void Serialize(ref MessagePackWriter writer, JsonDocument? value, MessagePackSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNil();
            return;
        }

        writer.Write(value.RootElement.GetRawText());
    }

    public JsonDocument? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        var json = reader.ReadString() ?? "{}";
        return JsonDocument.Parse(json);
    }
}