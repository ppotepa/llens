# Handoff

## Stan na

- data: `2026-04-11`
- repo: `D:\Git\llens`
- branch: `main`

## Cel prac

Repo rozwija się w kierunku narzędzi oszczędzających tokeny dla agentów LLM podczas pracy na kodzie i repozytorium:

- kompaktowe operacje na kodzie i Git
- benchmarki `classic vs compact vs hybrid`
- desktopowy `Test Explorer` do uruchamiania testów i analizy wyników
- docelowo: cache-first Git graph zamiast live `git log`/`git show`

## Co zostało zrobione

## 1. Benchmarki token savings

Dodane zostały benchmarki i testy porównujące użycie tokenów.

Najważniejsze elementy:

- pack 100 zadań dla `Rust + C#`:
  - [agent-rust-csharp-history-100.tasks.json](/D:/Git/llens/Llens.Bench/TaskPacks/agent-rust-csharp-history-100.tasks.json)
- benchmark history:
  - [HistoryTaskPackBenchmark.cs](/D:/Git/llens/Llens.Bench/Scenarios/HistoryTaskPackBenchmark.cs)
- testy:
  - [AgentHistoryTaskPackTests.cs](/D:/Git/llens/Llens.Tests/AgentHistoryTaskPackTests.cs)
  - [WorkflowTokenSavingsTests.cs](/D:/Git/llens/Llens.Tests/WorkflowTokenSavingsTests.cs)

Aktualny model benchmarku:

- `classic`
- `compact`
- `hybrid`

Rejestrowane są m.in.:

- `BaselineTokens`
- `OurTokens`
- `HybridTokens`
- `BaselineInput/Output`
- `OurInput/Output`
- `HybridInput/Output`
- `TraceJson`

## 2. Desktopowy Test Explorer

Powstała aplikacja Windows do eksploracji testów i wyników benchmarków:

- [Llens.TestExplorer.csproj](/D:/Git/llens/Llens.TestExplorer/Llens.TestExplorer.csproj)
- [Program.cs](/D:/Git/llens/Llens.TestExplorer/Program.cs)
- [MainForm.cs](/D:/Git/llens/Llens.TestExplorer/MainForm.cs)
- [MainForm.Layout.cs](/D:/Git/llens/Llens.TestExplorer/MainForm.Layout.cs)
- [MainForm.Data.cs](/D:/Git/llens/Llens.TestExplorer/MainForm.Data.cs)
- [README.md](/D:/Git/llens/Llens.TestExplorer/README.md)

Zaimplementowane funkcje:

- uruchamianie benchmarków z GUI
- ładowanie ostatniego runu z SQLite
- lista scenariuszy
- czytelniejsze nazwy scenariuszy przez `title`
- porównanie `classic / compact / hybrid`
- trace execution
- sanitizacja logów
- filtrowanie i widoki katalogowe
- checkboxy do batch-run zamiast multi-select wierszy

## 3. SQL reporting

Benchmark zapisuje dane do SQLite:

- nagłówki runów
- wyniki per test i per mode
- kroki wykonania

Powiązane miejsca:

- [Llens.Bench/Program.cs](/D:/Git/llens/Llens.Bench/Program.cs)
- [BenchmarkResult.cs](/D:/Git/llens/Llens.Bench/BenchmarkResult.cs)

## 4. Maquette UX

Dodana została maquette okna aplikacji, która była podstawą przebudowy explorera:

- [maquette.window.xml](/D:/Git/llens/docs/maquette.window.xml)

## 5. Plan architektury Git graph/cache

Został zapisany pełny plan przebudowy operacji Git na model cache-first:

- [git-graph-cache-plan.md](/D:/Git/llens/docs/git-graph-cache-plan.md)

Kluczowy wniosek:

- dziś operacje Git nadal głównie shellują do `git`
- docelowo historia repo ma być obsługiwana przez nasz własny graph/cache

## 6. Trwający refactor struktury core

W worktree jest też większa przebudowa struktury projektu:

- przenoszenie cache/indexing/scanning/observability/shared do `Llens.Core/*`
- nowe `Llens.Abstractions/`
- capability-based podział w językach `C#` i `Rust`

To jest widoczne po zmianach typu:

- usunięcia starych plików z root-level `Caching/`, `Indexing/`, `Scanning/`, `Observability/`, `Shared/`
- nowe odpowiedniki w `Llens.Core/*`

To jest część aktualnego stanu branchu i została zostawiona bez cofania.

## Co jest gotowe funkcjonalnie

- benchmark history dla 100 scenariuszy `Rust/C#`
- testy walidacyjne packa i token savings
- generowanie `CSV/JSON/SQLite`
- Windows GUI do uruchamiania i eksploracji benchmarków
- czytelniejsze nazwy scenariuszy
- UX explorer oparty o katalog testów + szczegóły lokalne
- dokument maquette
- architektoniczny plan dla Git graph/cache

## Co nadal wymaga zrobienia

## 1. Git graph/cache implementation

