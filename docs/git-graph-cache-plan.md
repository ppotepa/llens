# Git Graph Cache Plan

## Goal

Replace live `git log` / `git show` / `git diff` style queries on hot paths with a cache-first Git graph model:

- immutable repo history served from our indexed graph/cache
- mutable worktree state served from a lightweight live snapshot layer
- compact, token-efficient responses exposed through our own operations rather than raw git output

This aligns Git with the existing code indexing model. Today code lookups are index-backed; Git lookups are still mostly shell-backed.

## Current Gaps

### 1. History is still live-shell

Hot paths still shell out to `git` for every request:

- [CompactOpsEndpoints.cs](/D:/Git/llens/Api/CompactOpsEndpoints.cs:456) `/git/history`
- [CompactOpsEndpoints.cs](/D:/Git/llens/Api/CompactOpsEndpoints.cs:490) `/git/commit`
- [CompactOpsEndpoints.cs](/D:/Git/llens/Api/CompactOpsEndpoints.cs:515) `/git/patch`
- [CompactFsService.cs](/D:/Git/llens/Application/Fs/CompactFsService.cs:171) `DiffAsync`
- [CompactFsService.cs](/D:/Git/llens/Application/Fs/CompactFsService.cs:219) `GitStatusAsync`

`/git/history` is especially expensive:

- one `git log`
- then one `git show --name-status --numstat` per commit
- then optionally one `git show -U0` per commit for compact hunks

That creates an avoidable `1 + N (+N)` process pattern.

### 2. Benchmarks do not yet measure the real target architecture

[HistoryTaskPackBenchmark.cs](/D:/Git/llens/Llens.Bench/Scenarios/HistoryTaskPackBenchmark.cs:12) compares broad raw Git queries against narrower raw Git queries, but both sides still execute `git`.

That is not yet:

- `classic raw git output`
- versus
- `our repo graph/cache query`

### 3. No persistent Git context model

We already have a code cache:

- [SqliteCodeMapCache.cs](/D:/Git/llens/Llens.Core/Caching/SqliteCodeMapCache.cs:7)

There is no equivalent:

- `IGitContextCache`
- `SqliteGitContextCache`
- `GitGraphIndexer`

### 4. Mutable and immutable Git concerns are mixed

Two categories need different treatment:

- immutable history: commits, touches, ancestry, compact hunks, commit summaries
- mutable worktree: staged diff, unstaged diff, untracked files, rename state

History should be cache/index backed. Worktree should be live or short-TTL snapshot backed.

## Architecture

## Core Principle

Do not mirror the full Git object database in memory.

Use:

1. persistent compact Git graph cache in SQLite
2. small in-memory hot cache for repeated queries
3. incremental invalidation when `HEAD`, index, or worktree changes

This is lighter, deterministic, and easier to benchmark.

## Main Components

### 1. `IGitContextCache`

Primary query interface for immutable Git facts and compact patch metadata.

Suggested responsibilities:

- repo metadata lookup
- commit summary lookup
- file history lookup
- ancestry lookup
- path-level facts lookup
- compact hunk lookup
- worktree snapshot lookup

Suggested methods:

```csharp
public interface IGitContextCache
{
    Task<GitRepoState?> GetRepoStateAsync(string repoName, CancellationToken ct = default);

    Task<GitFileFacts?> GetFileFactsAsync(string repoName, string path, CancellationToken ct = default);
    Task<IReadOnlyList<GitCommitRef>> GetFileHistoryAsync(string repoName, string path, int take, string? beforeCommit = null, CancellationToken ct = default);
    Task<GitCommitSummary?> GetCommitSummaryAsync(string repoName, string commitId, string? path = null, CancellationToken ct = default);
    Task<IReadOnlyList<GitCompactHunk>> GetCommitHunksAsync(string repoName, string commitId, string? path = null, CancellationToken ct = default);

    Task<IReadOnlyList<GitCommitRef>> GetParentsAsync(string repoName, string commitId, CancellationToken ct = default);
    Task<IReadOnlyList<GitCommitRef>> GetChildrenAsync(string repoName, string commitId, CancellationToken ct = default);

    Task<GitWorktreeSnapshot?> GetWorktreeSnapshotAsync(string repoName, string? path = null, CancellationToken ct = default);

    Task UpsertRepoStateAsync(GitRepoState state, CancellationToken ct = default);
}
```

### 2. `GitGraphIndexer`

Builds and refreshes the Git cache.

Responsibilities:

- discover repo root and branch/HEAD
- walk commit graph once
- materialize path-level facts
- materialize compact commit summaries
- materialize compact hunk headers
- refresh worktree snapshot separately

Modes:

- `cold build`
- `incremental from last indexed commit`
- `worktree snapshot refresh`

### 3. `GitWorktreeSnapshotService`

Mutable layer for:

- `status`
- `staged diff`
- `unstaged diff`
- untracked files

This may still call `git`, but:

- results should be compacted immediately
- results should be cached with short TTL
- output should feed the same SQL/cache model as history

