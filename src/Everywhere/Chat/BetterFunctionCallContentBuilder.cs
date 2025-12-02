using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat;

partial class ChatService
{
    /// <summary>
    /// A builder class for creating <see cref="FunctionCallContent"/> objects from incremental function call updates represented by <see cref="StreamingFunctionCallUpdateContent"/>.
    /// </summary>
    public sealed class BetterFunctionCallContentBuilder
    {
        public int Count => _functionNamesByIndex?.Count ?? 0;

        private Dictionary<string, string>? _functionCallIdsByIndex;
        private Dictionary<string, string>? _functionNamesByIndex;
        private Dictionary<string, StringBuilder>? _functionArgumentBuildersByIndex;
        private Dictionary<string, IReadOnlyDictionary<string, object?>>? _functionMetadataByIndex;
        private readonly JsonSerializerOptions? _jsonSerializerOptions;

        /// <summary>
        /// Creates a new instance of the <see cref="BetterFunctionCallContentBuilder"/> class.
        /// </summary>
        [RequiresUnreferencedCode("Uses reflection to deserialize function arguments, making it incompatible with AOT scenarios.")]
        [RequiresDynamicCode("Uses reflection to deserialize function arguments, making it incompatible with AOT scenarios.")]
        public BetterFunctionCallContentBuilder()
        {
        }

        /// <summary>
        /// Creates a new instance of the <see cref="BetterFunctionCallContentBuilder"/> class.
        /// </summary>
        /// <param name="jsonSerializerOptions">The <see cref="JsonSerializerOptions"/> to use for deserializing function arguments.</param>
        [Experimental("SKEXP0120")]
        public BetterFunctionCallContentBuilder(JsonSerializerOptions jsonSerializerOptions)
        {
            _jsonSerializerOptions = jsonSerializerOptions;
        }

        /// <summary>
        /// Extracts function call updates from the content and track them for later building.
        /// </summary>
        /// <param name="content">The content to extract function call updates from.</param>
        public void Append(StreamingChatMessageContent content)
        {
            var streamingFunctionCallUpdates = content.Items.OfType<StreamingFunctionCallUpdateContent>();

            foreach (var update in streamingFunctionCallUpdates)
            {
                TrackStreamingFunctionCallUpdate(
                    update,
                    ref _functionCallIdsByIndex,
                    ref _functionNamesByIndex,
                    ref _functionArgumentBuildersByIndex,
                    ref _functionMetadataByIndex);
            }
        }

        /// <summary>
        /// Builds a list of <see cref="FunctionCallContent"/> out of function call updates tracked by the <see cref="Append"/> method.
        /// </summary>
        /// <returns>A list of <see cref="FunctionCallContent"/> objects.</returns>
        public IReadOnlyList<FunctionCallContent> Build()
        {
            FunctionCallContent[]? functionCalls = null;

            if (_functionCallIdsByIndex is not { Count: > 0 }) return functionCalls ?? [];
            functionCalls = new FunctionCallContent[_functionCallIdsByIndex.Count];

            for (var i = 0; i < _functionCallIdsByIndex.Count; i++)
            {
                var functionCallIndexAndId = _functionCallIdsByIndex.ElementAt(i);

                var functionName = string.Empty;

                if (_functionNamesByIndex?.TryGetValue(functionCallIndexAndId.Key, out var fqn) ?? false)
                {
                    functionName = fqn;
                }

                var (arguments, exception) = GetFunctionArgumentsSafe(functionCallIndexAndId.Key);

                IReadOnlyDictionary<string, object?>? metadata = null;
                _functionMetadataByIndex?.TryGetValue(functionCallIndexAndId.Key, out metadata);

                functionCalls[i] = new FunctionCallContent(
                    functionName: functionName,
                    pluginName: null,
                    id: functionCallIndexAndId.Value,
                    arguments)
                {
                    Exception = exception,
                    Metadata = metadata
                };
            }

            [UnconditionalSuppressMessage(
                "Trimming",
                "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
                Justification =
                    "The warning is shown and should be addressed at the class creation site; there is no need to show it again at the function invocation sites.")]
            [UnconditionalSuppressMessage(
                "AOT",
                "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.",
                Justification =
                    "The warning is shown and should be addressed at the class creation site; there is no need to show it again at the function invocation sites.")]
            (KernelArguments? Arguments, Exception? Exception) GetFunctionArgumentsSafe(string functionCallIndex)
            {
                return _jsonSerializerOptions is not null ?
                    GetFunctionArguments(functionCallIndex, _jsonSerializerOptions) :
                    GetFunctionArguments(functionCallIndex);
            }

            return functionCalls;
        }