To jest najważniejsza brakująca część.

Do zrobienia:

- `IGitContextCache`
- `SqliteGitContextCache`
- `GitGraphIndexer`
- `GitWorktreeSnapshotService`
- `ICompactGitService`

Następnie migracja:

- `/git/history`
- `/git/commit`
- `/git/patch`
- `/git/status`
- `/fs/diff`

## 2. Benchmarki Git powinny mierzyć cache-first, nie raw-vs-raw

Obecnie `HistoryTaskPackBenchmark` nadal uruchamia `git` po obu stronach porównania.

Docelowo:

- `classic` = szeroki raw git
- `compact` = query do naszego graph/cache
- `hybrid` = cache-first z fallbackiem do git

## 3. Explorer nadal wymaga dalszego szlifu UX

Poprawione zostały podstawy, ale dalej warto zrobić:

- bardziej zwarte `Selected Test Summary`
- lepszy `Overview` z badge typu `winner`, `fallback`, `correctness`
- `Run Filtered`
- widok `Top regressions`
- pełniejsze historyczne porównania runów

## 4. Metadata persistence

Warto dopiąć zapisywanie do SQL:

- `title`
- `kind`
- `goal`
- `language`
- `tooling`
- `intent`

żeby stare runy nie zależały od aktualnej wersji task packa.

## Główne ryzyka / problemy

## 1. Git nadal jest live-shell na hot path

Największy problem architektoniczny.

Szczególnie:

- [CompactOpsEndpoints.cs](/D:/Git/llens/Api/CompactOpsEndpoints.cs:456)
- [CompactOpsEndpoints.cs](/D:/Git/llens/Api/CompactOpsEndpoints.cs:490)
- [CompactOpsEndpoints.cs](/D:/Git/llens/Api/CompactOpsEndpoints.cs:515)
- [CompactFsService.cs](/D:/Git/llens/Application/Fs/CompactFsService.cs:171)
- [CompactFsService.cs](/D:/Git/llens/Application/Fs/CompactFsService.cs:219)

## 2. `history` ma wzorzec `1 + N (+N)`

Aktualna ścieżka:

- `git log`
- per commit `git show --name-status --numstat`
- czasem dodatkowe `git show -U0`

To jest kosztowne i niepotrzebnie rozpycha tokeny oraz latency.

## 3. Dirty worktree jest duży

Aktualny commit będzie obejmował większy zestaw zmian, nie tylko ostatni dokument Git planu. To wynika z tego, że branch zawiera całość bieżącej pracy nad benchmarkami, explorerem i refactorem.

## Ostatnio weryfikowane

W trakcie prac były uruchamiane i przechodziły:

- `dotnet build Llens.TestExplorer/Llens.TestExplorer.csproj -nologo`
- `dotnet build Llens.Bench/Llens.Bench.csproj -nologo`
- `dotnet test Llens.Tests/Llens.Tests.csproj --filter "AgentHistoryTaskPackTests|WorkflowTokenSavingsTests" -nologo`
- wariant `--no-build` dla testów, gdy pełny build był blokowany przez wcześniejsze problemy ze ścieżkami/kopiowaniem lub lock pliku `.exe`

Nie było pełnego świeżego rerunu całego repo tuż przed zapisaniem tego handoffu.

## Rekomendowana kolejność dalszych prac

1. zaimplementować `IGitContextCache` i schema SQLite
2. zaimplementować `GitGraphIndexer` dla immutable history
3. przepiąć `/git/history`, `/git/commit`, `/git/patch` na cache-first
4. dodać worktree snapshots dla `status` i `diff`
5. przebudować benchmarki Git na realne `raw vs cache vs hybrid`
6. dopiąć SQL metadata persistence
7. zrobić kolejny pass UX w explorerze

## Najważniejsze pliki startowe dla kolejnej osoby

- benchmark i packi:
  - [HistoryTaskPackBenchmark.cs](/D:/Git/llens/Llens.Bench/Scenarios/HistoryTaskPackBenchmark.cs)
  - [agent-rust-csharp-history-100.tasks.json](/D:/Git/llens/Llens.Bench/TaskPacks/agent-rust-csharp-history-100.tasks.json)
- explorer:
  - [MainForm.cs](/D:/Git/llens/Llens.TestExplorer/MainForm.cs)
  - [MainForm.Layout.cs](/D:/Git/llens/Llens.TestExplorer/MainForm.Layout.cs)
  - [MainForm.Data.cs](/D:/Git/llens/Llens.TestExplorer/MainForm.Data.cs)
- SQL/reporting:
  - [Llens.Bench/Program.cs](/D:/Git/llens/Llens.Bench/Program.cs)
  - [BenchmarkResult.cs](/D:/Git/llens/Llens.Bench/BenchmarkResult.cs)
- dokumenty:
  - [maquette.window.xml](/D:/Git/llens/docs/maquette.window.xml)
  - [git-graph-cache-plan.md](/D:/Git/llens/docs/git-graph-cache-plan.md)