## Data Model

Use SQLite as the source of truth.

### `git_repos`

Tracks repo state and invalidation anchors.

Columns:

- `repo_name`
- `git_root`
- `head_commit`
- `head_branch`
- `index_signature`
- `worktree_signature`
- `last_history_indexed_at`
- `last_worktree_indexed_at`

### `git_commits`

Commit-level metadata.

Columns:

- `repo_name`
- `commit_id`
- `tree_id`
- `author_name`
- `author_email`
- `author_date_utc`
- `committer_name`
- `committer_email`
- `committer_date_utc`
- `subject`
- `body_short`
- `parent_count`
- `topo_order`

### `git_commit_edges`

Commit graph relations.

Columns:

- `repo_name`
- `parent_commit_id`
- `child_commit_id`

### `git_commit_files`

Per-commit file changes.

Columns:

- `repo_name`
- `commit_id`
- `path`
- `prev_path`
- `status`
- `inserted`
- `deleted`
- `is_binary`

### `git_file_facts`

Materialized path-level facts for O(1)/O(log n) lookup.

Columns:

- `repo_name`
- `path`
- `first_commit_id`
- `latest_commit_id`
- `touch_count`
- `last_author`
- `last_subject`
- `last_touched_at_utc`

### `git_compact_hunks`

Compact hunk headers only, not full raw patches.

Columns:

- `repo_name`
- `commit_id`
- `path`
- `ordinal`
- `old_start`
- `old_count`
- `new_start`
- `new_count`

### `git_worktree_entries`

Snapshot of mutable current state.

Columns:

- `repo_name`
- `snapshot_id`
- `path`
- `prev_path`
- `xy`
- `staged`
- `unstaged`
- `untracked`
- `kind`

### `git_worktree_diffs`

Compact diff snapshot per path and mode.

Columns:

- `repo_name`
- `snapshot_id`
- `path`
- `diff_kind`
- `line_count`
- `compact_diff`
- `raw_diff_hash`

## Query Model

## Immutable History Operations

These should be fully cache-first.

### `latest touch`

Read from `git_file_facts.latest_commit_id`.

### `first touch`

Read from `git_file_facts.first_commit_id`.

### `touch count`

Read from `git_file_facts.touch_count`.

### `history(path)`

Read from `git_commit_files` joined with `git_commits`, sorted by `topo_order` or timestamp.

### `commit summary`

Read from:

- `git_commits`
- `git_commit_files`
- `git_compact_hunks`

### `ancestry`

Read from `git_commit_edges`.

Needed for graph-aware commands such as:

- previous N commits affecting path
- merge parent inspection
- nearest common ancestor
- child commits for blame-like navigation

## Mutable Worktree Operations

These should be snapshot-backed, not always history-backed.

### `status`

Source:

- live `git status --porcelain` on refresh
- then compact persisted snapshot

### `diff --staged`

Source:

- live refresh on demand or TTL expiry
- compact diff stored in `git_worktree_diffs`

### `diff`

Same as above for unstaged diff.

### `untracked`

Source:

- worktree snapshot entries

## API Changes

Move endpoints to cache-first with controlled fallback.

### New internal service

Add `ICompactGitService`.

Responsibilities:

- resolve repo scope
- serve compact Git operations from cache
- fallback to Git shell only when cache misses or invalid state is detected

Suggested methods:

```csharp
public interface ICompactGitService
{
    Task<CompactGitHistoryResponse> GetHistoryAsync(Project project, CompactGitHistoryRequest request, CancellationToken ct);
    Task<CompactGitCommitResponse> GetCommitAsync(Project project, CompactGitCommitRequest request, CancellationToken ct);
    Task<CompactGitPatchResponse> GetPatchAsync(Project project, CompactGitPatchRequest request, CancellationToken ct);
    Task<CompactGitStatusResponse> GetStatusAsync(Project project, CompactGitStatusRequest request, CancellationToken ct);
    Task<CompactFsDiffResponse> GetDiffAsync(Project project, CompactFsDiffRequest request, CancellationToken ct);
}
```

### Endpoint migration

Current endpoints should stop doing process orchestration inline:

- [CompactOpsEndpoints.cs](/D:/Git/llens/Api/CompactOpsEndpoints.cs:456)
- [CompactOpsEndpoints.cs](/D:/Git/llens/Api/CompactOpsEndpoints.cs:490)
- [CompactOpsEndpoints.cs](/D:/Git/llens/Api/CompactOpsEndpoints.cs:515)
- [CompactFsService.cs](/D:/Git/llens/Application/Fs/CompactFsService.cs:171)
- [CompactFsService.cs](/D:/Git/llens/Application/Fs/CompactFsService.cs:219)

They should call `ICompactGitService` instead.

## Graph-First Operations We Should Support

These are the Git operations an LLM agent uses most often and should have compact graph-backed variants.

### History / lineage

- latest commit touching file
- first commit introducing file
- touch count
- previous N commits affecting file
- commits between two revisions affecting path
- parents of commit
- children of commit
- merge ancestry

