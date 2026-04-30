# Architectural Blueprint & Refactoring Plan: Visual Context Pipeline for LLM Agents

## 1. Problem Statement & Motivation
The existing VisualContextBuilder has grown into a tightly coupled monolithic system, entangling Graph Traversal (Weighted BFS), Token Calculation, Visibility Propagation, and Format Serialization (XML/JSON/TOON) within a single pass. 

We must move to a **multi-stage compiler-like pipeline**. The goals are:
1. **Defend against Extreme Scale & Deadlocks:** Stop OS-level COM hangs or millions-of-nodes (e.g., lists) from crashing the extraction.
2. **Preserve the Core Heuristics:** The existing Weighted BFS, TraverseDistance decay, and VisitedElements deduplication are finely tuned and *must be preserved*.
3. **Decouple ID Assignment:** Virtual sequential IDs (int) should only be generated at the *very end* for nodes that actually survive rendering.
4. **Context Density with Traceability:** Merge fragmented nodes and omit non-essentials into a dense Markdown format, while leaving embedded pointers (`[...omit...](#id)`) so the LLM can trace back.

## 2. Core Domain Model (Code Specification)
We replace the messy mutable classes and DTOs with a clean, unified tree structure. Per our consensus, we use a `sealed class` to hold all relevant states, maintaining simplicity and eliminating mapping overhead.

```csharp
namespace Everywhere.Chat.VisualContext.Dom;

public sealed class VisualContextNode
{
    // Core Reference (Immutable after extraction)
    public required IVisualElement Element { get; init; }
    public VisualContextNode? Parent { get; set; }
    public List<VisualContextNode>? Children { get; set; }

    // Basic Visual & Text Properties
    public VisualElementType Type { get; init; }
    public string? Description { get; init; }
    public List<string>? ContentLines { get; set; }

    // Extension Property Slot (for DataAugmentor to inject RealPath, XPath, etc. replacing DTOs)
    public Dictionary<string, object> Properties { get; } = new();
    
    // Visibility cleanup flag
    public bool IsVisible { get; set; } = true;
    
    // Extraction metrics for Truncator weighting
    public int LocalDistance { get; init; }
    public int GlobalDistance { get; init; }

    // --- Omission Flags (No 'Fake' Nodes) ---
    // We strictly use flags rather than injecting "virtual" omission nodes (e.g., a dummy node 
    // representing "3 hidden items"). This maintains a pure 1:1 AST mapping with the real UI tree,
    // explicitly avoiding ID mapping discrepancies and complex type-checking during middleware passes.
    public bool HasOmittedChildren { get; set; }
    public OmissionReason ChildrenOmittedReason { get; set; }
    public bool IsContentTrimmed { get; set; }
    
    // Logical Merging: Stores references to adjacent sibling nodes that have been 
    // logically grouped into this node by the SmartMerger. The actual text concatenation 
    // and cropping are deferred to the Truncator and Renderer stages.
    public List<VisualContextNode>? MergedSiblings { get; set; }
    
    // Token cost of the omitted content/children to inform the formatter.
    public int PrunedTokenCost { get; set; } = -1;
    
    // Note: Do NOT add `int Id` here! Sequential IDs are assigned strictly in the final Renderer stage.

    public List<VisualContextNode> EnsureChildren() => Children ??= [];
}

public enum OmissionReason
{
    None,
    MaxChildrenExceeded,   // Sibling limit breached (Stage 1 Fence)
    TotalNodesExceeded,    // Global nodes limit breached (Stage 1 Fence)
    ExtractorTimeout,      // COM/IPC hang causing timeout (Stage 1 Fence)
    PreciseTokenPruned     // Pruned by the weighted Truncator (Stage 3 Pruner)
}
```

## 3. The 4-Stage Pipeline Architecture

### Stage 1: Extraction & Failsafe Fences (VisualTreeTraverser)
**Goal:** Encase the existing perfectly-tuned Weighted BFS (PriorityQueue & HashSet logic) within defensive fences.

**Long Text Defense:** To ensure a single massive text node (e.g., a 10MB log view) doesn't instantly consume the entire Stage 1 `looseTokenLimit` and starve surrounding structures, the `TokenEstimator` must **cap the max token contribution of any single text node** during traversal. We accept this temporary inaccuracy because Stage 3 will handle the actual exact truncation.

