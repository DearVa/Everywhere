// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.SemanticKernel.Connectors.Google.Core;

namespace Microsoft.SemanticKernel.Connectors.Google;

/// <summary>
/// Represents an Gemini function tool call with deserialized function name and arguments.
/// </summary>
public sealed class GeminiFunctionToolCall
{
    private string? _fullyQualifiedFunctionName;

    /// <summary>Initialize the <see cref="GeminiFunctionToolCall"/> from a <see cref="GeminiPart"/>.</summary>
    internal GeminiFunctionToolCall(GeminiPart part)
    {
        Verify.NotNull(part);
        Verify.NotNull(part.FunctionCall);
        Verify.NotNull(part.FunctionCall.FunctionName);

        var functionToolCall = part.FunctionCall;
        string fullyQualifiedFunctionName = functionToolCall.FunctionName;
        string functionName = fullyQualifiedFunctionName;
        string? pluginName = null;

        int separatorPos = fullyQualifiedFunctionName.IndexOf(GeminiFunction.NameSeparator, StringComparison.Ordinal);
        if (separatorPos >= 0)
        {
            pluginName = fullyQualifiedFunctionName.AsSpan(0, separatorPos).Trim().ToString();
            functionName = fullyQualifiedFunctionName.AsSpan(separatorPos + GeminiFunction.NameSeparator.Length).Trim().ToString();
        }

        this._fullyQualifiedFunctionName = fullyQualifiedFunctionName;
        this.PluginName = pluginName;
        this.FunctionName = functionName;
        this.ThoughtSignature = part.ThoughtSignature;
        if (functionToolCall.Arguments is not null)
        {
            this.Arguments = functionToolCall.Arguments.Deserialize<Dictionary<string, object?>>();
        }
    }

    /// <summary>Initialize the <see cref="GeminiFunctionToolCall"/> from a <see cref="GeminiPart.FunctionCallPart"/>.</summary>
    internal GeminiFunctionToolCall(GeminiPart.FunctionCallPart functionToolCall)
    {
        Verify.NotNull(functionToolCall);
        Verify.NotNull(functionToolCall.FunctionName);

        string fullyQualifiedFunctionName = functionToolCall.FunctionName;
        string functionName = fullyQualifiedFunctionName;
        string? pluginName = null;

        int separatorPos = fullyQualifiedFunctionName.IndexOf(GeminiFunction.NameSeparator, StringComparison.Ordinal);
        if (separatorPos >= 0)
        {
            pluginName = fullyQualifiedFunctionName.AsSpan(0, separatorPos).Trim().ToString();
            functionName = fullyQualifiedFunctionName.AsSpan(separatorPos + GeminiFunction.NameSeparator.Length).Trim().ToString();
        }

        this._fullyQualifiedFunctionName = fullyQualifiedFunctionName;
        this.PluginName = pluginName;
        this.FunctionName = functionName;
        if (functionToolCall.Arguments is not null)
        {
            this.Arguments = functionToolCall.Arguments.Deserialize<Dictionary<string, object?>>();
        }
    }

    /// <summary>Gets the name of the plugin with which this function is associated, if any.</summary>
    public string? PluginName { get; }

    /// <summary>Gets the name of the function.</summary>
    public string FunctionName { get; }

    /// <summary>Gets a name/value collection of the arguments to the function, if any.</summary>
    public IReadOnlyDictionary<string, object?>? Arguments { get; }

    /// <summary>Gets the thought signature for Gemini 3+ models.</summary>
    /// <remarks>
    /// This field is required for multi-turn function calling to work correctly with Gemini 3+ models.
    /// The thought signature must be preserved and passed back in subsequent requests.
    /// </remarks>
    public string? ThoughtSignature { get; }

    /// <summary>Gets the fully-qualified name of the function.</summary>
    /// <remarks>
    /// This is the concatenation of the <see cref="PluginName"/> and the <see cref="FunctionName"/>,
    /// separated by <see cref="GeminiFunction.NameSeparator"/>. If there is no <see cref="PluginName"/>,
    /// this is the same as <see cref="FunctionName"/>.
    /// </remarks>
    public string FullyQualifiedName
        => this._fullyQualifiedFunctionName
            ??= string.IsNullOrEmpty(this.PluginName) ? this.FunctionName : $"{this.PluginName}{GeminiFunction.NameSeparator}{this.FunctionName}";

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder(this.FullyQualifiedName);

        sb.Append('(');
        if (this.Arguments is not null)
        {
            string separator = "";
            foreach (var arg in this.Arguments)
            {
                sb.Append(separator).Append(arg.Key).Append(':').Append(arg.Value);
                separator = ", ";
            }
        }

        sb.Append(')');

        return sb.ToString();
    }
}