        /// <summary>
        /// Gets function arguments for a given function call index.
        /// </summary>
        /// <param name="functionCallIndex">The function call index to get the function arguments for.</param>
        /// <param name="jsonSerializerOptions">The <see cref="JsonSerializerOptions"/> to use for deserializing function arguments.</param>
        /// <returns>A tuple containing the KernelArguments and an Exception if any.</returns>
        [RequiresUnreferencedCode(
            "Uses reflection to deserialize function arguments if no JSOs are provided, making it incompatible with AOT scenarios.")]
        [RequiresDynamicCode("Uses reflection to deserialize function arguments if no JSOs are provided, making it incompatible with AOT scenarios.")]
        private (KernelArguments? Arguments, Exception? Exception) GetFunctionArguments(
            string functionCallIndex,
            JsonSerializerOptions? jsonSerializerOptions = null)
        {
            if (_functionArgumentBuildersByIndex is null ||
                !_functionArgumentBuildersByIndex.TryGetValue(functionCallIndex, out StringBuilder? functionArgumentsBuilder))
            {
                return (null, null);
            }

            var argumentsString = functionArgumentsBuilder.ToString();
            if (string.IsNullOrEmpty(argumentsString))
            {
                return (null, null);
            }

            Exception? exception = null;
            KernelArguments? arguments = null;
            try
            {
                if (jsonSerializerOptions is not null)
                {
                    var typeInfo = (JsonTypeInfo<KernelArguments>)jsonSerializerOptions.GetTypeInfo(typeof(KernelArguments));
                    arguments = JsonSerializer.Deserialize(argumentsString, typeInfo);
                }
                else
                {
                    arguments = JsonSerializer.Deserialize<KernelArguments>(argumentsString);
                }
            }
            catch (JsonException ex)
            {
                exception = new KernelException("Error: Function call arguments were invalid JSON.", ex);
            }

            return (arguments, exception);
        }

        /// <summary>
        /// Tracks streaming function call update contents.
        /// </summary>
        /// <param name="update">The streaming function call update content to track.</param>
        /// <param name="functionCallIdsByIndex">The dictionary of function call IDs by function call index.</param>
        /// <param name="functionNamesByIndex">The dictionary of function names by function call index.</param>
        /// <param name="functionArgumentBuildersByIndex">The dictionary of function argument builders by function call index.</param>
        /// <param name="functionMetadataByIndex">The dictionary of function metadata by function call index.</param>
        private static void TrackStreamingFunctionCallUpdate(
            StreamingFunctionCallUpdateContent? update,
            ref Dictionary<string, string>? functionCallIdsByIndex,
            ref Dictionary<string, string>? functionNamesByIndex,
            ref Dictionary<string, StringBuilder>? functionArgumentBuildersByIndex,
            ref Dictionary<string, IReadOnlyDictionary<string, object?>>? functionMetadataByIndex)
        {
            if (update is null)
            {
                // Nothing to track.
                return;
            }

            // Create index that is unique across many requests.
            var functionCallIndex = $"{update.RequestIndex}-{update.FunctionCallIndex}";

            // If we have a call id, ensure the index is being tracked. Even if it's not a function update,
            // we want to keep track of it so we can send back an error.
            if (update.CallId is { Length: > 0 } id)
            {
                (functionCallIdsByIndex ??= [])[functionCallIndex] = id;
            }

            // Ensure we're tracking the function's name.
            if (update.Name is { Length: > 0 } name)
            {
                (functionNamesByIndex ??= [])[functionCallIndex] = name;
            }

            // Track metadata
            if (update.Metadata is not null && !functionMetadataByIndex?.ContainsKey(functionCallIndex) == true)
            {
                (functionMetadataByIndex ??= [])[functionCallIndex] = update.Metadata;
            }
            else if (update.Metadata is not null)
            {
                (functionMetadataByIndex ??= [])[functionCallIndex] = update.Metadata;
            }

            // Ensure we're tracking the function's arguments.
            if (update.Arguments is not { } argumentsUpdate) return;

            if (!(functionArgumentBuildersByIndex ??= []).TryGetValue(functionCallIndex, out var arguments))
            {
                functionArgumentBuildersByIndex[functionCallIndex] = arguments = new StringBuilder();
            }

            arguments.Append(argumentsUpdate);
        }
    }
}