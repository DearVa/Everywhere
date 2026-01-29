using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

public abstract class FunctionContentMessagePackFormatter<T> : IMessagePackFormatter<T?> where T : class
{
    public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        SerializeCore(ref writer, value, options);
    }

    public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        if (reader.NextMessagePackType != MessagePackType.Array)
        {
            return LegacyDeserializeCore(ref reader, options);
        }

        return DeserializeCore(ref reader, options);
    }

    protected abstract void SerializeCore(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options);

    protected abstract T DeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options);

    protected abstract T LegacyDeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options);

    protected static void SerializeDictionary(
        ref MessagePackWriter writer,
        IReadOnlyDictionary<string, object?>? value,
        MessagePackSerializerOptions options)
    {
        writer.WriteMapHeader(value?.Count ?? 0);
        if (value is null) return;

        foreach (var (key, val) in value)
        {
            writer.Write(key);

            if (val is null)
            {
                writer.WriteNil();
            }
            else
            {
                var type = val.GetType();
                if (!TypeHelper.TypeCodes.TryGetValue(type, out var typeCode))
                {
                    throw new MessagePackSerializationException($"Unsupported dictionary value type: {type.FullName}");
                }

                writer.WriteArrayHeader(2);
                writer.Write(typeCode);
                MessagePackSerializer.Serialize(type, ref writer, val, options);
            }
        }
    }

    protected static Dictionary<string, object?>? DeserializeDictionary(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        var count = reader.ReadMapHeader();
        if (count == 0) return null;

        var dict = new Dictionary<string, object?>(count);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString() ?? throw new MessagePackSerializationException("Dictionary key cannot be null.");

            reader.ReadArrayHeader();
            var typeCode = reader.ReadInt32();
            if (typeCode == 0 || !TypeHelper.CodeTypes.TryGetValue(typeCode, out var targetType))
            {
                throw new MessagePackSerializationException($"Unsupported dictionary value type code: {typeCode}");
            }

            var val = MessagePackSerializer.Deserialize(targetType, ref reader, options);
            dict[key] = val;
        }

        return dict;
    }
}

file static class TypeHelper
{
    public static readonly IReadOnlyDictionary<Type, int> TypeCodes = new Dictionary<Type, int>(8)
    {
        { typeof(string), 1 },
        { typeof(int), 2 },
        { typeof(long), 3 },
        { typeof(float), 4 },
        { typeof(double), 5 },
        { typeof(bool), 6 },
        { typeof(byte[]), 7 },
        { typeof(JsonElement), 8 },
    };

    public static readonly IReadOnlyDictionary<int, Type> CodeTypes = TypeCodes.ToDictionary(kv => kv.Value, kv => kv.Key);
}