#if DEBUG
#define DEBUG_VISUAL_TREE_BUILDER
#endif

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Everywhere.Interop;
using ZLinq;
#if DEBUG_VISUAL_TREE_BUILDER
using Everywhere.Chat.Debugging;
#endif

namespace Everywhere.Chat;

public enum VisualTreeDetailLevel
{
    [DynamicResourceKey(LocaleKey.VisualTreeDetailLevel_Minimal)]
    Minimal = 0,

    [DynamicResourceKey(LocaleKey.VisualTreeDetailLevel_Compact)]
    Compact = 1,

    [DynamicResourceKey(LocaleKey.VisualTreeDetailLevel_Detailed)]
    Detailed = 2,
}

public enum VisualTreeLengthLimit
{
    /// <summary>
    /// 1024 tokens, suitable for short and focused interactions, such as answering a specific question about the UI or providing concise descriptions.
    /// </summary>
    [DynamicResourceKey(LocaleKey.VisualTreeLengthLimit_Minimal)]
    Minimal = 0,

    /// <summary>
    /// (Recommended) 4096 tokens, a balanced option that captures more context and details while still being manageable for most LLMs.
    /// Ideal for general use cases where a comprehensive understanding of the UI is beneficial without overwhelming the model.
    /// This is the recommended default setting for most interactions, providing a good trade-off between context and performance.
    /// </summary>
    [DynamicResourceKey(LocaleKey.VisualTreeLengthLimit_Balanced)]
    Balanced = 1,

    /// <summary>
    /// 10240 tokens, the most detailed option that includes extensive context and information about the UI.
    /// </summary>
    [DynamicResourceKey(LocaleKey.VisualTreeLengthLimit_Detailed)]
    Detailed = 2,

    /// <summary>
    /// 40960 tokens, an extremely detailed option that captures an exhaustive representation of the UI, including all elements and their properties.
    /// This setting is intended for advanced use cases where a deep understanding of the entire UI is necessary, and the LLM can handle very large inputs.
    /// </summary>
    [DynamicResourceKey(LocaleKey.VisualTreeLengthLimit_Ultimate)]
    Ultimate = 3,

    /// <summary>
    /// Unlimited tokens, no limit on the visual tree length. This setting should be used with caution, as it may lead to performance issues or exceed the input limits of the LLM.
    /// </summary>
    [DynamicResourceKey(LocaleKey.VisualTreeLengthLimit_Unlimited)]
    Unlimited = 4
}

/// <summary>
/// Defines the direction of traversal in the visual element tree.
/// It determines how a queued node is expanded.
/// </summary>
[Flags]
public enum VisualTreeTraverseDirections
{
    /// <summary>
    /// Core elements
    /// </summary>
    Core = 0,

    /// <summary>
    /// parent, previous sibling, next sibling
    /// </summary>
    Parent = 0x1,

    /// <summary>
    /// previous sibling, child
    /// </summary>
    PreviousSibling = 0x2,

    /// <summary>
    /// next sibling, child
    /// </summary>
    NextSibling = 0x4,

    /// <summary>
    /// next child, child
    /// </summary>
    Child = 0x8,

    All = Parent | PreviousSibling | NextSibling | Child
}

public static class VisualTreeLengthLimitExtension
{
    public static int ToTokenLimit(this VisualTreeLengthLimit limit)
    {
        return limit switch
        {
            VisualTreeLengthLimit.Minimal => 1024,
            VisualTreeLengthLimit.Balanced => 4096,
            VisualTreeLengthLimit.Detailed => 10240,
            VisualTreeLengthLimit.Ultimate => 40960,
            VisualTreeLengthLimit.Unlimited => int.MaxValue,
            _ => throw new ArgumentOutOfRangeException(nameof(limit), limit, null)
        };
    }
}

