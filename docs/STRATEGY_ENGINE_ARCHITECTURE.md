# Strategy Engine Architecture

> A next-generation interaction paradigm for the Everywhere AI Desktop Assistant

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Problem Statement](#2-problem-statement)
3. [Core Concepts](#3-core-concepts)
4. [Architecture Overview](#4-architecture-overview)
5. [Matching System](#5-matching-system)
6. [Command Generation](#6-command-generation)
7. [Configuration Format](#7-configuration-format)
8. [Graph-Based Context Resolution](#8-graph-based-context-resolution)
9. [Implementation Plan](#9-implementation-plan)
10. [API Reference](#10-api-reference)

---

## 1. Executive Summary

The **Strategy Engine** is an extensible, programmable framework that revolutionizes the traditional text-based chat interaction model. Instead of requiring users to type queries, the engine analyzes the current visual context (active application, selected elements, files, etc.) and presents contextually relevant commands that can be executed with a single click.

### Key Innovation

```
Traditional Flow:    User → Type Query → AI Processes → Response
Strategy Engine:     Context → Match Strategies → Generate Commands → User Clicks → AI Executes
```

### Design Principles

| Principle | Description |
|-----------|-------------|
| **Context-First** | Commands are derived from visual context, not user input |
| **Zero-Friction** | One-click access to intelligent actions |
| **Extensible** | Simple configuration files can add new strategies |
| **Composable** | Multiple matching strategies merge their commands |
| **Non-Invasive** | Parallel to existing plugin system, not replacing it |

---

## 2. Problem Statement

### Current Limitations

1. **High Friction**: Users must formulate their intent as text queries
2. **Context Blindness**: Users manually describe what they're looking at
3. **Repetitive Patterns**: Same queries for same contexts (e.g., "translate this page")
4. **Discovery Problem**: Users don't know what the assistant can do

### Target User Experience

```
┌─────────────────────────────────────────────────────────────────┐
│  User is on arxiv.org viewing a research paper                  │
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Everywhere Assistant                              ─ □ × │   │
│  ├─────────────────────────────────────────────────────────┤   │
│  │                                                         │   │
│  │  📄 Detected: Academic Paper (arxiv.org)               │   │
│  │                                                         │   │
│  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐    │   │
│  │  │ 📝 Summarize │ │ 🔬 Explain   │ │ 📚 Find      │    │   │
│  │  │    Paper     │ │   Methods    │ │   Related    │    │   │
│  │  └──────────────┘ └──────────────┘ └──────────────┘    │   │
│  │                                                         │   │
│  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐    │   │
│  │  │ 💬 Translate │ │ ❓ What is   │ │ 📋 Extract   │    │   │
│  │  │   Abstract   │ │    This?     │ │   Citations  │    │   │
│  │  └──────────────┘ └──────────────┘ └──────────────┘    │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

---

## 3. Core Concepts

### 3.1 Terminology

| Term | Definition |
|------|------------|
| **Strategy** | A rule set that matches contexts and produces commands |
| **Condition** | A predicate that evaluates against context (visual elements, attachments) |
| **Command** | An actionable item presented to the user |
| **Context** | Current state: attachments, visual elements, active process, etc. |
| **Attachment** | User-provided context: selected text, files, visual elements |

### 3.2 Context Composition

The Strategy Engine operates on a `StrategyContext` composed of:

```csharp
public record StrategyContext
{
    /// <summary>
    /// User-provided attachments (files, text selections, visual elements).
    /// Use ChatAttachment.IsPrimary to identify focused items (0 or more can be primary).
    /// </summary>
    public IReadOnlyList<ChatAttachment> Attachments { get; init; }
    
    /// <summary>
    /// Root visual elements derived from attachments.
    /// Every visual element's ancestor chain ends at Screen or TopLevel.
    /// Strategy matching follows paths from these roots downward.
    /// </summary>
    public IReadOnlyList<IVisualElement> RootElements { get; init; }
    
    /// <summary>
    /// Active process information (may be null if no visual elements).
    /// </summary>
    public ProcessInfo? ActiveProcess { get; init; }
    
    /// <summary>
    /// Additional metadata for custom matching logic.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; }
}
```

### 3.3 Strategy Hierarchy

```
┌─────────────────────────────────────────────────────────────┐
│                     Global Strategies                        │
│         (Always available: "What is this?", "Explain")       │
├─────────────────────────────────────────────────────────────┤
│                   Application Strategies                     │
│        (Process-specific: Browser, IDE, PDF Reader)          │
├─────────────────────────────────────────────────────────────┤
│                    Content Strategies                        │
│      (Content-specific: Academic paper, Code, Image)         │
├─────────────────────────────────────────────────────────────┤
│                     User Strategies                          │
│          (Custom user-defined rules and commands)            │
└─────────────────────────────────────────────────────────────┘
```

All matching strategies are **merged** (not overridden), producing a combined command list.

---

## 4. Architecture Overview

### 4.1 System Components

```
┌──────────────────────────────────────────────────────────────────────────┐
│                           Strategy Engine                                 │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐      │
│  │  Context        │    │   Strategy      │    │   Command       │      │
│  │  Collector      │───▶│   Matcher       │───▶│   Generator     │      │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘      │
│          │                      │                      │                 │
│          ▼                      ▼                      ▼                 │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐      │
│  │ Attachment      │    │   Strategy      │    │   Command       │      │
│  │ Processor       │    │   Registry      │    │   Executor      │      │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘      │
│                                                        │                 │
│                                                        ▼                 │
│                                              ┌─────────────────┐        │
│                                              │   Agent         │        │
│                                              │   Session       │        │
│                                              └─────────────────┘        │
│                                                                          │
└──────────────────────────────────────────────────────────────────────────┘
```

### 4.2 Component Responsibilities

| Component | Responsibility |
|-----------|----------------|
| **Context Collector** | Gathers current state: attachments, visual tree, process info |
| **Attachment Processor** | Analyzes attachments and extracts matchable properties |
| **Strategy Matcher** | Evaluates conditions against context, finds matching strategies |
| **Strategy Registry** | Stores and indexes all registered strategies |
| **Command Generator** | Produces commands from matched strategies |
| **Command Executor** | Launches agent session with configured prompt/tools |
| **Agent Session** | Full agent conversation with pre-configured context |

### 4.3 Data Flow

**Trigger Points:**
- User opens/triggers the assistant window
- Attachment list changes (add/remove/modify)
- Any context change that requires re-evaluation

All triggers invoke the same `EvaluateStrategiesAsync()` method:

```
Context Change (trigger)
         │
         ▼
┌─────────────────────────────────────────────────────────────┐
│ 1. COLLECT CONTEXT                                          │
│    - Get ChatAttachment list (files, selections, elements)  │
│    - Get active process info                                │
│    - Derive RootElements from attachment visual elements    │
└─────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────┐
│ 2. MATCH STRATEGIES                                         │
│    - Iterate all registered strategies                      │
│    - Evaluate each strategy's conditions                    │
│    - Collect all matching strategies (no short-circuit)     │
└─────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────┐
│ 3. GENERATE COMMANDS                                        │
│    - Each strategy produces its commands                    │
│    - Merge and deduplicate commands                         │
│    - Sort by relevance/priority                             │
└─────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────┐
│ 4. DISPLAY TO USER                                          │
│    - Show command buttons/cards in UI                       │
│    - User clicks desired command                            │
└─────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────┐
│ 5. EXECUTE COMMAND                                          │
│    - Apply command's prompt template                        │
│    - Configure allowed tools                                │
│    - Inject context into agent                              │
│    - Start full agent session                               │
└─────────────────────────────────────────────────────────────┘
```

### 4.4 Integration with Existing Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│                        Everywhere.Core                              │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│  ┌──────────────────┐          ┌──────────────────┐               │
│  │  ChatService     │◀────────▶│  Strategy Engine │               │
│  │  (Agent Core)    │          │  (Context Match) │               │
│  └──────────────────┘          └──────────────────┘               │
│          │                              │                          │
│          ▼                              ▼                          │
│  ┌──────────────────┐          ┌──────────────────┐               │
│  │ ChatPluginManager│          │ StrategyRegistry │               │
│  │ (Tool Registry)  │          │ (Rule Registry)  │               │
│  └──────────────────┘          └──────────────────┘               │
│          │                              │                          │
│          ▼                              ▼                          │
│  ┌──────────────────┐          ┌──────────────────┐               │
│  │VisualContextPlugin          │ Built-in         │               │
│  │FileSystemPlugin  │          │ Strategies       │               │
│  │WebSearchPlugin   │          │ (Global/App)     │               │
│  └──────────────────┘          └──────────────────┘               │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

**Key Integration Points:**

1. **ChatService**: Strategy Engine uses `IChatService` to start agent sessions
2. **ChatPluginManager**: Commands can specify which plugins/tools to enable
3. **IVisualElementContext**: Provides visual element access for matching
4. **ChatAttachment**: Input source for context matching

---

## 5. Matching System

### 5.1 Condition Types

Based on `IVisualElement` properties and `ChatAttachment` types:

#### 5.1.1 Visual Element Conditions

| Property | Condition Type | Example |
|----------|---------------|---------|
| `Type` | Enum match | `Type == TextEdit` |
| `States` | Flag check | `States.HasFlag(Focused)` |
| `Name` | Regex/Glob | `Name ~ ".*address.*"` |
| `ProcessId` | Process lookup | `ProcessName == "chrome"` |
| `BoundingRectangle` | Geometry | `Width > 500` |
| `GetText()` | Content match | `Text ~ "arxiv\.org"` |

#### 5.1.2 Attachment Conditions

| Attachment Type | Matchable Properties |
|-----------------|---------------------|
| `ChatVisualElementAttachment` | Element properties + Content |
| `ChatTextSelectionAttachment` | Selected text content |
| `ChatTextAttachment` | Text content |
| `ChatFileAttachment` | File path, extension, MIME type |

#### 5.1.3 Process Conditions

| Property | Description |
|----------|-------------|
| `ProcessName` | Executable name (e.g., "chrome", "code") |
| `MainWindowTitle` | Window title text |
| `ProcessPath` | Full executable path |

### 5.2 Condition Expression Language

A simple DSL for expressing conditions:

```yaml
# Simple property match
condition: "element.Type == 'Document'"

# Regex match
condition: "element.Name =~ 'arxiv|springer|ieee'"

# Multiple conditions (AND)
conditions:
  - "process.Name == 'chrome'"
  - "element.Text =~ '.*\\.pdf$'"

# Complex expression
condition: |
  (element.Type == 'Document' OR element.Type == 'TextEdit')
  AND process.Name IN ['chrome', 'firefox', 'edge']
```

### 5.3 XPath-like Visual Tree Queries

For complex visual tree matching, support XPath-inspired queries:

```
# Find any TextEdit descendant of a Panel
//Panel/TextEdit

# Find Document with specific name pattern
//Document[@Name =~ 'arxiv']

# Find focused element within browser
//TopLevel[@ProcessName = 'chrome']//[@Focused]

# Ancestor check (is element inside a specific container?)
ancestor::DataGrid

# Sibling check
preceding-sibling::Button
```

### 5.4 Condition Evaluation Pipeline

```
┌─────────────────────────────────────────────────────────────┐
│                  Condition Evaluation                        │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  Input: StrategyContext                                     │
│                                                             │
│  Step 1: Quick Filters (Process name, attachment types)    │
│          └─ Early exit if no match                          │
│                                                             │
│  Step 2: Property Matchers (Type, States, Name)            │
│          └─ Evaluate against primary attachment             │
│                                                             │
│  Step 3: Content Matchers (Text regex, URL patterns)       │
│          └─ May require GetText() calls                     │
│                                                             │
│  Step 4: Graph Queries (Ancestor/Descendant checks)        │
│          └─ Visual tree traversal (see Section 8)           │
│                                                             │
│  Output: bool (match or no match)                           │
│                                                             │
└─────────────────────────────────────────────────────────────┘

---

## 6. Command Generation

### 6.1 Command Structure

```csharp
public record StrategyCommand
{
    /// <summary>
    /// Unique identifier for deduplication
    /// </summary>
    public required string Id { get; init; }
    
    /// <summary>
    /// Display name (supports i18n keys)
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Optional description
    /// </summary>
    public string? Description { get; init; }
    
    /// <summary>
    /// Icon for UI display
    /// </summary>
    public LucideIconKind Icon { get; init; }
    
    /// <summary>
    /// Priority for sorting (higher = more prominent)
    /// </summary>
    public int Priority { get; init; }
    
    /// <summary>
    /// System prompt template (supports variable interpolation)
    /// </summary>
    public string? SystemPrompt { get; init; }
    
    /// <summary>
    /// User message template (auto-sent to start conversation)
    /// </summary>
    public string? UserMessage { get; init; }
    
    /// <summary>
    /// Allowed tools/plugins for this command
    /// null = use default, empty = no tools
    /// </summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
    
    /// <summary>
    /// MCP servers to enable
    /// </summary>
    public IReadOnlyList<string>? McpServers { get; init; }
    
    /// <summary>
    /// Context variables available for prompt interpolation
    /// </summary>
    public IReadOnlyDictionary<string, object>? Variables { get; init; }
}
```

### 6.2 Prompt Template Variables

Commands can use template variables that are resolved at execution time:

| Variable | Description | Example Value |
|----------|-------------|---------------|
| `{{selected_text}}` | User's text selection | "The quick brown fox..." |
| `{{element_content}}` | Visual element's text | "Page content here..." |
| `{{element_type}}` | Visual element type | "Document" |
| `{{file_path}}` | Attached file path | "/path/to/file.pdf" |
| `{{file_content}}` | File contents (if text) | "File text..." |
| `{{process_name}}` | Active process name | "chrome" |
| `{{window_title}}` | Window title | "arxiv.org - Paper" |
| `{{url}}` | Detected URL (if any) | "https://arxiv.org/..." |
| `{{clipboard}}` | Clipboard content | "Copied text" |

### 6.3 Command Merging Strategy

When multiple strategies match:

```
Strategy A Commands: [Translate, Summarize]
Strategy B Commands: [Summarize, Explain]
Strategy C Commands: [What is this?]
                          │
                          ▼
┌─────────────────────────────────────────────────────────────┐
│                    MERGE ALGORITHM                           │
│                                                             │
│  1. Collect all commands from all matching strategies       │
│  2. Group by Command.Id                                     │
│  3. For duplicates: keep highest priority version           │
│  4. Sort final list by priority (descending)                │
│  5. Apply max display limit (e.g., top 8 commands)          │
│                                                             │
└─────────────────────────────────────────────────────────────┘
                          │
                          ▼
Final Commands: [What is this?, Translate, Summarize, Explain]
                 (sorted by priority)
```

### 6.4 Built-in Global Commands

Always-available commands regardless of context:

| Command | Description |
|---------|-------------|
| **What is this?** | Explain the current visual element/selection |
| **Summarize** | Summarize the visible content |
| **Help me with this** | Open-ended assistance with current context |

These have lower priority and are shown when no context-specific commands match.

---

## 7. Configuration Format

### 7.1 Condition Logic Model

Strategies support flexible condition logic with **AND/OR** combinations:

```yaml
# AND logic: all conditions must match
conditions:
  all:
    - condition1
    - condition2

# OR logic: any condition can match  
conditions:
  any:
    - condition1
    - condition2

# Mixed: groups are OR'd, conditions within group are AND'd
conditionGroups:
  - # Group 1 (all must match)
      condition1
      condition2
  - # Group 2 (all must match)
      condition3
# Strategy matches if Group1 OR Group2 matches
```

### 7.2 Attachment Type Conditions

Three attachment types are supported with type-specific matchers:

#### 7.2.1 ChatVisualElementAttachment

```yaml
conditions:
  any:
    - type: VisualElement
      # Element's own properties
      elementType: ["Document", "TextEdit"]  # VisualElementType enum
      elementStates: ["Focused"]              # Required states
      elementName: ".*content.*"              # Regex on Name
      elementText: "arxiv\\.org"              # Regex on GetText()
      
      # Process context
      processName: ["chrome", "firefox"]
      
      # Ancestor path check (is element inside this container?)
      ancestorQuery: "TopLevel[ProcessName ~= 'chrome']"
      
      # Cross-path query (probe other branches of the tree)
      probeQueries:
        - query: "^TopLevel > ToolBar TextEdit[Text ~= 'https://']"
          required: true
```

#### 7.2.2 ChatTextAttachment / ChatTextSelectionAttachment

```yaml
conditions:
  any:
    - type: TextSelection
      # Text content matching
      textPattern: "^https?://"              # Regex on selected text
      textMinLength: 10                       # Minimum text length
      textMaxLength: 5000                     # Maximum text length
      
      # Language detection (future)
      # language: ["en", "zh"]
      
    - type: Text
      textPattern: "error|exception|failed"
      textContains: ["stack trace"]          # Simple contains check
```

#### 7.2.3 ChatFileAttachment

```yaml
conditions:
  any:
    - type: File
      # Path matching
      pathPattern: ".*\\.pdf$"               # Regex on full path
      pathContains: ["Downloads", "Documents"]
      
      # Extension matching
      extensions: [".pdf", ".docx", ".doc"]
      
      # File metadata
      minSize: 1024                          # Minimum file size (bytes)
      maxSize: 10485760                      # Maximum file size (10MB)
      
      # Count constraints
      minCount: 1                            # At least 1 file
      maxCount: 5                            # At most 5 files
```

### 7.3 Complete Strategy Example

```yaml
# ~/.everywhere/strategies/arxiv-helper.yaml

id: "arxiv-paper-assistant"
name: "arXiv Paper Assistant"
description: "Commands for viewing academic papers on arXiv"
priority: 100
enabled: true

# OR logic: match if ANY condition group is satisfied
conditionGroups:
  # Group 1: Browser with arxiv page (via address bar probe)
  - all:
      - type: VisualElement
        processName: ["chrome", "firefox", "edge", "safari"]
        probeQueries:
          - query: "^TopLevel TextEdit[Name ~= 'address|url', Text ~= 'arxiv\\.org']"
            required: true
  
  # Group 2: PDF file with arxiv in name
  - all:
      - type: File
        extensions: [".pdf"]
        pathPattern: ".*arxiv.*"
  
  # Group 3: Selected text containing arxiv URL
  - all:
      - type: TextSelection
        textPattern: "arxiv\\.org/abs/\\d+"

commands:
  - id: "summarize-paper"
    name: "Summarize Paper"
    icon: "FileText"
    priority: 100
    systemPrompt: |
      You are an academic paper summarization assistant.
      Provide a structured summary including:
      - Main contribution
      - Key methods  
      - Results and conclusions
    userMessage: "Please summarize this academic paper."
    tools: ["web_search", "visual_context"]

  - id: "explain-methods"
    name: "Explain Methodology"
    icon: "FlaskConical"
    priority: 90
    userMessage: "Explain the methodology of this paper in simple terms."

  - id: "find-related"
    name: "Find Related Papers"
    icon: "Search"
    priority: 80
    tools: ["web_search"]
    userMessage: "Find related papers to this one."
```

### 7.4 Multi-Attachment Strategies

Strategies can require combinations of different attachment types:

```yaml
id: "code-review-assistant"
name: "Code Review Helper"

conditionGroups:
  # Group 1: VS Code with code file attachment
  - all:
      - type: VisualElement
        processName: "code"
        elementType: ["TextEdit", "Document"]
      - type: File
        extensions: [".py", ".ts", ".js", ".cs", ".java"]
        minCount: 1
  
  # Group 2: GitHub PR page
  - all:
      - type: VisualElement
        processName: ["chrome", "firefox"]
        probeQueries:
          - query: "^TopLevel [Text ~= 'github\\.com.*pull']"
            required: true
  
  # Group 3: Just a code file (no visual context needed)
  - all:
      - type: File
        extensions: [".py", ".ts", ".js", ".cs", ".java", ".go", ".rs"]
        minCount: 1

commands:
  - id: "review-code"
    name: "Review This Code"
    priority: 100
```

### 7.5 Mixed Attachment Example

```yaml
id: "file-organizer"
name: "Smart File Organizer"

conditionGroups:
  # Need: File Explorer window + file attachments
  - all:
      - type: VisualElement
        processName: ["explorer", "finder", "nautilus"]
      - type: File
        minCount: 1
  
  # Alternative: Just file attachments with common extensions
  - all:
      - type: File
        extensions: [".pdf", ".docx", ".xlsx", ".pptx"]
        minCount: 2

commands:
  - id: "organize-files"
    name: "Organize These Files"
  - id: "rename-batch"
    name: "Smart Rename"
```

### 7.6 Attachment-Specific Conditions
    priority: 100
```

### 7.3 Attachment-Specific Conditions

```yaml
id: "file-handler"
name: "Smart File Handler"

# Match based on attached files
conditions:
  attachments:
    # Require at least one file attachment
    hasFile: true
    # Optional: specific types
    fileTypes:
      - extensions: [".pdf"]
        commands: ["extract-text", "summarize-pdf"]
      - extensions: [".png", ".jpg", ".jpeg"]
        commands: ["describe-image", "extract-text-ocr"]
      - extensions: [".csv", ".xlsx"]
        commands: ["analyze-data", "visualize"]
```

### 7.4 Dynamic Command Generation

For advanced use cases, strategies can define command templates:

```yaml
id: "dynamic-translator"
name: "Context-Aware Translator"

conditions:
  attachment:
    hasTextSelection: true

# Dynamic commands based on detected language
commandTemplate:
  detect: "language"  # Detect source language from selection
  generate:
    - id: "translate-to-{{targetLang}}"
      name: "Translate to {{targetLangName}}"
      icon: "Languages"
      userMessage: "Translate the selected text to {{targetLangName}}."
  
  # Available target languages
  targets:
    - { targetLang: "en", targetLangName: "English" }
    - { targetLang: "zh", targetLangName: "Chinese" }
    - { targetLang: "ja", targetLangName: "Japanese" }
```

### 7.5 Configuration Directory Structure

```
~/.everywhere/
├── strategies/
│   ├── builtin/           # Read-only, shipped with app
│   │   ├── global.yaml
│   │   ├── browser.yaml
│   │   └── code-editors.yaml
│   ├── user/              # User-defined strategies
│   │   ├── my-workflow.yaml
│   │   └── work-tools.yaml
│   └── community/         # Downloaded from community
│       └── arxiv-helper.yaml
└── strategy-settings.yaml # Global settings
```

---

## 8. Cross-Path Element Querying

### 8.1 The Core Challenge

The key problem is **not** finding the Lowest Common Ancestor (LCA), but rather:

1. **Cross-path queries**: Attachment is a browser document, but we need to check the address bar (a sibling subtree)
2. **Visual tree instability**: Element IDs may be random, child indices may change between sessions
3. **Efficient traversal**: Cannot traverse entire window's descendants for every strategy check

**Example Scenario:**

```
TopLevel (Browser)
├── ToolBar
│   ├── BackButton
│   ├── ForwardButton
│   └── AddressBar ← We want to check: text =~ "arxiv.org"
│       └── TextEdit (URL text)
├── Panel (Content Area)
│   └── Document ← User's attachment (selected element)
│       └── ... (page content)
└── StatusBar
```

The user selected the `Document`, but the strategy needs to verify the URL in `AddressBar`.

### 8.2 Solution: Resilient Element Selectors

Instead of relying on unstable properties (ID, index), use **semantic selectors** based on stable characteristics:

| Selector Type | Description | Stability |
|---------------|-------------|-----------|
| `Type` | Element type enum | ⭐⭐⭐ Very stable |
| `Name` pattern | Regex on Name property | ⭐⭐ Usually stable |
| `Text` pattern | Regex on GetText() | ⭐⭐ Content-dependent |
| `States` | Flag matching | ⭐⭐⭐ Very stable |
| `ProcessName` | Process filter | ⭐⭐⭐ Very stable |
| Relative position | First/Last/Nth of type | ⭐ Less stable |

### 8.3 Query Language Design

A CSS/XPath-inspired query language for visual elements:

```
# Basic syntax
Selector := TypeSelector? AttributeFilters? Combinator?

# Type selector (optional, defaults to any)
TypeSelector := VisualElementType    # e.g., TextEdit, Document, Panel

# Attribute filters
AttributeFilters := '[' Filter (',' Filter)* ']'
Filter := PropertyName Operator Value

# Operators
Operator := '=' | '!=' | '~=' (regex) | '^=' (starts) | '$=' (ends) | '*=' (contains)

# Combinators
' '   - Descendant (any depth)
'>'   - Direct child
'~'   - Sibling (any)
'+'   - Adjacent sibling
'^'   - Ancestor (reverse traversal)
```

**Examples:**

```yaml
# Find address bar text in browser (from any starting point)
query: "^TopLevel > ToolBar TextEdit[Name ~= 'address|url|location']"

# Explanation:
# ^TopLevel     - Go up to the TopLevel ancestor
# > ToolBar     - Direct child of type ToolBar
# TextEdit      - Any descendant TextEdit
# [Name ~= ...] - Where Name matches regex

# Find any focused element in same window
query: "^TopLevel [States.Focused]"

# Check if we're in a DataGrid row
query: "^DataGrid"  # True if any ancestor is DataGrid

# Find sibling tabs
query: "~ TabItem"
```

### 8.4 Query Execution Algorithm

```python
def execute_query(start_element: IVisualElement, query: str) -> List[IVisualElement]:
    """
    Execute a visual element query starting from an attachment's element.
    
    Key insight: Start from attachment element, traverse up/down as needed.
    Use early termination and caching for performance.
    """
    tokens = parse_query(query)
    current_set = {start_element}
    
    for token in tokens:
        if token.combinator == '^':  # Ancestor
            current_set = traverse_ancestors(current_set, token.selector)
        elif token.combinator == '>':  # Direct child
            current_set = get_direct_children(current_set, token.selector)
        elif token.combinator == ' ':  # Descendant
            current_set = traverse_descendants_lazy(current_set, token.selector)
        elif token.combinator == '~':  # Sibling
            current_set = get_siblings(current_set, token.selector)
        
        if not current_set:
            return []  # Early termination
    
    return list(current_set)

def traverse_descendants_lazy(elements: Set, selector: Selector) -> Set:
    """
    Lazy descendant traversal with early termination.
    Only traverses as deep as needed to find matches.
    Uses BFS with depth limit to avoid full tree traversal.
    """
    MAX_DEPTH = 10  # Configurable limit
    result = set()
    
    for elem in elements:
        queue = [(child, 1) for child in elem.Children]
        
        while queue:
            current, depth = queue.pop(0)
            
            if selector.matches(current):
                result.add(current)
                # Found match, don't need to go deeper in this branch
                continue
            
            if depth < MAX_DEPTH:
                queue.extend((child, depth + 1) for child in current.Children)
    
    return result
```

### 8.5 Selector Matching with Instability Tolerance

Handle visual tree instability by using fuzzy matching:

```python
@dataclass
class ResilientSelector:
    """A selector that tolerates visual tree instability."""
    
    type_filter: Optional[VisualElementType] = None
    name_pattern: Optional[re.Pattern] = None
    text_pattern: Optional[re.Pattern] = None
    states_required: VisualElementStates = VisualElementStates.None_
    states_forbidden: VisualElementStates = VisualElementStates.None_
    
    # Fuzzy matching options
    allow_type_variants: bool = False  # e.g., TextEdit OR Document
    text_sample_length: int = 1000     # Limit GetText() for performance
    
    def matches(self, element: IVisualElement) -> bool:
        # Type check (with optional variants)
        if self.type_filter and element.Type != self.type_filter:
            if not self.allow_type_variants:
                return False
        
        # Name pattern (regex)
        if self.name_pattern:
            name = element.Name or ""
            if not self.name_pattern.search(name):
                return False
        
        # States check
        if self.states_required:
            if not (element.States & self.states_required):
                return False
        if self.states_forbidden:
            if element.States & self.states_forbidden:
                return False
        
        # Text pattern (expensive, check last)
        if self.text_pattern:
            text = element.GetText(maxLength=self.text_sample_length) or ""
            if not self.text_pattern.search(text):
                return False
        
        return True
```

### 8.6 YAML Configuration with Queries

```yaml
id: "arxiv-detector"
name: "arXiv Paper Helper"

# Match conditions using queries
conditions:
  # ANY attachment must satisfy these conditions
  any:
    # Attachment is a visual element
    - type: VisualElement
      # The element OR its ancestors should be in a browser
      ancestorQuery: "TopLevel[ProcessName ~= 'chrome|firefox|edge|safari']"
      # Cross-path query: find address bar and check URL
      probeQueries:
        - name: "url_check"
          query: "^TopLevel > * TextEdit[Name ~= 'address|url|location', Text ~= 'arxiv\\.org']"
          required: true  # Strategy only matches if this query finds results
    
    # OR attachment is a file
    - type: File
      pathPattern: ".*arxiv.*\\.pdf$"

commands:
  - id: "summarize-arxiv"
    name: "Summarize Paper"
    # ...
```

### 8.7 Handling Multiple Attachment Types

```yaml
id: "smart-file-move"
name: "Smart File Mover"

conditions:
  # Require BOTH conditions (AND logic across groups)
  all:
    # First: must have a File Explorer visual element
    - type: VisualElement
      ancestorQuery: "TopLevel[ProcessName = 'explorer']"
    
    # Second: must have file attachments matching pattern
    - type: File
      pathPattern: ".*\\.docx$"
      minCount: 1

  # OR alternative condition group
  any:
    - type: TextSelection
      textPattern: "move .+ to"

commands:
  - id: "move-files"
    name: "Move Files Here"
```

### 8.8 Performance Optimizations

| Optimization | Description |
|--------------|-------------|
| **Ancestor-first** | Always traverse ancestors before descendants (cheaper) |
| **Depth limits** | Cap descendant traversal at configurable depth |
| **Text sampling** | Limit `GetText()` calls with `maxLength` parameter |
| **Query caching** | Cache parsed queries and compiled regexes |
| **Early termination** | Stop traversal once condition is satisfied/failed |
| **Lazy evaluation** | Only execute expensive queries if cheap checks pass |

### 8.9 Fallback Strategies

When visual tree is unstable or query fails:

```yaml
id: "browser-generic"
name: "Browser Helper"

conditions:
  any:
    # Primary: precise query
    - type: VisualElement
      probeQueries:
        - query: "^TopLevel > ToolBar TextEdit[Text ~= 'https?://']"
          required: true
      
    # Fallback: just check process name
    - type: VisualElement
      processName: ["chrome", "firefox", "edge", "safari"]
      # Lower priority if only fallback matches
      priority: -10

commands:
  # Commands get priority adjustment from matching condition
```

---

## 9. Implementation Plan

### 9.1 Phase 1: Core Infrastructure (MVP)

**Goal**: Basic strategy matching and command generation

| Task | Description | Priority |
|------|-------------|----------|
| Define core interfaces | `IStrategy`, `IStrategyCondition`, `IStrategyCommand` | P0 |
| Implement `StrategyContext` | Context collection from attachments | P0 |
| Implement `StrategyRegistry` | In-memory strategy storage | P0 |
| Basic condition evaluators | Process name, element type, text regex | P0 |
| Command generator | Generate commands from matched strategies | P0 |
| UI integration | Display command buttons in chat window | P0 |
| Built-in global strategies | "What is this?", "Summarize" | P0 |

**Deliverable**: Hard-coded strategies work end-to-end

### 9.2 Phase 2: Configuration System

**Goal**: User-defined strategies via YAML files

| Task | Description | Priority |
|------|-------------|----------|
| YAML parser | Parse strategy definition files | P1 |
| Condition DSL | Parse condition expressions | P1 |
| File watcher | Hot-reload strategy changes | P1 |
| Strategy validation | Validate configs on load | P1 |
| User strategy directory | Support `~/.everywhere/strategies/` | P1 |
| Built-in strategies | Browser, IDE, file type strategies | P1 |

**Deliverable**: Users can create custom strategies

### 9.3 Phase 3: Advanced Matching

**Goal**: Graph-based context and complex matching

| Task | Description | Priority |
|------|-------------|----------|
| Context graph builder | Build relevance-scored graph | P2 |
| LCA algorithm | Find lowest common ancestor | P2 |
| XPath-like queries | Visual tree query language | P2 |
| Multi-attachment handling | Smart aggregation | P2 |
| Dynamic commands | Template-based generation | P2 |

**Deliverable**: Complex multi-element scenarios supported

### 9.4 Phase 4: Extensibility

**Goal**: Plugin-style strategy extensions

| Task | Description | Priority |
|------|-------------|----------|
| C# script support | Roslyn-based strategy scripting | P3 |
| Community repository | Strategy sharing infrastructure | P3 |
| Strategy marketplace | In-app strategy browser | P3 |
| Strategy analytics | Usage tracking for optimization | P3 |

### 9.5 Proposed Directory Structure

```
src/Everywhere.Core/
├── StrategyEngine/
│   ├── Abstractions/
│   │   ├── IStrategy.cs
│   │   ├── IStrategyCondition.cs
│   │   ├── IStrategyCommand.cs
│   │   ├── IStrategyEngine.cs
│   │   ├── IStrategyRegistry.cs
│   │   └── StrategyContext.cs
│   │
│   ├── Conditions/
│   │   ├── AttachmentCondition.cs         # Base for attachment matching
│   │   ├── VisualElementCondition.cs      # ChatVisualElementAttachment
│   │   ├── TextCondition.cs               # ChatTextAttachment/Selection
│   │   ├── FileCondition.cs               # ChatFileAttachment
│   │   ├── CompositeCondition.cs          # AND/OR logic
│   │   └── ProbeQueryCondition.cs         # Cross-path visual queries
│   │
│   ├── Query/
│   │   ├── VisualElementQuery.cs          # Query language parser
│   │   ├── QueryExecutor.cs               # Query execution engine
│   │   ├── ResilientSelector.cs           # Instability-tolerant matching
│   │   └── QueryCache.cs                  # Compiled query cache
│   │
│   ├── Configuration/
│   │   ├── StrategyDefinition.cs          # YAML model
│   │   ├── ConditionDefinition.cs         # Condition YAML model
│   │   ├── StrategyLoader.cs              # Load from files
│   │   └── StrategyValidator.cs           # Validate configs
│   │
│   ├── BuiltIn/
│   │   ├── GlobalStrategy.cs              # "What is this?", etc.
│   │   ├── BrowserStrategy.cs
│   │   └── CodeEditorStrategy.cs
│   │
│   ├── StrategyEngine.cs                  # Main orchestrator
│   ├── StrategyRegistry.cs
│   └── CommandExecutor.cs
```

---

## 10. API Reference

### 10.1 Core Interfaces

```csharp
/// <summary>
/// The main entry point for the Strategy Engine.
/// </summary>
public interface IStrategyEngine
{
    /// <summary>
    /// Evaluate all strategies against the current context
    /// and return matching commands.
    /// </summary>
    Task<IReadOnlyList<StrategyCommand>> GetCommandsAsync(
        StrategyContext context,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Execute a command, starting an agent session.
    /// </summary>
    Task ExecuteCommandAsync(
        StrategyCommand command,
        StrategyContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A strategy defines conditions and produces commands.
/// </summary>
public interface IStrategy
{
    /// <summary>
    /// Unique identifier for this strategy.
    /// </summary>
    string Id { get; }
    
    /// <summary>
    /// Display name (may be i18n key).
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Priority for conflict resolution.
    /// </summary>
    int Priority { get; }
    
    /// <summary>
    /// Whether this strategy is currently enabled.
    /// </summary>
    bool IsEnabled { get; }
    
    /// <summary>
    /// Evaluate if this strategy matches the given context.
    /// </summary>
    bool Matches(StrategyContext context);
    
    /// <summary>
    /// Generate commands for the matched context.
    /// </summary>
    IEnumerable<StrategyCommand> GetCommands(StrategyContext context);
}

/// <summary>
/// A condition that can be evaluated against context.
/// </summary>
public interface IStrategyCondition
{
    /// <summary>
    /// Evaluate this condition against the context.
    /// </summary>
    bool Evaluate(StrategyContext context);
}

/// <summary>
/// Registry for all available strategies.
/// </summary>
public interface IStrategyRegistry
{
    /// <summary>
    /// All registered strategies.
    /// </summary>
    IReadOnlyList<IStrategy> Strategies { get; }
    
    /// <summary>
    /// Register a strategy.
    /// </summary>
    void Register(IStrategy strategy);
    
    /// <summary>
    /// Unregister a strategy by ID.
    /// </summary>
    void Unregister(string strategyId);
    
    /// <summary>
    /// Reload strategies from configuration files.
    /// </summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
```

### 10.2 Context Types

```csharp
/// <summary>
/// The context for strategy evaluation.
/// </summary>
public record StrategyContext
{
    /// <summary>
    /// User-provided attachments.
    /// Use ChatAttachment.IsPrimary to identify focused items (0 or more).
    /// </summary>
    public required IReadOnlyList<ChatAttachment> Attachments { get; init; }
    
    /// <summary>
    /// Root visual elements derived from attachments.
    /// Each element's ancestor chain ends at Screen or TopLevel.
    /// </summary>
    public IReadOnlyList<IVisualElement> RootElements { get; init; }
    
    /// <summary>
    /// Active process information (derived from visual elements).
    /// </summary>
    public ProcessInfo? ActiveProcess { get; init; }
    
    /// <summary>
    /// Additional metadata for custom matching logic.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; }
    
    /// <summary>
    /// Helper to get all primary attachments.
    /// </summary>
    public IEnumerable<ChatAttachment> PrimaryAttachments => 
        Attachments.Where(a => a.IsPrimary);
    
    /// <summary>
    /// Helper to get attachments by type.
    /// </summary>
    public IEnumerable<T> GetAttachments<T>() where T : ChatAttachment =>
        Attachments.OfType<T>();
}

/// <summary>
/// Process information for matching.
/// </summary>
public record ProcessInfo(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string? MainWindowTitle
);
```

### 10.3 Command Types

```csharp
/// <summary>
/// A command that can be executed.
/// </summary>
public record StrategyCommand
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public LucideIconKind Icon { get; init; } = LucideIconKind.Sparkles;
    public int Priority { get; init; } = 0;
    
    /// <summary>
    /// System prompt for the agent session.
    /// </summary>
    public string? SystemPrompt { get; init; }
    
    /// <summary>
    /// Initial user message to send.
    /// </summary>
    public string? UserMessage { get; init; }
    
    /// <summary>
    /// Allowed tool names. null = default, empty = none.
    /// </summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
    
    /// <summary>
    /// MCP server names to enable.
    /// </summary>
    public IReadOnlyList<string>? McpServers { get; init; }
    
    /// <summary>
    /// Variables for prompt template interpolation.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Variables { get; init; }
}
```

### 10.4 Built-in Conditions

```csharp
// Process name matching
public class ProcessNameCondition(params string[] processNames) 
    : IStrategyCondition
{
    public bool Evaluate(StrategyContext context) =>
        context.ActiveProcess is { } p && 
        processNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase);
}

// Element type matching
public class ElementTypeCondition(params VisualElementType[] types)
    : IStrategyCondition
{
    public bool Evaluate(StrategyContext context) =>
        context.VisualElements.Any(e => types.Contains(e.Type));
}

// Text pattern matching (regex)
public class TextPatternCondition(string pattern, RegexOptions options = default)
    : IStrategyCondition
{
    private readonly Regex _regex = new(pattern, options | RegexOptions.Compiled);
    
    public bool Evaluate(StrategyContext context)
    {
        foreach (var elem in context.VisualElements)
        {
            var text = elem.GetText(maxLength: 10000);
            if (text is not null && _regex.IsMatch(text))
                return true;
        }
        return false;
    }
}

// File extension matching
public class FileExtensionCondition(params string[] extensions)
    : IStrategyCondition
{
    public bool Evaluate(StrategyContext context) =>
        context.Attachments
            .OfType<ChatFileAttachment>()
            .Any(f => extensions.Contains(
                Path.GetExtension(f.FilePath), 
                StringComparer.OrdinalIgnoreCase));
}

// Composite conditions
public class AndCondition(params IStrategyCondition[] conditions)
    : IStrategyCondition
{
    public bool Evaluate(StrategyContext context) =>
        conditions.All(c => c.Evaluate(context));
}

public class OrCondition(params IStrategyCondition[] conditions)
    : IStrategyCondition
{
    public bool Evaluate(StrategyContext context) =>
        conditions.Any(c => c.Evaluate(context));
}
```

---

## 11. Open Questions

| Question | Options | Status |
|----------|---------|--------|
| Should strategies support async matching? | Yes (for OCR), No (keep simple) | TBD |
| Command caching? | Cache per context hash | TBD |
| Strategy versioning? | Semantic versioning for sharing | TBD |
| Telemetry for command usage? | Privacy-respecting analytics | TBD |
| Integration with custom assistants? | Strategy per assistant | TBD |

---

## 12. References

- [Microsoft Semantic Kernel](https://github.com/microsoft/semantic-kernel) - Planner concepts
- [VS Code Command Palette](https://code.visualstudio.com/docs/getstarted/userinterface#_command-palette) - Context-aware commands
- [Alfred Workflows](https://www.alfredapp.com/workflows/) - Extensible command system
- [Raycast Extensions](https://www.raycast.com/extensions) - Context-aware launcher
- [XPath Specification](https://www.w3.org/TR/xpath/) - Tree query language

---

*Document Version: 0.1.0*
*Last Updated: 2026-01-21*
```

