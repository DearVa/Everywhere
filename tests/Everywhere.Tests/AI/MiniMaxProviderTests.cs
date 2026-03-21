using Everywhere.AI;

namespace Everywhere.Tests.AI;

[TestFixture]
public class MiniMaxProviderTests
{
    private ModelProviderTemplate _minimaxTemplate = null!;

    [SetUp]
    public void SetUp()
    {
        // Access the MiniMax provider template via reflection since ModelProviderTemplates is private
        var property = typeof(CustomAssistant).GetProperty(
            "ModelProviderTemplates",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.That(property, Is.Not.Null, "ModelProviderTemplates property should exist");

        var templates = (ModelProviderTemplate[])property!.GetValue(null)!;
        _minimaxTemplate = templates.First(t => t.Id == "minimax");
    }

    [Test]
    public void MiniMaxTemplate_ShouldExist()
    {
        Assert.That(_minimaxTemplate, Is.Not.Null);
    }

    [Test]
    public void MiniMaxTemplate_ShouldHaveCorrectDisplayName()
    {
        Assert.That(_minimaxTemplate.DisplayName, Is.EqualTo("MiniMax"));
    }

    [Test]
    public void MiniMaxTemplate_ShouldUseOpenAISchema()
    {
        Assert.That(_minimaxTemplate.Schema, Is.EqualTo(ModelProviderSchema.OpenAI));
    }

    [Test]
    public void MiniMaxTemplate_ShouldHaveCorrectEndpoint()
    {
        Assert.That(_minimaxTemplate.Endpoint, Is.EqualTo("https://api.minimax.io/v1"));
    }

    [Test]
    public void MiniMaxTemplate_ShouldHaveOfficialWebsiteUrl()
    {
        Assert.That(_minimaxTemplate.OfficialWebsiteUrl, Is.EqualTo("https://www.minimaxi.com"));
    }

    [Test]
    public void MiniMaxTemplate_ShouldHaveIconUrls()
    {
        using var scope = Assert.EnterMultipleScope();
        Assert.That(_minimaxTemplate.DarkIconUrl, Is.Not.Null.And.Not.Empty);
        Assert.That(_minimaxTemplate.LightIconUrl, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void MiniMaxTemplate_ShouldHaveThreeModels()
    {
        Assert.That(_minimaxTemplate.ModelDefinitions, Has.Count.EqualTo(3));
    }

    [Test]
    public void MiniMaxTemplate_M27_ShouldBeDefault()
    {
        var m27 = _minimaxTemplate.ModelDefinitions.First(m => m.Id == "MiniMax-M2.7");
        Assert.That(m27.IsDefault, Is.True);
    }

    [Test]
    public void MiniMaxTemplate_M27_ShouldHaveCorrectProperties()
    {
        var m27 = _minimaxTemplate.ModelDefinitions.First(m => m.Id == "MiniMax-M2.7");

        using var scope = Assert.EnterMultipleScope();
        Assert.That(m27.ModelId, Is.EqualTo("MiniMax-M2.7"));
        Assert.That(m27.DisplayName, Is.EqualTo("MiniMax M2.7"));
        Assert.That(m27.MaxTokens, Is.EqualTo(1_000_000));
        Assert.That(m27.IsImageInputSupported, Is.False);
        Assert.That(m27.IsFunctionCallingSupported, Is.True);
        Assert.That(m27.IsDeepThinkingSupported, Is.True);
    }

    [Test]
    public void MiniMaxTemplate_M27Highspeed_ShouldHaveCorrectProperties()
    {
        var m27hs = _minimaxTemplate.ModelDefinitions.First(m => m.Id == "MiniMax-M2.7-highspeed");

        using var scope = Assert.EnterMultipleScope();
        Assert.That(m27hs.ModelId, Is.EqualTo("MiniMax-M2.7-highspeed"));
        Assert.That(m27hs.DisplayName, Is.EqualTo("MiniMax M2.7 Highspeed"));
        Assert.That(m27hs.MaxTokens, Is.EqualTo(1_000_000));
        Assert.That(m27hs.IsFunctionCallingSupported, Is.True);
        Assert.That(m27hs.IsDeepThinkingSupported, Is.True);
        Assert.That(m27hs.IsDefault, Is.False);
    }

    [Test]
    public void MiniMaxTemplate_M25Highspeed_ShouldHaveCorrectProperties()
    {
        var m25hs = _minimaxTemplate.ModelDefinitions.First(m => m.Id == "MiniMax-M2.5-highspeed");

        using var scope = Assert.EnterMultipleScope();
        Assert.That(m25hs.ModelId, Is.EqualTo("MiniMax-M2.5-highspeed"));
        Assert.That(m25hs.DisplayName, Is.EqualTo("MiniMax M2.5 Highspeed"));
        Assert.That(m25hs.MaxTokens, Is.EqualTo(204_800));
        Assert.That(m25hs.IsFunctionCallingSupported, Is.True);
        Assert.That(m25hs.IsDeepThinkingSupported, Is.False);
        Assert.That(m25hs.IsDefault, Is.False);
    }

    [Test]
    public void MiniMaxEndpoint_ShouldNormalizeCorrectly()
    {
        // MiniMax uses OpenAI schema, so endpoint normalization should follow OpenAI rules
        var normalized = ModelProviderSchema.OpenAI.NormalizeEndpoint("https://api.minimax.io/v1");
        Assert.That(normalized, Is.EqualTo("https://api.minimax.io/v1"));
    }

    [Test]
    public void MiniMaxEndpoint_WithoutVersion_ShouldAppendV1()
    {
        var normalized = ModelProviderSchema.OpenAI.NormalizeEndpoint("https://api.minimax.io");
        Assert.That(normalized, Is.EqualTo("https://api.minimax.io/v1"));
    }

    [Test]
    public void MiniMaxEndpoint_PreviewEndpoint_ShouldBeChatCompletions()
    {
        var preview = ModelProviderSchema.OpenAI.PreviewEndpoint("https://api.minimax.io/v1");
        Assert.That(preview, Is.EqualTo("https://api.minimax.io/v1/chat/completions"));
    }

    [Test]
    public void SiliconCloud_ShouldHaveMiniMaxM27Model()
    {
        var property = typeof(CustomAssistant).GetProperty(
            "ModelProviderTemplates",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var templates = (ModelProviderTemplate[])property!.GetValue(null)!;
        var siliconcloud = templates.First(t => t.Id == "siliconcloud");

        var minimaxModel = siliconcloud.ModelDefinitions.FirstOrDefault(m => m.Id == "MiniMaxAI/MiniMax-M2.7");
        Assert.That(minimaxModel, Is.Not.Null, "SiliconCloud should have MiniMax M2.7 model");
        Assert.That(minimaxModel!.MaxTokens, Is.EqualTo(1_000_000));
    }

    [Test]
    public void AllProviderTemplates_ShouldHaveUniqueIds()
    {
        var property = typeof(CustomAssistant).GetProperty(
            "ModelProviderTemplates",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var templates = (ModelProviderTemplate[])property!.GetValue(null)!;

        var ids = templates.Select(t => t.Id).ToList();
        Assert.That(ids, Is.Unique, "All provider template IDs should be unique");
    }

    [Test]
    public void MiniMaxModelIds_ShouldBeUnique()
    {
        var ids = _minimaxTemplate.ModelDefinitions.Select(m => m.Id).ToList();
        Assert.That(ids, Is.Unique);
    }
}