/// <summary>
///     This class builds an XML representation of the core elements, which is limited by the soft token limit and finally used by a LLM.
/// </summary>
/// <param name="coreElements"></param>
/// <param name="approximateTokenLimit"></param>
/// <param name="detailLevel"></param>
public partial class VisualTreeBuilder(
    IReadOnlyList<IVisualElement> coreElements,
    int approximateTokenLimit,
    int startingId,
    VisualTreeDetailLevel detailLevel,
    VisualTreeTraverseDirections allowedTraverseDirections = VisualTreeTraverseDirections.All
)
{
    private static readonly ActivitySource ActivitySource = new(typeof(VisualTreeBuilder).FullName.NotNull());

    /// <summary>
    /// Builds the text representation of the visual tree for the given attachments as core elements and populates the attachment contents.
    /// </summary>
    /// <param name="attachments"></param>
    /// <param name="approximateTokenLimit"></param>
    /// <param name="startingId"></param>
    /// <param name="detailLevel"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static IReadOnlyDictionary<int, IVisualElement> BuildAndPopulate(
        IReadOnlyList<ChatVisualElementAttachment> attachments,
        int approximateTokenLimit,
        int startingId,
        VisualTreeDetailLevel detailLevel,
        CancellationToken cancellationToken)
    {
        using var builderActivity = ActivitySource.StartActivity();

        var result = new Dictionary<int, IVisualElement>();
        var validAttachments = attachments
            .AsValueEnumerable()
            .Select(x => (Attachment: x, Element: x.Element?.Target))
            .Where(t => t.Element is not null)
            .Select(t => (t.Attachment, Element: t.Element!))
            .ToList();

        if (validAttachments.Count == 0)
        {
            return result;
        }

        // 1. Group core elements by their root element. Key is tuple (ProcessId, NativeWindowHandle of the ancestor TopLevel)
        var groups = validAttachments
            .AsValueEnumerable()
            .GroupBy(x =>
            {
                var current = x.Element;
                while (current is { Type: not VisualElementType.Screen and not VisualElementType.TopLevel, Parent: { } parent })
                {
                    current = parent;
                }

                return (x.Element.ProcessId, current.NativeWindowHandle);
            })
            .ToArray();

        var totalElements = validAttachments.Count;
        var totalBuiltElements = 0;

        foreach (var group in groups.AsValueEnumerable())
        {
            var groupElements = group.AsValueEnumerable().Select(x => x.Element).ToList();
            var groupCount = groupElements.Count;

            // 2. Build XML for each root group
            // Allocate token limit relative to the number of elements in the group
            var groupTokenLimit = (int)((long)approximateTokenLimit * groupCount / totalElements);

            var xmlBuilder = new VisualTreeBuilder(
                groupElements,
                groupTokenLimit,
                startingId,
                detailLevel);

            var xml = xmlBuilder.Build(cancellationToken);

            // 3. for attachments in the same group
            // First attachment gets the full XML, others got null.
            var isFirst = true;
            foreach (var (attachment, _) in group.AsValueEnumerable())
            {
                if (isFirst)
                {
                    attachment.Content = xml;
                    isFirst = false;
                }
                else
                {
                    attachment.Content = null;
                }
            }

            foreach (var kvp in xmlBuilder.BuiltVisualElements.AsValueEnumerable())
            {
                result[kvp.Key] = kvp.Value;
            }

            startingId += xmlBuilder.BuiltVisualElements.Count;
            totalBuiltElements += xmlBuilder.BuiltVisualElements.Count;
        }

        builderActivity?.SetTag("xml.detail_level", detailLevel);
        builderActivity?.SetTag("xml.length_limit", approximateTokenLimit);
        builderActivity?.SetTag("xml.built_visual_elements.count", totalBuiltElements);

        return result;
    }

    /// <summary>
    /// Traversal distance metrics for prioritization.
    /// Global: distance from core elements, Local: distance from the originating node.
    /// </summary>
    /// <param name="Global"></param>
    /// <param name="Local"></param>
    private readonly record struct TraverseDistance(int Global, int Local)
    {
        public static implicit operator TraverseDistance(int distance) => new(distance, distance);

        /// <summary>
        /// Resets the local distance to 1 and increments the global distance by 1.
        /// </summary>
        /// <returns></returns>
        public TraverseDistance Reset() => new(Global + 1, 1);

        /// <summary>
        /// Increments both global and local distances by 1.
        /// </summary>
        /// <returns></returns>
        public TraverseDistance Step() => new(Global + 1, Local + 1);
    }

    /// <summary>
    /// Represents a node in the traversal queue with a calculated priority score.
    /// </summary>
    private readonly record struct TraversalNode(
        IVisualElement Element,
        IVisualElement? Previous,
        TraverseDistance Distance,
        VisualTreeTraverseDirections Direction,
        int SiblingIndex,
        IEnumerator<IVisualElement> Enumerator
    )
    {
        public string? ParentId { get; } = Element.Parent?.Id;

        /// <summary>
        /// Calculates the final priority score for the Best-First Search algorithm.
        /// Lower value means higher priority (Min-Heap).
        /// <para>
        /// The scoring formula is a multi-dimensional weighted product:
        /// <br/>
        /// <c>FinalScore = -(TopologyScore * IntrinsicScore)</c>
        /// </para>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>1. Topology Score (Distance Decay):</b>
        /// Represents the relevance of the element based on its position in the tree relative to the Core Element.
        /// <br/>
        /// <c>Score_topo = BaseScore / (Distance + 1)</c>
        /// <br/>
        /// - Spine nodes (Ancestors) get a 2x boost.
        /// - Non-spine nodes decay linearly with distance.
        /// </para>
        /// <para>
        /// <b>2. Intrinsic Score (Type Weight):</b>
        /// Represents the inherent importance of the element type.
        /// <br/>
        /// - Interactive controls (Button, Input): 1.5x
        /// - Semantic text (Label): 1.2x
        /// - Containers: 1.0x
        /// - Decorative: 0.5x
        /// </para>
        /// <para>
        /// <b>3. Intrinsic Score (Size Weight):</b>
        /// Represents the visual prominence of the element.
        /// <br/>
        /// <c>Score_size = 1.0 + (Area / ScreenArea)</c>
        /// <br/>
        /// Larger elements are considered more important context.
        /// </para>
        /// <para>
        /// <b>4. Noise Penalty:</b>
        /// Tiny elements (&lt; 5px) receive a 0.1x penalty to filter out visual noise.
        /// </para>
        /// </remarks>
        public float GetScore()
        {
            // Core elements have the highest priority
            if (Direction == VisualTreeTraverseDirections.Core) return float.NegativeInfinity;

            // 1. Base score based on topology
            var score = Direction switch
            {
                VisualTreeTraverseDirections.Parent => 2000.0f,
                VisualTreeTraverseDirections.PreviousSibling => 10000f,
                VisualTreeTraverseDirections.NextSibling => 10000f,
                VisualTreeTraverseDirections.Child => 1000.0f,
                _ => throw new ArgumentOutOfRangeException()
            };
            if (Distance.Local > 0) score /= Distance.Local; // Linear decay with local distance
            score -= Distance.Global - Distance.Local;

            // We only calculate element properties when direction is Parent or Child
            // because when enumerating siblings, a small weighted element will "block" subsequent siblings.
            var weightedElement = Direction switch
            {
                VisualTreeTraverseDirections.Parent => Element,
                VisualTreeTraverseDirections.Child => Previous,
                _ => null
            };
            if (weightedElement is not null)
            {
                // 2. Intrinsic Score (Type Weight)
                score *= GetTypeWeight(weightedElement.Type);

                // Sometimes the visual element's BoundingRectangle is invalid,
                // but it actually has a valid size that can be obtained from its children.
                // So the following algorithm is not used for now, but we may consider adding it back in the future with some safeguards
                // (e.g., only apply size weight to Panels and TopLevels, and cap the maximum size weight) to prevent potential abuse from noisy bounding rectangles.

                // // 3. Intrinsic Score (Size Weight)
                // // Logarithmic scale for area: log(Area + 1)
                // // Larger elements are usually more important containers or focal points.
                // var rect = weightedElement.BoundingRectangle;
                // if (rect is { Width: > 0, Height: > 0 })
                // {
                //     var area = (float)rect.Width * rect.Height;
                //     // Normalize against a reference screen size (e.g., 1920x1080)
                //     const float screenArea = 1920f * 1080;
                //     var sizeFactor = 1.0f + (area / screenArea);
                //     score *= sizeFactor;
                // }
                //
                // // 4. Penalty for tiny elements (likely noise or invisible)
                // if (rect.Width is > 0 and < 5 || rect.Height is > 0 and < 5)
                // {
                //     score *= 0.1f;
                // }
            }

            // PriorityQueue is a min-heap, so we return negative score to make high scores come first.
            return -score;
        }

        private static float GetTypeWeight(VisualElementType type)
        {
            return type switch
            {
                // Semantic text: High value
                VisualElementType.Label or
                    VisualElementType.TextEdit or
                    VisualElementType.Document => 2.0f,

                // Structural containers: High value
                VisualElementType.Panel or
                    VisualElementType.TopLevel or
                    VisualElementType.TabControl => 1.5f,

                // Interactive controls: Medium value
                VisualElementType.Button or
                    VisualElementType.ComboBox or
                    VisualElementType.CheckBox or
                    VisualElementType.RadioButton or
                    VisualElementType.Slider or
                    VisualElementType.MenuItem or
                    VisualElementType.TabItem => 1.0f,

                // Decorative/Less important: Low value
                VisualElementType.Image or
                    VisualElementType.ScrollBar => 0.5f,

                _ => 1.0f
            };
        }
    }

    /// <summary>
    /// Represents a node in the XML tree being built.
    /// This class is mutable to support dynamic updates of activation state during traversal.
    /// </summary>
    private class VisualElementNode(
        IVisualElement element,
        VisualElementType type,
        string? parentId,
        int siblingIndex,
        string? description,
        IReadOnlyList<string> contentLines,
        int tokenCount,
        int contentTokenCount,
        bool isSelfInformative,
        bool isImportant
    )
    {
        public IVisualElement Element { get; } = element;

        public VisualElementType Type { get; } = type;

        public string? ParentId { get; } = parentId;

        public int SiblingIndex { get; } = siblingIndex;

        public string? Description { get; } = description;

        public IReadOnlyList<string> ContentLines { get; } = contentLines;

        /// <summary>
        /// The token cost of the element's structure (tags, attributes, ID) excluding content text.
        /// </summary>
        public int TokenCount { get; } = tokenCount;

        /// <summary>
        /// The token cost of the element's content text (Description, Contents).
        /// </summary>
        public int ContentTokenCount { get; } = contentTokenCount;

        public VisualElementNode? Parent { get; set; }

        public HashSet<VisualElementNode> Children { get; } = [];

        /// <summary>
        /// Indicates whether this element should be rendered in the final XML.
        /// This is determined dynamically based on <see cref="VisualTreeDetailLevel"/> and the presence of informative children.
        /// </summary>
        public bool IsVisible { get; set; } = isSelfInformative;

        /// <summary>
        /// Indicates whether this element is intrinsically informative (e.g., has text, is interactive, or is a core element).
        /// If true, <see cref="IsVisible"/> is always true.
        /// </summary>
        public bool IsSelfInformative { get; } = isSelfInformative;

        /// <summary>
        /// Indicates whether this element is an important element.
        /// </summary>
        public bool IsImportant { get; } = isImportant;

        /// <summary>
        /// The number of children that have informative content (either self-informative or have informative descendants).
        /// </summary>
        public int InformativeChildCount { get; set; }

        /// <summary>
        /// Indicates whether this element has any informative descendants.
        /// </summary>
        public bool HasInformativeDescendants { get; set; }

        /// <summary>
        /// Indicates that some children of this element were omitted due to the token budget being exhausted.
        /// Set during the BFS cleanup phase when remaining queue items are discarded.
        /// </summary>
        public bool HasOmittedChildren { get; set; }

        /// <summary>
        /// Indicates that the text content of this element was truncated to fit the remaining token budget.
        /// </summary>
        public bool IsContentTruncated { get; set; }
    }

    /// <summary>
    /// Hierarchical DTO for JSON / TOON serialization.
    /// Property names are deliberately short to minimise token usage.
    /// Null fields are omitted by <see cref="CompactJsonOptions"/>.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private readonly record struct VisualElementDto(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("type"), JsonConverter(typeof(JsonStringEnumConverter))] VisualElementType Type,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("box")] string? Box,
        [property: JsonPropertyName("extra")] string? Extra,
        [property: JsonPropertyName("children")] List<VisualElementDto>? Children,
        [property: JsonPropertyName("omitted")] string? Omitted
    );

    /// <summary>
    ///     The mapping from original element ID to the built sequential ID starting from <see cref="startingId"/>.
    /// </summary>
    public Dictionary<int, IVisualElement> BuiltVisualElements { get; } = [];

    private readonly HashSet<string> _coreElementIdSet = coreElements
        .Select(e => e.Id)
        .Where(id => !string.IsNullOrEmpty(id))
        .ToHashSet(StringComparer.Ordinal);

    private string? _cachedResult;

