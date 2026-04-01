# Llens Roadmap

## Mission

Llens should become an LLM-first code retrieval engine.

Its job is not to behave like a general IDE. Its job is to take a broad, fuzzy, or partial lookup request and return the smallest useful set of code facts quickly:

- what object likely matches the request
- where it lives
- what it contains
- what touches it
- what snippets should be sent back to the model

The main optimization target is not only latency. It is reducing wasted LLM tool calls and reducing tokens spent on irrelevant code.

## Product Direction

Llens should be defined by five core capabilities:

1. Fast object lookup
2. Incremental graph exploration
3. Compact context-pack generation
4. Persistent incremental indexing
5. One structured LLM-facing query API

If those five are strong, the product direction is correct.

## Principles

- LLM-first: every major feature should help a model find code faster and with fewer tokens.
- Index-first: most lookup speed comes from better indexed data, not better UI.
- Progressive disclosure: return a small answer first, then expand only when needed.
- Bounded output: every lookup path should respect result and snippet limits.
- Deterministic retrieval first: exact, fuzzy, path, snippet, graph. Embeddings come later.

## What Counts As Success

For a common lookup like "find the method that computes discount total":

- one query should return likely symbols
- one expansion should show related files and usages
- one context pack should be enough to answer or patch safely

Operational targets:

- fuzzy search should feel interactive in the browser
- graph expansion should be incremental, not full redraw only
- context packs should stay under a strict token budget
- reindexing should happen automatically after file changes

## Core Feature Set

### 1. Repo Orientation

Purpose: help the LLM understand where it is before it starts searching.

Features:

- project summary
- file catalog
- module or folder summaries
- language breakdown
- symbol density per file or folder
- dependency hotspot indicators

### 2. Lookup Engine

Purpose: solve the first retrieval step quickly.

Required lookup modes:

- exact symbol lookup
- fuzzy symbol lookup
- path lookup
- snippet lookup
- file lookup

Useful result fields:

- object id
- path
- line range
- kind
- signature
- score
- confidence

### 3. Graph Exploration

Purpose: move from "what is this?" to "what touches this?" and "what should I read next?"

Required graph edge types:

- contains
- imports
- references

Next edge types:

- callers
- callees
- implements
- extends
- test coverage links

Required graph UX:

- select node
- expand out
- expand in
- expand both
- collapse branch
- refocus on node
- pan and zoom
- history or breadcrumbs

### 4. Context Packs

Purpose: convert retrieval results into compact evidence for the model.

A context pack should contain:

- summary
- top symbols
- top files
- short snippets
- confidence
- known gaps

Context packs should be task-shaped, not generic.

Initial pack types:

- lookup
- impact
- implementation
- bug trace

### 5. Incremental Indexing

Purpose: keep results fresh and avoid redoing expensive work.

Required indexing behavior:

- git-aware scanning
- persistent cache
- incremental file reindexing
- stable symbol ids
- fast file-context retrieval
- invalidation on file delete or rename

### 6. LLM Query API

Purpose: let a model compose reliable retrieval steps.

The API should converge toward a small set of structured calls:

- `/api/query`
- `/api/graph/query`
- `/api/context-pack`

The long-term goal is a small JSON query language, not a growing pile of unrelated endpoints.

## Delivery Phases

## Phase 1: Retrieval MVP

Goal: make lookup reliable enough that Llens is already useful in real coding sessions.

Scope:

- exact symbol lookup
- fuzzy lookup
- snippet lookup
- file lookup
- file detail view
- catalog view
- project summaries
- buildable API documentation

Definition of done:

- fast fuzzy searches in the browser
- consistent result ranking
- compact response schema
- Shell Quest sized project can be indexed and queried

## Phase 2: Navigable Graph

Goal: make exploration incremental instead of static.

Scope:

- graph tab
- node selection
- expand in and out
- collapse branch
- larger scan depth
- better layout than static radial snapshot
- node history or breadcrumbs

Definition of done:

- user can start from one file or symbol and walk to neighboring objects without resetting context
- graph state persists across expansions
- graph also returns an LLM-usable graph payload

## Phase 3: Context-Pack Engine

Goal: stop returning raw retrieval output and start returning model-ready evidence.

Scope:

- context-pack endpoint
- pack ranking
- snippet deduplication
- token budgeting
- task-specific pack modes
- confidence and gap reporting

Definition of done:

- a model can ask for a context pack and get a small, useful answer without reconstructing the graph itself

## Phase 4: Stronger Semantics

Goal: improve precision beyond lexical and syntax-only retrieval.

Scope:

- semantic C# references via Roslyn
- stronger Rust references
- implementors and type links
- call graph edges
- module summaries from indexed structure

Definition of done:

- "who calls this?"
- "what implements this?"
- "what breaks if I change this?"

should be answered from index data rather than text search heuristics.

## Phase 5: Optional Semantic Search

Goal: help with vague or intent-heavy questions that names do not resolve well.

Scope:

- embeddings
- semantic reranking
- NL-to-query rewrite
- cached retrieval sessions

This phase is optional. It should only be pursued after deterministic retrieval and context packs are already strong.

## Performance Priorities

In order:

1. Correct top results
2. Small context packs
3. Fast incremental lookups
4. Richer semantics
5. Fancy visualization

If a feature improves visuals but not retrieval quality or retrieval speed, it is secondary.

## Non-Goals Right Now

- full IDE parity
- full language-server replacement
- arbitrary code execution workflows
- embeddings before deterministic retrieval is solid
- giant unbounded graph renders

## Immediate Next Work

1. Stabilize graph exploration so node expansion and collapse are smooth and predictable.
2. Improve graph layout so it reflects graph structure, not only concentric rings.
3. Add history or breadcrumbs for graph navigation.
4. Add a first `/api/context-pack` endpoint that consumes current lookup and graph results.
5. Move cache backend from in-memory default to persistent SQLite for real sessions.

## Definition Of The Project

Llens is successful if an LLM can use it to:

- orient in a repo quickly
- find the right symbol or file quickly
- expand to directly relevant neighbors quickly
- receive a compact context pack without reading large amounts of code blindly

That is the project direction.
