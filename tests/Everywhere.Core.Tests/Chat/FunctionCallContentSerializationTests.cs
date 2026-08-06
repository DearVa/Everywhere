using MessagePack;
using Microsoft.SemanticKernel;

namespace Everywhere.Core.Tests.Chat;

public sealed class FunctionCallContentSerializationTests
{
    [Test]
    public void RoundTrip_WhenArgumentsContainNullValue_PreservesNullValue()
    {
        var source = new FunctionCallContent(
            "probe",
            arguments: new KernelArguments
            {
                ["optional"] = null
            });

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = MessagePackSerializer.Deserialize<FunctionCallContent>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(restored.Arguments, Is.Not.Null);
            Assert.That(restored.Arguments, Does.ContainKey("optional"));
            Assert.That(restored.Arguments!["optional"], Is.Null);
        });
    }

    [Test]
    public void RoundTrip_WhenMetadataContainsNullValue_PreservesNullValue()
    {
        var source = new FunctionCallContent("probe")
        {
            Metadata = new Dictionary<string, object?>
            {
                ["optional"] = null
            }
        };

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = MessagePackSerializer.Deserialize<FunctionCallContent>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(restored.Metadata, Is.Not.Null);
            Assert.That(restored.Metadata, Does.ContainKey("optional"));
            Assert.That(restored.Metadata!["optional"], Is.Null);
        });
    }
}