### Patch / change summary

- commit summary with file list and counts
- compact hunk headers
- patch span per file
- changed files by commit
- renamed-from / renamed-to resolution

### Worktree

- compact status
- staged diff summary
- unstaged diff summary
- untracked files
- changed paths by directory

### Graph questions

- nearest common ancestor
- branch divergence summary
- file history continuity through renames
- commit neighborhood around path

## Compact Output Shapes

Do not return raw Git output by default.

Preferred shapes:

### `history compact`

```json
{
  "path": "src/foo.rs",
  "latest": "abc1234",
  "first": "def5678",
  "touchCount": 19,
  "recent": [
    { "id": "abc1234", "date": "2026-04-10", "author": "P", "subject": "Tighten parser" }
  ]
}
```

### `commit compact`

```json
{
  "id": "abc1234",
  "subject": "Tighten parser",
  "inserted": 12,
  "deleted": 7,
  "files": [
    {
      "path": "src/parser.rs",
      "status": "M",
      "hunks": [
        { "oldStart": 10, "oldCount": 2, "newStart": 10, "newCount": 4 }
      ]
    }
  ]
}
```

### `status compact`

```json
{
  "count": 3,
  "compact": [
    "M src/parser.rs",
    "A src/cache.rs",
    "?? notes.txt"
  ]
}
```

### `diff compact`

Return path-scoped hunks or span summaries first, raw patch only on explicit escalation.

## Invalidation Strategy

### History invalidation

Refresh history index when:

- `HEAD` commit changed
- branch changed
- repo root changed

This should trigger incremental indexing from previous `head_commit`.

### Worktree invalidation

Refresh worktree snapshot when:

- index mtime/signature changed
- worktree file signature changed
- TTL expired
- explicit refresh requested

Recommended TTL:

- status: 1-2 seconds
- diff: 1-2 seconds

## Benchmark Changes

## Replace current history benchmark goal

Today:

- broad Git query
- narrower Git query

Target:

- `classic`: raw Git process output
- `compact`: graph/cache result
- `hybrid`: graph/cache with Git fallback only on miss/invalidation

### New benchmark suites

#### `git-history-ops`

- latest touch
- first touch
- touch count
- recent commits for file
- commit summary for file

#### `git-diff-ops`

- staged diff summary
- unstaged diff summary
- changed files under path
- compact patch spans

#### `git-graph-ops`

- parents
- children
- merge base
- divergence
- rename continuity

### Metrics

For every task and mode:

- success
- latency
- process count
- baseline tokens
- compact tokens
- hybrid tokens
- token delta
- savings percent
- cache hit / miss
- fallback used

## Tests

## Unit tests

- commit graph ingestion
- rename tracking
- file facts materialization
- hunk parser correctness
- worktree snapshot diff normalization

## Integration tests

- cache-first `/git/history`
- cache-first `/git/commit`
- cache-first `/git/patch`
- snapshot-backed `/git/status`
- snapshot-backed `/fs/diff`

## Regression tests

- hybrid fallback correctness
- stale cache invalidation
- rename continuity across history
- merge commit parent handling

## Desktop Explorer Impact

The test explorer should expose Git-mode evidence directly.

Per testcase and per mode show:

- input operation
- compact query path
- cache hit or fallback
- tokens in/out
- returned facts
- trace of graph steps used

Additional categories to add in explorer:

- `Git / History`
- `Git / Diff`
- `Git / Worktree`
- `Git / Graph`

## Delivery Plan

## Phase 1: Foundation

1. Add `IGitContextCache`
2. Add SQLite schema for Git graph/cache
3. Add `GitGraphIndexer`
4. Add `GitWorktreeSnapshotService`

## Phase 2: Read Path Migration

1. Add `ICompactGitService`
2. Migrate `/git/history`
3. Migrate `/git/commit`
4. Migrate `/git/patch`
5. Migrate `/git/status`
6. Migrate `/fs/diff`

## Phase 3: Benchmarking

1. Replace raw-vs-raw history benchmark with cache-vs-raw
2. Add diff/worktree benchmark packs
3. Add cache hit/fallback telemetry
4. Persist mode-level evidence to SQL

## Phase 4: UX

1. Show cache hit/miss badges in explorer
2. Show fallback reason
3. Show graph trace steps
4. Add history/diff/graph categories and saved views

## Immediate First Slice

The best first slice is not full diff support.

Start with high-value immutable history facts:

1. `latest touch`
2. `first touch`
3. `touch count`
4. `recent commits for path`
5. `commit summary with compact hunks`

Reason:

- highest agent usage
- clean cache model
- easiest benchmark win
- avoids mixing in mutable worktree complexity too early

After that, add:

1. `status snapshot`
2. `staged diff summary`
3. `unstaged diff summary`

## Non-Goals

Do not do these in v1:

- full in-memory mirror of raw Git objects
- full raw patch persistence for every commit
- full blame engine
- branch visualization UI before core cache correctness is proven