```csharp
namespace Everywhere.Chat.VisualContext.Pipelines;

// Abstract Token Estimator delegate for phase-specific cost calculations
public delegate int TokenEstimator(VisualContextNode node);

public class VisualTreeTraverser
{
    // Core traversal entry point retaining existing BFS semantics.
    // 'coreElements' acts as the distance=0 focal seeds for the PriorityQueue.
    public VisualContextNode Traverse(
        IReadOnlyList<IVisualElement> coreElements,
        int looseTokenLimit,
        TokenEstimator looseEstimator,
        CancellationToken cancellationToken)
    {
        // TODO: Port BFS from VisualContextBuilder.Traversal.cs (PriorityQueue & HashSet deduplication)
        // TODO: Check Multi-Layered Fences on child expansion:
        //   1. Max Capacity (Total Nodes Limit)
        //   2. Max Children Per Node Limit
        //   3. Traversal Timeout (Global or Per-Root)
        //   4. Loose Token Bounding (using looseEstimator with Max Cap)
        // if (fences.IsBreached) 
        // { 
        //     node.HasOmittedChildren = true; 
        //     node.ChildrenOmittedReason = OmissionReason.MaxChildrenExceeded; // or TotalNodesExceeded / ExtractorTimeout
        //     break; 
        // }
    }
}
```

### Stage 2: Data Augmentation & Cleansing (Middleware)
A standard middleware pattern that mutates the tree sequentially by updating node states/properties directly.

```csharp
public interface IContextProcessor 
{
    void Process(VisualContextNode root);
}

// 1. VisibilityPropagator: Walks tree and sets node.IsVisible = false for hidden items.
// 2. DataAugmentor: Async domain-metadata fetch (e.g., node.Properties["RealFilePath"] = ...)
// 3. SmartMerger: Logically groups fragmented adjacent text/labels (non-interactive elements). 
//    IT DOES NOT MANIPULATE STRINGS. If node B merges into A, it adds B to A.MergedSiblings 
//    and flags B.IsVisible = false.
```

### Stage 3: Exact Target Truncator (The Pruning Engine)
Reduces the processed AST exactly to the hard limit required by LLMs, functioning similar to `Aider`/`ctags` code-pruning.

**Truncation Strategy:**
1. **Weights & Skeleton First:** Nodes are ranked by distance/priority. We allocate tokens to ensure every discovered interactive/focal element gets its "Skeleton" (its signature and ID) preserved.
2. **Text Cropping:** If a node's text exceeds the remaining budget, it drops the remainder and triggers `IsContentTrimmed = true`. 
3. **Traceable Merged Trims:** For logically merged blocks, it aggregates text strictly up to the token boundary. When it cuts off, it definitively identifies which `MergedSibling` corresponds to the cut-off point, using that node's original element ID to weave an explicit boundary marker (e.g. `[...omit...](#original-id)`).

```csharp
public class ContextTruncator
{
    public void Truncate(VisualContextNode root, int preciseLimit, TokenEstimator preciseEstimator)
    {
        // Outward radiation using priority ranking to evaluate limits.
        // Drops over-budget content and child branches safely, prioritizing distant nodes.
        // Sets node.IsContentTrimmed or node.ChildrenOmittedReason accordingly.
    }
}
```

### Stage 4: Renderers (The End of the Line)
The final string output generator. Generates consecutive integers dynamically.

```csharp
public interface IContextRenderer
{
    // Renders the final text and returns an ID mapping for nodes that survived to rendering.
    string Render(VisualContextNode root, out IReadOnlyDictionary<int, IVisualElement> idMap);
}

public class MarkdownRenderer : IContextRenderer 
{
    public string Render(VisualContextNode root, out IReadOnlyDictionary<int, IVisualElement> idMap) 
    {
        // 1. Traverse nodes with IsVisible == true.
        // 2. Assign strictly late-bound consecutive IDs (1, 2, 3...) ONLY for interactive or focal elements.
        // 3. Construct Markdown dynamically (e.g., `[{Id}] {Type}: {Text}`). 
        // 4. Inject inline traceability boundaries when node.IsContentTrimmed or HasOmittedChildren is true.
        //    (e.g., append `[...omit...](#original-element-id)` mapping back to the omitted raw element).
        // 5. Build and populate the `idMap`, which becomes the single source of truth for LLM interactions.
        idMap = new Dictionary<int, IVisualElement>();
        return string.Empty;
    }
}
```

## 4. Execution Plan
1. **Clean Slate:** Introduce `VisualContextNode` wrapped as a `sealed class`. 
2. **Migrate Extractor:** Lift and shift the `Traversal.cs` BFS algorithm into the new `VisualTreeExtractor`, wrapping it in the Fences (Time, Count, Delegate Loose Tokens). 
3. **Build Middleware:** Implement the `SmartMerger` and visibility flags operating over the extracted AST.
4. **Build Truncator:** Implement the exact token limit pruning logic based on the format delegate.
5. **Build Renderers:** Implement late-stage `int` ID assignment mapping and the Markdown/XML string builders.
6. **Swap & Delete:** Point `VisualContextBuilder` to the new pipeline as a Facade class, and begin deleting all legacy JSON/TOON entanglement code.
