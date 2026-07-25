using System.Reflection;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Chat.Plugins.BuiltIn;
using Everywhere.Chat.Plugins.BuiltIn.FileSystem;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.I18N;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

public class FileSystemPluginTests
{
    [TestCase("Copy", true)]
    [TestCase("Move", false)]
    public async Task TransferFileAsync_ForFile_PerformsRequestedOperation(string operation, bool sourceShouldRemain)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source.txt");
        var destination = Path.Combine(root, "nested", "destination.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "content");

        try
        {
            var plugin = CreatePlugin();
            var userInterface = CreateUserInterface(consent: true);

            await InvokeTransferFileAsync(plugin, userInterface, source, destination, operation);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(source), Is.EqualTo(sourceShouldRemain));
                Assert.That(File.ReadAllText(destination), Is.EqualTo("content"));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TransferFileAsync_CopyDirectory_CopiesNestedContentAndKeepsSource()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        var sourceFile = Path.Combine(source, "nested", "content.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "content");

        try
        {
            var plugin = CreatePlugin();
            var userInterface = CreateUserInterface(consent: true);

            await InvokeTransferFileAsync(plugin, userInterface, source, destination, "Copy");

            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(source), Is.True);
                Assert.That(
                    File.ReadAllText(Path.Combine(destination, "nested", "content.txt")),
                    Is.EqualTo("content"));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public void WriteToFileAsync_DeniedConsent_DoesNotCreateFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var userInterface = CreateUserInterface(consent: false);
        var plugin = CreatePlugin();

        var method = typeof(FileSystemPlugin).GetMethod(
            "WriteToFileAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        var task = (Task)method!.Invoke(
            plugin,
            [userInterface, new ChatContext(), path, "content", false, CancellationToken.None])!;

        Assert.ThrowsAsync<HandledException>(async () => await task);
        Assert.That(File.Exists(path), Is.False);
    }

    private static FileSystemPlugin CreatePlugin() =>
        new(
            new Settings(Substitute.For<IServiceProvider>()),
            new FileHandlerContextFactory([new PdfFileHandler(), new TextFileHandler(), new BinaryFileHandler()]),
            Substitute.For<ILogger<FileSystemPlugin>>());

    private static IChatPluginUserInterface CreateUserInterface(bool consent)
    {
        var userInterface = Substitute.For<IChatPluginUserInterface>();
        userInterface.DisplaySink.Returns(new ChatPluginDisplaySink());
        userInterface.RequestConsentAsync(
                Arg.Any<string?>(),
                Arg.Any<IDynamicLocaleKey>(),
                Arg.Any<ChatPluginDisplayBlock?>(),
                Arg.Any<RequestConsentRememberMasks>(),
                Arg.Any<IReadOnlyList<RequestConsentCustomOption>?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RequestConsentResult(consent, consent ? null : "denied")));
        return userInterface;
    }

    private static async Task InvokeTransferFileAsync(
        FileSystemPlugin plugin,
        IChatPluginUserInterface userInterface,
        string source,
        string destination,
        string operation)
    {
        var method = typeof(FileSystemPlugin).GetMethod(
            "TransferFileAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);

        var operationType = method!
            .GetParameters()
            .Single(static parameter => parameter.Name is "operation")
            .ParameterType;
        var operationValue = Enum.Parse(operationType, operation);
        var task = (Task)method.Invoke(
            plugin,
            [
                userInterface,
                new ChatContext(),
                source,
                destination,
                operationValue,
                CancellationToken.None
            ])!;
        await task;
    }
}