#if DEBUG_VISUAL_TREE_BUILDER
    private VisualTreeRecorder? _debugRecorder;
#endif

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const VisualElementStates InteractiveStates = VisualElementStates.Focused | VisualElementStates.Selected;

    /// <summary>
    /// Builds the text representation of the visual tree for the core elements.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public string Build(CancellationToken cancellationToken)
    {
        if (coreElements.Count == 0) throw new InvalidOperationException("No core elements to build.");

        if (_cachedResult != null) return _cachedResult;
        cancellationToken.ThrowIfCancellationRequested();

#if DEBUG_VISUAL_TREE_BUILDER
        _debugRecorder = new VisualTreeRecorder(coreElements, approximateTokenLimit, "WeightedPriority");
#endif

        // Priority Queue for Best-First Search
        var priorityQueue = new PriorityQueue<TraversalNode, float>();
        var visitedElements = new Dictionary<string, VisualElementNode>();

        // 1. Enqueue core nodes
        TryEnqueueTraversalNode(priorityQueue, null, 0, VisualTreeTraverseDirections.Core, coreElements.GetEnumerator());

        // 2. Process the Queue
        ProcessTraversalQueue(priorityQueue, visitedElements, cancellationToken);

        // 3. Dispose remaining enumerators and mark omitted parents.
        // Any node still in the queue was discarded due to token budget exhaustion.
        // If its parent was already visited, that parent has omitted children.
        while (priorityQueue.Count > 0)
        {
            if (priorityQueue.TryDequeue(out var node, out _))
            {
                if (node.ParentId is not null && visitedElements.TryGetValue(node.ParentId, out var parentNode))
                {
                    parentNode.HasOmittedChildren = true;
                }

                node.Enumerator.Dispose();
            }
        }

        // 4. Generate output based on detail level
        _cachedResult = detailLevel switch
        {
            VisualTreeDetailLevel.Detailed => GenerateXmlString(visitedElements),
            VisualTreeDetailLevel.Compact => GenerateJsonString(visitedElements),
            _ => GenerateToonString(visitedElements),
        };

#if DEBUG_VISUAL_TREE_BUILDER
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var filename = $"visual_tree_debug_{timestamp}.json";
        var debugPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
        _debugRecorder?.SaveSession(debugPath);
#endif

        return _cachedResult;
    }

#if DEBUG_VISUAL_TREE_BUILDER
    private void TryEnqueueTraversalNode(
#else
    private static void TryEnqueueTraversalNode(
#endif
        PriorityQueue<TraversalNode, float> priorityQueue,
        in TraversalNode? previous,
        in TraverseDistance distance,
        VisualTreeTraverseDirections direction,
        IEnumerator<IVisualElement> enumerator)
    {
        if (!enumerator.MoveNext())
        {
            enumerator.Dispose();
            return;
        }

        var node = new TraversalNode(
            enumerator.Current,
            previous?.Element,
            distance,
            direction,
            direction switch
            {
                VisualTreeTraverseDirections.PreviousSibling => previous?.SiblingIndex - 1 ?? 0,
                VisualTreeTraverseDirections.NextSibling => previous?.SiblingIndex + 1 ?? 0,
                _ => 0
            },
            enumerator);
        var score = node.GetScore();
        priorityQueue.Enqueue(node, score);

#if DEBUG_VISUAL_TREE_BUILDER
        _debugRecorder?.RegisterNode(node.Element, node.GetScore());
        _debugRecorder?.RecordStep(
            node.Element,
            "Enqueue",
            score,
            $"Parent: {node.ParentId}, Previous: {node.Previous?.Id}, Direction: {node.Direction}, Distance: {node.Distance}",
            0,
            priorityQueue.Count);
#endif
    }

    private void ProcessTraversalQueue(
        PriorityQueue<TraversalNode, float> priorityQueue,
        Dictionary<string, VisualElementNode> visitedElements,
        CancellationToken cancellationToken)
    {
        var accumulatedTokenCount = 0;

        while (priorityQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remainingTokenCount = approximateTokenLimit - accumulatedTokenCount;
            if (remainingTokenCount <= 0)
            {
#if DEBUG_VISUAL_TREE_BUILDER
                _debugRecorder?.RecordStep(
                    priorityQueue.Peek().Element,
                    "Stop",
                    0,
                    "Token limit reached",
                    accumulatedTokenCount,
                    priorityQueue.Count);
#endif
                break;
            }

#if DEBUG_VISUAL_TREE_BUILDER
            if (!priorityQueue.TryDequeue(out var node, out var priority)) break;
#else
            if (!priorityQueue.TryDequeue(out var node, out _)) break;
#endif
            var element = node.Element;
            var id = element.Id;

            if (visitedElements.ContainsKey(id))
            {
#if DEBUG_VISUAL_TREE_BUILDER
                _debugRecorder?.RecordStep(element, "Skip", priority, "Already visited", accumulatedTokenCount, priorityQueue.Count);
#endif
                continue;
            }

            // Process the current node and create the VisualElementNode
            CreateVisualElementNode(visitedElements, node, remainingTokenCount, ref accumulatedTokenCount);

#if DEBUG_VISUAL_TREE_BUILDER
            _debugRecorder?.RecordStep(
                element,
                "Visit",
                priority,
                $"Parent: {node.ParentId}, Previous: {node.Previous?.Id}, Direction: {node.Direction}, Distance: {node.Distance}",
                accumulatedTokenCount,
                priorityQueue.Count);
#endif

            // Check limit again after adding this node
            if (accumulatedTokenCount > approximateTokenLimit) break;

            // Add more nodes to the queue based on traversal direction
            PropagateNode(priorityQueue, node);
        }
    }

    private void CreateVisualElementNode(
        Dictionary<string, VisualElementNode> visitedElements,
        TraversalNode traversalNode,
        int remainingTokenCount,
        ref int accumulatedTokenCount)
    {
        var element = traversalNode.Element;
        var id = element.Id;
        var type = element.Type;

        // --- Determine Content and Self-Informativeness ---
        string? description = null;
        string? content = null;
        var isContentTruncated = false;
        var isTextElement = type is VisualElementType.Label or VisualElementType.TextEdit or VisualElementType.Document;
        var text = element.GetText();
        if (element.Name is { Length: > 0 } name)
        {
            if (isTextElement && string.IsNullOrEmpty(text))
            {
                content = TruncateIfNeeded(name, remainingTokenCount, out var truncated);
                isContentTruncated |= truncated;
            }
            else if (!isTextElement || name != text)
            {
                description = TruncateIfNeeded(name, remainingTokenCount, out var truncated);
                isContentTruncated |= truncated;
            }
        }
        if (content is null && text is { Length: > 0 })
        {
            content = TruncateIfNeeded(text, remainingTokenCount, out var truncated);
            isContentTruncated |= truncated;
        }
        var contentLines = content?.Split(Environment.NewLine) ?? [];

        var hasTextContent = contentLines.Length > 0;
        var hasDescription = !string.IsNullOrWhiteSpace(description);
        var interactive = IsInteractiveElement(element);
        var isCoreElement = _coreElementIdSet.Contains(id);
        var isSelfInformative = hasTextContent || hasDescription || interactive || isCoreElement;

        // --- Calculate Token Costs ---
        // Cost varies by output format: XML is verbose, JSON is compact, TOON is tabular (header amortized).
        var selfTokenCount = detailLevel switch
        {
            VisualTreeDetailLevel.Detailed => 8, // XML: <Type id="N">...</Type>
            VisualTreeDetailLevel.Compact => 5, // JSON: {"t":"Type","id":N,...}
            _ => 2 // TOON: row values only (header amortized)
        };

        // Add cost for bounds attributes if applicable (x, y, width, height)
        if (ShouldIncludeBounds(detailLevel, type))
        {
            selfTokenCount += detailLevel switch
            {
                VisualTreeDetailLevel.Detailed => 20, // pos="x,y" size="wxh"
                VisualTreeDetailLevel.Compact => 10, // ,"pos":"x,y","size":"wxh"
                _ => 4 // ,x,y,wxh
            };
        }

        var attrOverhead = detailLevel switch
        {
            VisualTreeDetailLevel.Detailed => 3, // description="..."
            VisualTreeDetailLevel.Compact => 2, // ,"desc":"..."
            _ => 1 // ,value
        };
        var lineOverhead = detailLevel == VisualTreeDetailLevel.Detailed ? 4 : 0; // XML indentation per line; JSON/TOON join with \n
        var blockOverhead = detailLevel switch
        {
            VisualTreeDetailLevel.Detailed => 8, // end tag
            VisualTreeDetailLevel.Compact => 2, // ,"content":"..."
            _ => 1 // ,value
        };

        var contentTokenCount = 0;
        if (description != null) contentTokenCount += EstimateTokenCount(description) + attrOverhead;
        contentTokenCount += contentLines.Length switch
        {
            > 0 and < 3 => contentLines.Sum(EstimateTokenCount),
            >= 3 => contentLines.Sum(line => EstimateTokenCount(line) + lineOverhead) + blockOverhead,
            _ => 0
        };

        // Create the XML Element node
        var elementNode = visitedElements[id] = new VisualElementNode(
            element,
            type,
            traversalNode.ParentId,
            traversalNode.SiblingIndex,
            description,
            contentLines,
            selfTokenCount,
            contentTokenCount,
            isSelfInformative,
            traversalNode.Direction == VisualTreeTraverseDirections.Core)
        {
            IsContentTruncated = isContentTruncated
        };

        // --- Update Token Count and Propagate ---

        // If the element is self-informative, it is active immediately.
        if (elementNode.IsVisible || type is not VisualElementType.TopLevel and not VisualElementType.Screen)
        {
            accumulatedTokenCount += elementNode.TokenCount + elementNode.ContentTokenCount;
        }

        // Link to parent and propagate updates
        if (traversalNode.ParentId != null && visitedElements.TryGetValue(traversalNode.ParentId, out var parentXmlElement))
        {
            parentXmlElement.Children.Add(elementNode);
            elementNode.Parent = parentXmlElement;

            // If the new child is informative (self-informative or has informative descendants),
            // we need to notify the parent.
            // Note: A newly created node has no descendants yet, so HasInformativeDescendants is false.
            // So we only check IsSelfInformative.
            if (elementNode.IsSelfInformative)
            {
                PropagateInformativeUpdate(parentXmlElement, ref accumulatedTokenCount);
            }
        }
        // If we traversed from parent direction, above method cannot link parent-child.
        else if (traversalNode is { Direction: VisualTreeTraverseDirections.Parent })
        {
            foreach (var childXmlElement in visitedElements.Values
                         .AsValueEnumerable()
                         .Where(e => e.Parent is null)
                         .Where(e => string.Equals(e.ParentId, id, StringComparison.Ordinal)))
            {
                elementNode.Children.Add(childXmlElement);
                childXmlElement.Parent = elementNode;

                if (elementNode.IsSelfInformative)
                {
                    PropagateInformativeUpdate(childXmlElement, ref accumulatedTokenCount);
                }
            }
        }
    }

#if DEBUG_VISUAL_TREE_BUILDER
    private void PropagateNode(
#else
    private void PropagateNode(
#endif
        PriorityQueue<TraversalNode, float> priorityQueue,
        in TraversalNode node)
    {
#if DEBUG_VISUAL_TREE_BUILDER
        Debug.WriteLine($"[PropagateNode] {node}");
#endif

        var elementType = node.Element.Type;
        switch (node.Direction)
        {
            case VisualTreeTraverseDirections.Core:
            {
                // In this case, node.Enumerator is the core element enumerator
                TryEnqueueTraversalNode(
                    priorityQueue,
                    node,
                    0,
                    VisualTreeTraverseDirections.Core,
                    node.Enumerator);

                // Only enqueue parent and siblings if not top-level
                if (elementType != VisualElementType.TopLevel)
                {
                    if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.Parent))
                        TryEnqueueTraversalNode(
                            priorityQueue,
                            node,
                            1,
                            VisualTreeTraverseDirections.Parent,
                            node.Element.GetAncestors().GetEnumerator());

                    // Get two enumerators together, prohibited to dispose one before the other, causing resource reallocation.
                    var siblingAccessor = node.Element.SiblingAccessor;

                    if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.PreviousSibling))
                        TryEnqueueTraversalNode(
                            priorityQueue,
                            node,
                            1,
                            VisualTreeTraverseDirections.PreviousSibling,
                            siblingAccessor.BackwardEnumerator);

                    if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.NextSibling))
                        TryEnqueueTraversalNode(
                            priorityQueue,
                            node,
                            1,
                            VisualTreeTraverseDirections.NextSibling,
                            siblingAccessor.ForwardEnumerator);
                }

                if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.Child))
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        1,
                        VisualTreeTraverseDirections.Child,
                        node.Element.Children.GetEnumerator());
                break;
            }
            case VisualTreeTraverseDirections.Parent when elementType != VisualElementType.TopLevel:
            {
                if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.Parent))
                    // In this case, node.Enumerator is the Ancestors enumerator
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Step(),
                        VisualTreeTraverseDirections.Parent,
                        node.Enumerator);

                // Get two enumerators together, prohibited to dispose one before the other, causing resource reallocation.
                var siblingAccessor = node.Element.SiblingAccessor;

                if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.PreviousSibling))
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Reset(),
                        VisualTreeTraverseDirections.PreviousSibling,
                        siblingAccessor.BackwardEnumerator);

                if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.NextSibling))
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Reset(),
                        VisualTreeTraverseDirections.NextSibling,
                        siblingAccessor.ForwardEnumerator);
                break;
            }
            case VisualTreeTraverseDirections.PreviousSibling:
            {
                if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.PreviousSibling))
                    // In this case, node.Enumerator is the Previous Sibling enumerator
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Step(),
                        VisualTreeTraverseDirections.PreviousSibling,
                        node.Enumerator);

                if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.Child))
                    // Also enqueue the children of this sibling
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Reset(),
                        VisualTreeTraverseDirections.Child,
                        node.Element.Children.GetEnumerator());
                break;
            }
            case VisualTreeTraverseDirections.NextSibling:
            {
                if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.NextSibling))
                    // In this case, node.Enumerator is the Next Sibling enumerator
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Step(),
                        VisualTreeTraverseDirections.NextSibling,
                        node.Enumerator);

                if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.Child))
                    // Also enqueue the children of this sibling
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Reset(),
                        VisualTreeTraverseDirections.Child,
                        node.Element.Children.GetEnumerator());
                break;
            }
            case VisualTreeTraverseDirections.Child:
            {
                if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.NextSibling))
                    // In this case, node.Enumerator is the Children enumerator
                    // But note that these children are actually descendants of the original node's sibling.
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Step(),
                        VisualTreeTraverseDirections.NextSibling,
                        node.Enumerator);

                if (allowedTraverseDirections.HasFlag(VisualTreeTraverseDirections.Child))
                    // Also enqueue the children of this child
                    TryEnqueueTraversalNode(
                        priorityQueue,
                        node,
                        node.Distance.Reset(),
                        VisualTreeTraverseDirections.Child,
                        node.Element.Children.GetEnumerator());
                break;
            }
        }
    }

    private string GenerateXmlString(Dictionary<string, VisualElementNode> visualElements)
    {
        var sb = new StringBuilder();
        foreach (var rootElement in visualElements.Values.AsValueEnumerable().Where(e => e.Parent is null))
        {
            if (rootElement.Type is not VisualElementType.TopLevel and not VisualElementType.Screen)
            {
                // Append a synthetic root for non-top-level elements
                var topLevelOrScreenElement = rootElement.Element.Parent;
                while (topLevelOrScreenElement is { Type: not VisualElementType.TopLevel and not VisualElementType.Screen, Parent: { } parent })
                {
                    topLevelOrScreenElement = parent;
                }

                if (topLevelOrScreenElement is not null)
                {
                    // Create a synthetic root element and build its XML
                    var actualRootElement = new VisualElementNode(
                        topLevelOrScreenElement,
                        topLevelOrScreenElement.Type,
                        null,
                        0,
                        null,
                        ["<!-- Child elements omitted for brevity -->"],
                        8,
                        0,
                        true,
                        false)
                    {
                        Children = { rootElement }
                    };
                    BuildXml(sb, actualRootElement, 0);
                    continue;
                }
            }

            BuildXml(sb, rootElement, 0);
        }

        return sb.TrimEnd().ToString();
    }

    private void BuildXml(StringBuilder sb, VisualElementNode elementNode, int indentLevel)
    {
        var element = elementNode.Element;
        var elementType = elementNode.Type;
        var indent = new string(' ', indentLevel * 2);

        // If not active, we don't render this element's tags, but we might render its children.
        // This acts as a "passthrough" for structural containers that are not interesting enough to show.
        // For TopLevel and Screen elements, we always render them even if not visible.
        if (!elementNode.IsVisible && elementType is not VisualElementType.TopLevel and not VisualElementType.Screen)
        {
            foreach (var child in elementNode.Children.AsValueEnumerable().OrderBy(x => x.SiblingIndex))
            {
                BuildXml(sb, child, indentLevel);
            }

            return;
        }

        // Start tag
        sb.Append(indent).Append('<').Append(elementType);

        // Add ID
        var id = BuiltVisualElements.Count + startingId;
        BuiltVisualElements[id] = element;
        sb.Append(" id=\"").Append(id).Append('"');

        // Add coreElement attribute if applicable
        if (elementNode.IsImportant)
        {
            sb.Append(" important=\"true\"");
        }

        // Add bounds if needed
        if (ShouldIncludeBounds(detailLevel, elementType))
        {
            // for containers, include the element's size
            var bounds = element.BoundingRectangle;
            sb.Append(" box=\"")
                .Append(bounds.X).Append(',')
                .Append(bounds.Y).Append(',')
                .Append(bounds.Width).Append(',')
                .Append(bounds.Height).Append('"');
        }

        // For top-level elements, add pid, process name and WindowHandle attributes
        if (elementType == VisualElementType.TopLevel)
        {
            var processId = elementNode.Element.ProcessId;
            if (processId > 0)
            {
                sb.Append(" pid=\"").Append(processId).Append('"');
                try
                {
                    using var process = Process.GetProcessById(processId);
                    sb.Append(" process=\"").Append(SecurityElement.Escape(process.ProcessName)).Append('"');
                }
                catch
                {
                    // Ignore if process not found
                }
            }

            var windowHandle = elementNode.Element.NativeWindowHandle;
            if (windowHandle > 0)
            {
                sb.Append(" hwnd=\"0x").Append(windowHandle.ToString("X")).Append('"');
            }
        }

        if (elementNode.Description != null)
        {
            sb.Append(" description=\"").Append(SecurityElement.Escape(elementNode.Description)).Append('"');
        }

        // Add content attribute if there's a 1 or 2 line content
        if (elementNode.ContentLines.Count is > 0 and < 3)
        {
            sb.Append(" content=\"").Append(SecurityElement.Escape(string.Join('\n', elementNode.ContentLines))).Append('"');
        }

        if (elementNode.Children.Count == 0 && elementNode.ContentLines.Count < 3 && !elementNode.HasOmittedChildren)
        {
            // Self-closing tag if no children, no content, and nothing omitted
            sb.Append("/>").AppendLine();
            return;
        }

        sb.Append('>').AppendLine();
        var xmlLengthBeforeContent = sb.Length;

        // Add contents if there are 3 or more lines
        if (elementNode.ContentLines.Count >= 3)
        {
            foreach (var contentLine in elementNode.ContentLines.AsValueEnumerable())
            {
                if (string.IsNullOrWhiteSpace(contentLine))
                {
                    sb.AppendLine(); // don't write indentation for empty lines
                    continue;
                }

                sb.Append(indent).Append("  ").Append(SecurityElement.Escape(contentLine)).AppendLine();
            }
        }

        // Handle child elements
        foreach (var child in elementNode.Children.AsValueEnumerable().OrderBy(x => x.SiblingIndex))
        {
            BuildXml(sb, child, indentLevel + 1);
        }

        // Add omission hint for the LLM
        if (elementNode.HasOmittedChildren)
        {
            sb.Append(indent).Append("  <!-- more children omitted, use get_visual_tree(elementId=").Append(id).Append(") to expand -->").AppendLine();
        }

        if (xmlLengthBeforeContent == sb.Length)
        {
            // No content or children were added, so we can convert to self-closing tag
            sb.Length -= Environment.NewLine.Length + 1; // Remove the newline and '>'
            sb.Append("/>").AppendLine();
            return;
        }

        // End tag
        sb.Append(indent).Append("</").Append(element.Type).Append('>').AppendLine();
    }

    /// <summary>
    /// Generates a compact minified JSON string from the visual tree using <see cref="VisualElementDto"/>.
    /// The output preserves the full tree hierarchy via nested <c>ch</c> (children) arrays.
    /// Null fields are omitted to minimize token usage.
    /// </summary>
    private string GenerateJsonString(Dictionary<string, VisualElementNode> visitedElements)
    {
        var tree = BuildElementDtoTree(visitedElements);
        return JsonSerializer.Serialize(tree, CompactJsonOptions);
    }

    /// <summary>
    /// Generates a TOON (Token-Oriented Object Notation) string from the visual tree.
    /// The output preserves the full tree hierarchy.
    /// </summary>
    private string GenerateToonString(Dictionary<string, VisualElementNode> visitedElements)
    {
        var tree = BuildElementDtoTree(visitedElements);

        var sb = new StringBuilder("{id,type,name,text,box,extra,children,omitted}[");
        sb.Append(tree.Count).Append(']').AppendLine();

        foreach (var root in tree)
        {
            EncodeToonString(sb, root, 0);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Encodes a single <see cref="VisualElementDto"/> and its children into TOON format.
    /// </summary>
    /// <example>
    /// 12|Label|"System.Net.Http.HttpRequestException: Response status code does not indicate success: 500 (Internal Server Error). — sylinko — everywhere - 内存使用率 - 393 MB"||||0
    /// </example>
    /// <param name="sb"></param>
    /// <param name="dto"></param>
    /// <param name="indentLevel"></param>
    private static void EncodeToonString(StringBuilder sb, VisualElementDto dto, int indentLevel)
    {
        if (indentLevel > 0) sb.Append(new string(' ', indentLevel * 2));

        sb.Append(dto.Id).Append('|').Append(dto.Type).Append('|');
        if (!string.IsNullOrEmpty(dto.Name)) sb.Append(JsonSerializer.Serialize(dto.Name, CompactJsonOptions));
        sb.Append('|');
        if (!string.IsNullOrEmpty(dto.Text)) sb.Append(JsonSerializer.Serialize(dto.Text, CompactJsonOptions));
        sb.Append('|');
        if (!string.IsNullOrEmpty(dto.Box)) sb.Append(JsonSerializer.Serialize(dto.Box, CompactJsonOptions));
        sb.Append('|');
        if (!string.IsNullOrEmpty(dto.Extra)) sb.Append(JsonSerializer.Serialize(dto.Extra, CompactJsonOptions));
        sb.Append("|[").Append(dto.Children?.Count ?? 0).Append(']');
        sb.Append('|');
        if (!string.IsNullOrEmpty(dto.Omitted)) sb.Append(JsonSerializer.Serialize(dto.Omitted, CompactJsonOptions));
        sb.AppendLine();

        if (dto.Children is { Count: > 0 } children)
        {
            foreach (var child in children)
            {
                EncodeToonString(sb, child, indentLevel + 1);
            }
        }
    }

    /// <summary>
    /// Builds a hierarchical list of root <see cref="VisualElementDto"/> trees from the visited elements.
    /// Non-visible containers are skipped (passthrough) — their children are promoted to the parent level,
    /// replicating the same structural semantics as <see cref="BuildXml"/>.
    /// Synthetic TopLevel/Screen roots are created when the actual root is a non-top-level element.
    /// </summary>
    private List<VisualElementDto> BuildElementDtoTree(Dictionary<string, VisualElementNode> visitedElements)
    {
        var roots = new List<VisualElementDto>();
        foreach (var rootElement in visitedElements.Values.AsValueEnumerable().Where(e => e.Parent is null))
        {
            if (rootElement.Type is not VisualElementType.TopLevel and not VisualElementType.Screen)
            {
                // Walk up to find the actual TopLevel/Screen ancestor
                var topLevelOrScreenElement = rootElement.Element.Parent;
                while (topLevelOrScreenElement is { Type: not VisualElementType.TopLevel and not VisualElementType.Screen, Parent: { } parent })
                {
                    topLevelOrScreenElement = parent;
                }

                if (topLevelOrScreenElement is not null)
                {
                    var syntheticId = BuiltVisualElements.Count + startingId;
                    BuiltVisualElements[syntheticId] = topLevelOrScreenElement;

                    // Collect children from the rootElement subtree
                    var childDtos = new List<VisualElementDto>();
                    CollectVisibleDtos(childDtos, rootElement);
                    childDtos = MergeConsecutiveLabels(childDtos);

                    roots.Add(
                        CreateElementDto(
                            topLevelOrScreenElement,
                            topLevelOrScreenElement.Type,
                            syntheticId,
                            description: null,
                            contentLines: null,
                            isImportant: false,
                            children: childDtos.Count > 0 ? childDtos : null,
                            omitted: "children")); // Synthetic roots always have omitted children
                    continue;
                }
            }

            CollectVisibleDtos(roots, rootElement);
        }

        return MergeConsecutiveLabels(roots);
    }

    /// <summary>
    /// Recursively builds <see cref="VisualElementDto"/> nodes for the tree.
    /// Visible elements produce a DTO whose <see cref="VisualElementDto.Children"/> contains
    /// their own visible descendants. Non-visible containers are transparent — their children
    /// are promoted directly into <paramref name="output"/> (passthrough semantics).
    /// </summary>
    private void CollectVisibleDtos(List<VisualElementDto> output, VisualElementNode elementNode)
    {
        var element = elementNode.Element;
        var elementType = elementNode.Type;

        // Non-visible non-top-level elements pass through: skip self, promote children.
        if (!elementNode.IsVisible && elementType is not VisualElementType.TopLevel and not VisualElementType.Screen)
        {
            foreach (var child in elementNode.Children.AsValueEnumerable().OrderBy(x => x.SiblingIndex))
            {
                CollectVisibleDtos(output, child);
            }

            return;
        }

        // Visible node: assign sequential ID and recurse children.
        var id = BuiltVisualElements.Count + startingId;
        BuiltVisualElements[id] = element;

        var childDtos = new List<VisualElementDto>();
        foreach (var child in elementNode.Children.AsValueEnumerable().OrderBy(x => x.SiblingIndex))
        {
            CollectVisibleDtos(childDtos, child);
        }

        childDtos = MergeConsecutiveLabels(childDtos);

        // Compute omission marker
        var omitted = GetOmittedMarker(elementNode.HasOmittedChildren, elementNode.IsContentTruncated);

        output.Add(
            CreateElementDto(
                element,
                elementType,
                id,
                elementNode.Description,
                elementNode.ContentLines,
                elementNode.IsImportant,
                children: childDtos.Count > 0 ? childDtos : null,
                omitted: omitted));
    }

    /// <summary>
    /// Merges runs of consecutive childless <see cref="VisualElementType.Label"/> DTOs into
    /// a single DTO to reduce token waste. The merged element keeps the first label's ID,
    /// concatenates names and texts, unions bounding boxes, and combines extras.
    /// </summary>
    private static List<VisualElementDto> MergeConsecutiveLabels(List<VisualElementDto> dtos)
    {
        if (dtos.Count < 2) return dtos;

        var result = new List<VisualElementDto>(dtos.Count);
        var i = 0;

        while (i < dtos.Count)
        {
            var current = dtos[i];
            if (current.Type != VisualElementType.Label || current.Children is { Count: > 0 })
            {
                result.Add(current);
                i++;
                continue;
            }

            // Scan for the end of the consecutive-label run.
            var j = i + 1;
            while (j < dtos.Count && dtos[j].Type == VisualElementType.Label && dtos[j].Children is null or { Count: 0 })
            {
                j++;
            }

            if (j - i == 1)
            {
                // Single label — no merging needed.
                result.Add(current);
                i++;
                continue;
            }

            result.Add(MergeLabelRange(dtos, i, j));
            i = j;
        }

        return result;
    }

    /// <summary>
    /// Produces a single merged <see cref="VisualElementDto"/> from the label DTOs in
    /// <paramref name="dtos"/>[<paramref name="start"/> .. <paramref name="end"/>).
    /// </summary>
    private static VisualElementDto MergeLabelRange(List<VisualElementDto> dtos, int start, int end)
    {
        var first = dtos[start];

        StringBuilder? nameBuilder = null;
        StringBuilder? textBuilder = null;
        StringBuilder? extraBuilder = null;
        StringBuilder? omittedBuilder = null;

        int? minX = null, minY = null, maxX2 = null, maxY2 = null;

        for (var k = start; k < end; k++)
        {
            var dto = dtos[k];

            if (dto.Name is { Length: > 0 } name)
            {
                nameBuilder ??= new StringBuilder();
                if (nameBuilder.Length > 0) nameBuilder.Append(' ');
                nameBuilder.Append(name);
            }

            if (dto.Text is { Length: > 0 } text)
            {
                textBuilder ??= new StringBuilder();
                if (textBuilder.Length > 0) textBuilder.Append(' ');
                textBuilder.Append(text);
            }

            if (dto.Extra is { Length: > 0 } extra)
            {
                extraBuilder ??= new StringBuilder();
                if (extraBuilder.Length > 0) extraBuilder.Append(',');
                extraBuilder.Append(extra);
            }

            // Merge omitted markers from individual labels (union of all flags)
            if (dto.Omitted is { Length: > 0 } omitted)
            {
                if (omittedBuilder is null)
                {
                    omittedBuilder = new StringBuilder(omitted);
                }
                else
                {
                    foreach (var part in omitted.Split(','))
                    {
                        if (!omittedBuilder.ToString().Contains(part, StringComparison.Ordinal))
                        {
                            omittedBuilder.Append(',').Append(part);
                        }
                    }
                }
            }

            if (dto.Box is not null)
            {
                var parts = dto.Box.Split(',');
                if (parts.Length == 4
                    && int.TryParse(parts[0], out var x)
                    && int.TryParse(parts[1], out var y)
                    && int.TryParse(parts[2], out var w)
                    && int.TryParse(parts[3], out var h))
                {
                    var x2 = x + w;
                    var y2 = y + h;
                    minX = minX is null ? x : Math.Min(minX.Value, x);
                    minY = minY is null ? y : Math.Min(minY.Value, y);
                    maxX2 = maxX2 is null ? x2 : Math.Max(maxX2.Value, x2);
                    maxY2 = maxY2 is null ? y2 : Math.Max(maxY2.Value, y2);
                }
            }
        }

        return new VisualElementDto
        {
            Id = first.Id,
            Type = first.Type,
            Name = nameBuilder?.ToString(),
            Text = textBuilder?.ToString(),
            Box = minX is not null ? $"{minX},{minY},{maxX2!.Value - minX.Value},{maxY2!.Value - minY!.Value}" : null,
            Extra = extraBuilder?.ToString(),
            Children = null,
            Omitted = omittedBuilder?.ToString()
        };
    }

    /// <summary>
    /// Creates a single <see cref="VisualElementDto"/> for the given visual element.
    /// Secondary metadata (importance flag, TopLevel process info, window handle)
    /// is assembled into the compact <see cref="VisualElementDto.Extra"/> string.
    /// </summary>
    private VisualElementDto CreateElementDto(
        IVisualElement element,
        VisualElementType elementType,
        int id,
        string? description,
        IReadOnlyList<string>? contentLines,
        bool isImportant,
        List<VisualElementDto>? children,
        string? omitted = null)
    {
        // Build Box
        string? box = null;
        if (ShouldIncludeBounds(detailLevel, elementType))
        {
            var bounds = element.BoundingRectangle;
            box = $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}";
        }

        // Build Extra — assemble all secondary metadata into a compact string
        var extraPartsBuilder = new StringBuilder();
        if (isImportant) extraPartsBuilder.Append("!important");
        if (elementType == VisualElementType.TopLevel)
        {
            var processId = element.ProcessId;
            if (processId > 0)
            {
                AppendExtraPart("pid:").Append(processId);
                try
                {
                    using var process = Process.GetProcessById(processId);
                    AppendExtraPart("process:").Append(process.ProcessName);
                }
                catch
                {
                    // Ignore if process not found
                }
            }

            var windowHandle = element.NativeWindowHandle;
            if (windowHandle > 0) AppendExtraPart("hwnd:0x").Append(windowHandle.ToString("X"));
        }

        return new VisualElementDto(
            id,
            elementType,
            description,
            contentLines is { Count: > 0 } ? string.Join('\n', contentLines) : null,
            box,
            extraPartsBuilder.Length > 0 ? extraPartsBuilder.ToString() : null,
            children,
            omitted);

        StringBuilder AppendExtraPart(string part)
        {
            if (extraPartsBuilder.Length > 0) extraPartsBuilder.Append(',');
            return extraPartsBuilder.Append(part);
        }
    }

    private static bool ShouldIncludeBounds(VisualTreeDetailLevel detailLevel, VisualElementType type) => detailLevel switch
    {
        VisualTreeDetailLevel.Detailed => true,
        VisualTreeDetailLevel.Compact when type is
            VisualElementType.TextEdit or
            VisualElementType.Button or
            VisualElementType.CheckBox or
            VisualElementType.ListView or
            VisualElementType.TreeView or
            VisualElementType.DataGrid or
            VisualElementType.TabControl or
            VisualElementType.Table or
            VisualElementType.Document or
            VisualElementType.TopLevel or
            VisualElementType.Screen => true,
        VisualTreeDetailLevel.Minimal when type is
            VisualElementType.TopLevel or
            VisualElementType.Screen => true,
        _ => false
    };

    private static string TruncateIfNeeded(string text, int maxLength, out bool wasTruncated)
    {
        var tokenCount = EstimateTokenCount(text);
        if (maxLength <= 0 || tokenCount <= maxLength)
        {
            wasTruncated = false;
            return text;
        }

        wasTruncated = true;
        var approximateLength = text.Length * maxLength / tokenCount;
        return text[..Math.Max(0, approximateLength - 2)] + "...omitted";
    }

    /// <summary>
    /// Computes the omission marker string for a visual element based on its omission state.
    /// Returns <c>null</c> when nothing is omitted (no overhead in serialized output).
    /// </summary>
    private static string? GetOmittedMarker(bool hasOmittedChildren, bool isContentTruncated) =>
        (hasOmittedChildren, isContentTruncated) switch
        {
            (true, true) => "children,content",
            (true, false) => "children",
            (false, true) => "content",
            _ => null
        };

    /// <summary>
    /// Propagates the information that a child is informative up the tree.
    /// This may cause ancestors to become active (rendered) if they meet the criteria for the current <see cref="detailLevel"/>.
    /// </summary>
    private void PropagateInformativeUpdate(VisualElementNode? parent, ref int accumulatedTokenCount)
    {
        while (parent != null)
        {
            parent.InformativeChildCount++;

            var wasActive = parent.IsVisible;
            var wasHasInfo = parent.HasInformativeDescendants;

            parent.HasInformativeDescendants = true;

            // Check if activation state changes based on the new child count
            UpdateActivationState(parent);

            if (!wasActive && parent.IsVisible)
            {
                // Parent just became active, so we must pay for its structure tokens.
                accumulatedTokenCount += parent.TokenCount;
                // Note: ContentTokenCount is 0 for non-self-informative elements, so we don't add it.
            }

            // If the parent already had informative descendants, we don't need to propagate the "existence" of info further up.
            // The ancestors already know this branch is informative.
            // However, we DO need to continue if the parent's activation state changed, because that might affect token count?
            // No, token count is updated locally.
            // Does parent activation affect grandparent activation?
            // Grandparent activation depends on grandparent.InformativeChildCount.
            // Grandparent.InformativeChildCount counts children that are "informative" (HasInformativeContent).
            // HasInformativeContent = IsSelfInformative || HasInformativeDescendants.
            // Since parent.HasInformativeDescendants was already true (if wasHasInfo is true), 
            // parent was already contributing to grandparent's InformativeChildCount.
            // So grandparent's count doesn't change.

            if (wasHasInfo) break;

            parent = parent.Parent;
        }
    }

    /// <summary>
    /// Updates the <see cref="VisualElementNode.IsVisible"/> state of an element based on the current <see cref="detailLevel"/>
    /// and its informative status.
    /// </summary>
    private void UpdateActivationState(VisualElementNode element)
    {
        // If it's self-informative, it's always active.
        if (element.IsSelfInformative)
        {
            element.IsVisible = true;
            return;
        }

        // Otherwise, it depends on the detail level and children.
        var shouldRender = detailLevel switch
        {
            VisualTreeDetailLevel.Compact => ShouldKeepContainerForCompact(element),
            VisualTreeDetailLevel.Minimal => ShouldKeepContainerForMinimal(element),
            // For Detailed, we render if there are any informative descendants.
            _ => element.HasInformativeDescendants
        };

        element.IsVisible = shouldRender;
    }

    private static bool ShouldKeepContainerForCompact(VisualElementNode element)
    {
        if (element.Parent is null) return element.InformativeChildCount > 0;

        return element.Type switch
        {
            VisualElementType.Screen or VisualElementType.TopLevel => element.InformativeChildCount > 1,
            VisualElementType.Document => element.InformativeChildCount > 0,
            VisualElementType.Panel => element.InformativeChildCount > 1,
            _ => false
        };
    }

    private static bool ShouldKeepContainerForMinimal(VisualElementNode element)
    {
        if (element.Parent is null)
        {
            return element.InformativeChildCount > 0;
        }

        return false;
    }

    private static bool IsInteractiveElement(IVisualElement element)
    {
        if (element.Type is VisualElementType.Button or
            VisualElementType.Hyperlink or
            VisualElementType.CheckBox or
            VisualElementType.RadioButton or
            VisualElementType.ComboBox or
            VisualElementType.ListView or
            VisualElementType.ListViewItem or
            VisualElementType.TreeView or
            VisualElementType.TreeViewItem or
            VisualElementType.DataGrid or
            VisualElementType.DataGridItem or
            VisualElementType.TabControl or
            VisualElementType.TabItem or
            VisualElementType.Menu or
            VisualElementType.MenuItem or
            VisualElementType.Slider or
            VisualElementType.ScrollBar or
            VisualElementType.ProgressBar or
            VisualElementType.TextEdit or
            VisualElementType.Table or
            VisualElementType.TableRow) return true;

        return (element.States & InteractiveStates) != 0;
    }

    // The token-to-word ratio for English/Latin-based text.
    private const double EnglishTokenRatio = 3.0;

    // The token-to-character ratio for CJK-based text.
    private const double CjkTokenRatio = 2.0;

    /// <summary>
    ///     Approximates the number of LLM tokens for a given string.
    ///     This method first detects the language family of the string and then applies the corresponding heuristic.
    /// </summary>
    /// <param name="text">The input string to calculate the token count for.</param>
    /// <returns>An approximate number of tokens.</returns>
    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return IsCjkLanguage(text) ? (int)Math.Ceiling(text.Length * CjkTokenRatio) : (int)Math.Ceiling(CountWords(text) * EnglishTokenRatio);
    }

    /// <summary>
    ///     Detects if a string is predominantly composed of CJK characters.
    ///     This method makes a judgment by calculating the proportion of CJK characters.
    /// </summary>
    /// <param name="text">The string to be checked.</param>
    /// <returns>True if the string is mainly CJK, false otherwise.</returns>
    private static bool IsCjkLanguage(string text)
    {
        var cjkCount = 0;
        var totalChars = 0;

        foreach (var c in text.AsValueEnumerable().Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)))
        {
            totalChars++;
            // Use regex to match CJK characters
            if (CjkRegex().IsMatch(c.ToString()))
            {
                cjkCount++;
            }
        }

        // Set a threshold: if the proportion of CJK characters exceeds 10%, it is considered a CJK language.
        return totalChars > 0 && (double)cjkCount / totalChars > 0.1;
    }

    /// <summary>
    ///     Counts the number of words in a string using a regular expression.
    ///     This method matches sequences of non-whitespace characters to provide a more accurate word count than simple splitting.
    /// </summary>
    /// <param name="s">The string in which to count words.</param>
    /// <returns>The number of words.</returns>
    private static int CountWords(string s)
    {
        // Matches one or more non-whitespace characters, considered as a single word.
        var collection = WordCountRegex().Matches(s);
        return collection.Count;
    }

    /// <summary>
    ///     Regex to match CJK characters, including Chinese, Japanese, and Korean.
    /// </summary>
    /// <returns></returns>
    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}|\p{IsCJKCompatibility}|\p{IsHangulJamo}|\p{IsHangulSyllables}|\p{IsHangulCompatibilityJamo}")]
    private static partial Regex CjkRegex();

    /// <summary>
    ///     Regex to match words (sequences of non-whitespace characters).
    /// </summary>
    /// <returns></returns>
    [GeneratedRegex(@"\S+")]
    private static partial Regex WordCountRegex();
}