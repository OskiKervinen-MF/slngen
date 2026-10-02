# multi-generation

branch: topic/multi-generation
-----
## Specification

**Goal:**

Make the slngen command line tool able to generate multiple solutions with a single
execution.

**Why is this important:**

We execute the slngen tool for dozens of projects in our repo.
Much of their dependency graph is shared, so these separate
slngen executions waste a lot of time. It would be more
efficient to load the combined dependency graph of all the
projects into memory once. From that graph a separate solution
can then be generated quickly, since all the information
is already in memory.

The slngen command-line tool already accepts multiple projects
as parameters. Today slngen loads all given projects into one graph
and writes a single solution containing all of them (verified in
Program.cs). This change adds a new command-line parameter
`--solution-per-project`.
When given, the dependency graph is loaded for all listed
projects together, but then the set of transitive dependencies
is analyzed for each one separately.

**Acceptance criteria:**

With `--solution-per-project`, for each entry project P, slngen writes
exactly one solution whose set of projects equals the set slngen would
produce when run with P alone. The solution's default name and location
are the same as running slngen with P alone (P's directory, named after P).
Projects are evaluated only once per run.

## Testing the changes

1. Generate dummy projects A, B, C, L1 and L2.
    * A and B depend on L1.
    * C depends on L2.
    * A depends on C.
2. Run `slngen --solution-per-project --launch:false A.csproj B.csproj C.csproj`.
    * Expect A.sln = {A, C, L1, L2}, B.sln = {B, L1}, C.sln = {C, L2}.
    * Each set must also equal the output of a single-project run.

-----
## Decisions

Q: With --solution-per-project there are several output files. What should happen with --solutionfile (-o), which names a single output file?
A: Log an error and exit. --solutiondir (-d) is still allowed: every solution goes into that directory.

Q: Should Visual Studio be launched in --solution-per-project mode?
A: Never launch it. If --launch:true is passed explicitly, log a warning that it is ignored in this mode.

Q: What if two entry projects would produce the same solution path?
A: Find duplicate output paths up front, log an error that lists them, and write nothing.

Q: Where will the PR go, and which CI should be triggered and monitored?
A: Local only, no PR yet. Verification is a local build and local tests; no push or CI.

Agent: Following the decision above, the branch was not pushed, no PR was opened and no Copilot review was polled. Implementation was committed locally in logical units.

Agent: Plan Testing step 1 (`dotnet build SlnGen.sln`) cannot succeed in this environment because the VSIX project fails with "VSIX deployment is not supported with 'dotnet build'" (unrelated to this change). I built `Microsoft.VisualStudio.SlnGen` (all TFMs) and the unit test project instead, both with 0 warnings and 0 errors.

Agent: Plan Testing step 2 covered the inner-build case by evaluating one project twice with `TargetFramework` set and unset, rather than by building a multi-targeted SDK project. This tests the same closure behaviour on every TFM without SDK resolution.

Agent: On net472, 50 existing tests fail ("tools version 'Current' is unrecognized"). The same 50 fail on `main` (baseline 50 failed / 78 passed; with this change 50 failed / 95 passed), so they are environmental. All new tests pass. net8.0, net9.0, net10.0 and net11.0 pass fully (126 tests each).

Agent: Plan Testing step 7 (smoke script) passes with the net472 slngen.exe and `dotnet slngen.dll` for net11.0. For net8.0/9.0/10.0, `dotnet slngen.dll` fails before reaching this code with "Could not load System.Runtime 11.0.0.0"; this also happens on unmodified `main` (newest SDK's MSBuild is picked up by an older runtime), so it is not caused by this change.

-----
## Plan

### Background (current behaviour)

- `Program.Execute(ProgramArguments, IConsole)` (`src/Microsoft.VisualStudio.SlnGen/Program.cs`)
  resolves entry paths via `ProgramArguments.TryGetEntryProjectPaths`, loads every entry
  and its transitive references into one `ProjectCollection` via `ProjectLoader.LoadProjects`,
  then calls `SlnFile.GenerateSolutionFile(arguments, projectCollection.LoadedProjects.Where(i => !i.GlobalProperties.ContainsKey("TargetFramework")), logger)`
  once and launches VS for that one solution.
- `SlnFile.GenerateSolutionFile` uses `projectList.First()` as the "main" project: it picks the
  default solution directory and name (`SlnGenProjectName` or file name + `.sln`/`.slnx`), the
  custom project type GUIDs, the `SlnGenFolders` value and the `IsMainProject` flag.
- Two loaders fill the collection: `ProjectGraphProjectLoader` (MSBuild Static Graph, used for
  .NET and MSBuild ≥ 16.4) and `LegacyProjectLoader` (follows `ProjectReference`, plus `ProjectFile`
  for traversal projects). Both also load `.shproj`/`.vcxitems` shared projects that are
  imported via `.projitems`/`.vcxitems` (NETFRAMEWORK only). Multi-targeted projects show up
  as an outer build (no `TargetFramework` global property) plus inner builds (with one).

### Step 1 — Add the `--solution-per-project` option

File: `src/Microsoft.VisualStudio.SlnGen/ProgramArguments.cs`

1. Add the property (keep the alphabetical order of options, after `SolutionFileFullPath`):
   ```csharp
   [Option(
       "--solution-per-project",
       CommandOptionType.NoValue,
       Description = "Generates a separate solution for each specified project, containing only that project and its transitive project references.  Cannot be combined with --solutionfile.  Visual Studio is not launched.")]
   public bool SolutionPerProject { get; set; }
   ```
   (`CommandOptionType.NoValue` like `--nologo` / `--ignoreMainProject`.)
2. Add `internal bool ValidateSolutionPerProject(ISlnGenLogger logger)`, with a doc comment:
   - Returns `true` right away if `SolutionPerProject` is false.
   - If `SolutionFileFullPath` has any non-whitespace value, call `logger.LogError("The --solutionfile option cannot be used with --solution-per-project because multiple solution files are generated.  Use --solutiondir to choose an output directory.")`
     and return `false`.
   - If `LaunchVisualStudio` was given explicitly and its last value parses to `true`
     (`TryGetBoolean(LaunchVisualStudio) == true`), call `logger.LogWarning("Visual Studio is not launched when --solution-per-project is specified.")`.
     (`ISlnGenLogger.LogWarning` exists.)
   - Returns `true`.
3. In `TryGetEntryProjectPaths` nothing changes. Directories and wildcards still expand to
   individual projects, and each expanded project becomes its own entry project.

### Step 2 — Compute the per-entry transitive project closure

New file: `src/Microsoft.VisualStudio.SlnGen/ProjectLoading/ProjectReferenceClosure.cs`
(`internal sealed class ProjectReferenceClosure`, MIT header, namespace
`Microsoft.VisualStudio.SlnGen.ProjectLoading`, doc comments on the class and every member).

This class works on the already-evaluated `Project` objects in the `ProjectCollection`, so it
works the same for both loaders and evaluates nothing again. That satisfies "projects are
evaluated only once per run".

1. Constructor `ProjectReferenceClosure(IEnumerable<Project> loadedProjects)` builds two indexes,
   both keyed by full path with `StringComparer.OrdinalIgnoreCase`. Normalize each key with
   `Path.GetFullPath(...)` only (no `ToFullPathInCorrectCase`, which hits the disk).
   - `_outerProjects : Dictionary<string, List<Project>>`: projects whose `GlobalProperties` has no
     `TargetFramework` key. These are the same projects `Program` passes to `GenerateSolutionFile` today.
     It is a list because the graph loader can evaluate the same path several times with different
     global properties (reference `Properties`/`SetConfiguration` metadata); keeping every instance
     avoids duplicate-key exceptions and matches what a single run passes today.
   - `_references : Dictionary<string, HashSet<string>>`: for **every** loaded project (outer and
     inner builds), add edges keyed by its path. That way references that only exist under a
     `TargetFramework` condition (see `SlnGenTests.ProjectReferencesDeterminedInCrossTargetingBuild`) are unioned into the node. Edges:
     - every `ProjectReference` item (`MSBuildItemNames.ProjectReference`): the target is
       `Path.IsPathRooted(EvaluatedInclude) ? EvaluatedInclude : Path.GetFullPath(Path.Combine(project.DirectoryPath, EvaluatedInclude))`
       (same resolution as `LegacyProjectLoader.LoadProjectReferences`);
     - if the project is a traversal project (`IsPropertyValueTrue(MSBuildPropertyNames.IsTraversal)` or
       `IsTraversalProject`), every `ProjectFile` item (`MSBuildItemNames.ProjectFile`), resolved the same way;
     - if `HasSharedItems` is true: for each `ResolvedImport` in `project.Imports` whose `import.ImportedProject.FullPath` ends in `ProjectFileExtensions.ProjItems`, an
       edge to the sibling `.shproj` (`Path.ChangeExtension(..., ProjectFileExtensions.Shproj)`); for each ending in
       `ProjectFileExtensions.VcxItems`, an edge to that `.vcxitems` path (same as the loaders' shared-project logic).
     - Self-edges are ignored.
2. `public IReadOnlyList<Project> GetProjects(string entryProjectPath)`:
   - Breadth-first walk over `_references` starting at the normalized entry path, with a visited set.
   - Return all `_outerProjects` instances for the visited paths (skip paths with no loaded outer
     project, such as missing references, which the loaders already reported). The result is limited to
     what the loader actually loaded; edges that only one loader produces (inner-build edges for the graph
     loader, `ProjectFile` edges for the legacy loader) are harmless for that reason. The **entry
     project comes first**, then the rest in walk order. The entry must be first because
     `GenerateSolutionFile` treats element 0 as the main project, which sets the solution name and location.
   - If the entry path itself has no outer project, return an empty list.

### Step 3 — Extract solution path computation

File: `src/Microsoft.VisualStudio.SlnGen/SlnFile.cs`

1. Move the block that computes `solutionFileFullPath` (current lines 98–114) into
   `internal static string GetSolutionFileFullPath(ProgramArguments arguments, Project firstProject)`
   with a doc comment. Return the raw value exactly as computed today (a relative `-o` stays relative), so
   single-solution behaviour is unchanged. Step 4 applies `Path.GetFullPath` only for collision grouping.
2. `GenerateSolutionFile` calls this method. Single-solution behaviour must not change; the
   existing `SlnFileTests` and `SlnGenTests` cover that.

### Step 4 — Generate one solution per entry project

File: `src/Microsoft.VisualStudio.SlnGen/Program.cs`, inside `Execute(ProgramArguments, IConsole)`.

1. Right after `TryGetEntryProjectPaths` succeeds, call `arguments.ValidateSolutionPerProject(forwardingLogger)`
   and return `1` if it fails.
2. Load the projects once with `ProjectLoader.LoadProjects` as today, and return 1 on errors.
3. If `!arguments.SolutionPerProject`: keep the existing code path exactly as it is (generate + launch).
4. Otherwise call a new `private static int GenerateSolutionPerProject(ProgramArguments arguments, ProjectCollection projectCollection, IReadOnlyList<string> projectEntryPaths, ISlnGenLogger logger)`
   (doc comment). It returns the exit code and:
   - De-duplicates entry paths (`Path.GetFullPath`, `OrdinalIgnoreCase`) and keeps the first-seen order.
   - Builds `ProjectReferenceClosure` once from `projectCollection.LoadedProjects`.
   - For each entry, gets `closure.GetProjects(entry)`. If it is empty, logs an error
     (`$"Project \"{entry}\" was not loaded"`) and skips the entry (never index `projects[0]` on an empty list).
   - Computes each target path with `Path.GetFullPath(SlnFile.GetSolutionFileFullPath(arguments, projects[0]))`
     (`GetSolutionFileExtension` throws on a bad `--format`, same as today).
   - **Collision check before any write:** group targets by path (`OrdinalIgnoreCase`). For every
     group with more than one entry, log an error that names the solution path and the clashing
     entry projects. If any errors were logged, return 1 without writing anything.
   - For each entry, calls `SlnFile.GenerateSolutionFile(arguments, projects, logger)`.
   - Logs `logger.LogMessageHigh($"Generated {count:N0} solution(s)")` at the end.
   - Does **not** call `VisualStudioLauncherFactory` / `TryLaunch` (decision: never launch).
   - Returns `logger.HasLoggedErrors ? 1 : 0`.
5. Call `featureFlags.Dispose()` on both paths, as today.
6. Note: `GenerateSolutionFile` may set `arguments.LoadProjectsInVisualStudio` when it updates an existing
   solution. That only affects launching, which this mode skips, so it is harmless.

### Step 5 — Documentation (supporting work; no spec requirement, but the option must be discoverable)

- `docs/README.md`: add `--solution-per-project` to the options/usage block (around line 94),
  using the option's description text, plus a short example:
  `slngen --solution-per-project src\App1\App1.csproj src\App2\App2.csproj`.
- Root `README.md` has no options listing; leave it unchanged.

### Testing

The implementing agent is responsible for **verifying all changes locally before finishing**
(build and run every test below, and check that they pass). Per Decisions there is **no push, PR or CI**
for now, so no pipeline is triggered. If that changes, run `azure-pipelines.yml` (Build and Test job) and monitor it.

New or changed test files are in `src/Microsoft.VisualStudio.SlnGen.UnitTests/`. Every test asserts on runtime behaviour.

1. **Build:** `dotnet build SlnGen.sln -c Debug` must succeed with no warnings
   (`MSBuildTreatWarningsAsErrors` is on, and StyleCop is active).
2. **`ProjectReferenceClosureTests.cs`** (new, all TFMs). Use `ProjectCreator` to create A, B, C, L1, L2 under
   `TestRootPath` with references A→L1, A→C, B→L1, C→L2. Load them with
   `ProjectLoader.LoadProjects(...)` (the loader selected for the current runtime) into a fresh
   `ProjectCollection`, with `globalProperties` from `new ProgramArguments().GetGlobalProperties()`. Do not call
   `ProjectLoader.LoadProjects` (on net472 it needs an MSBuild.exe `FileInfo`); instead construct the loaders directly as
   `MSBuildProjectLoaderTests` does (`new ProjectGraphProjectLoader(logger)`, and on NETFRAMEWORK also `new LegacyProjectLoader(logger)`)
   and write the tests as a `[Theory]` over both loaders. Assert:
   - `GetProjects(A)` paths == {A, C, L1, L2}, with A first;
   - `GetProjects(B)` == {B, L1}, with B first;
   - `GetProjects(C)` == {C, L2}, with C first;
   - `GetProjects(L2)` == {L2};
   - a project that is only referenced under `Condition="'$(TargetFramework)' == 'net46'"` from a
     multi-targeted SDK project (`TargetFrameworks=net46;netcoreapp2.0`) is part of the closure. Reuse the
     setup pattern from `SlnGenTests.ProjectReferencesDeterminedInCrossTargetingBuild`. If SDK evaluation
     is not available on that TFM, put this case under `#if NETFRAMEWORK`.
   - a traversal project: with `LegacyProjectLoader` only (NETFRAMEWORK), `IsTraversal=true` with `ProjectFile` items A and B
     returns {dirs, A, C, L1, L2, B}; for the graph loader (which only follows `ProjectReference`), use a dirs.proj with
     `ProjectReference` items instead and assert the same set.
   - the same path evaluated twice with different global properties (a `ProjectReference` with `Properties` metadata) does not throw
     and the closure still contains the project.
   - an entry path that was never loaded returns an empty list.
3. **`ArgumentsTests.cs`** (extend):
   - `Program.Execute(new[] { "--solution-per-project", "a.csproj" }, console, (args, c) => args.SolutionPerProject ? 0 : 1)` returns 0, so the option parses.
   - `ValidateSolutionPerProject` with `SolutionFileFullPath = { "x.sln" }` returns false and `TestLogger.ErrorMessages` has one entry.
   - With `LaunchVisualStudio = { "true" }`, it returns true and `logger.Warnings.ShouldHaveSingleItem()`
     (`TestLogger.LogWarning` fills `Warnings`, not `WarningMessages`).
   - With the option off, it returns true and both `ErrorMessages` and `Warnings` are empty, even if `-o` is set.
4. **`SlnFileTests.cs`** (extend): `GetSolutionFileFullPath` returns `<projectDir>\<name>.sln` by default,
   `<dir>\<name>.sln` with `SolutionDirectoryFullPath`, `.slnx` with `Format="slnx"`, and the `SlnGenProjectName` override.
   This is a regression guard for the extraction in Step 3.
5. **`SolutionPerProjectTests.cs`** (new, `#if NETFRAMEWORK` like `SlnGenTests`, end-to-end). This is the spec's test scenario.
   Create A, B, C, L1, L2 as plain `ProjectCreator.Create(...)` projects with `ItemProjectReference`, in separate folders under
   `TestRootPath` (empty `Directory.Build.props/targets` as in `SlnGenTests`). The child process needs VS/MSBuild to be discoverable,
   same as `SlnGenTests`. Start `Path.Combine(Environment.CurrentDirectory, "slngen.exe")` as a child `Process` with
   `--nologo --solution-per-project --launch:false A.csproj B.csproj C.csproj` and capture stdout and stderr. Assert:
   - exit code 0;
   - A.sln, B.sln and C.sln exist next to their projects, and `SolutionFile.Parse(...).ProjectsInOrder` absolute paths
     (as sets) are {A, C, L1, L2}, {B, L1} and {C, L2};
   - no L1.sln or L2.sln was created;
   - stdout has exactly one `Loading project references...` line. That shows the graph was loaded once.
   - **Equivalence:** for each of A, B and C, run slngen.exe again *without* the flag, with only that project and
     `-o <TestRootPath>\single\<name>.sln --launch:false`. Assert that its project set equals the per-project solution's set.
   - Also add these cases, each a separate `[Fact]` with its own process run:
     - `-o x.sln` with the flag gives a non-zero exit code, the error text in the output, and no .sln files written;
     - two entry projects with the same file name in different folders plus `-d <shared dir>` give a non-zero exit code,
       a collision error naming both, and no .sln written in the shared dir;
     - `--launch:true` with the flag gives exit code 0, the warning in the output, and the solutions are generated.
       This must not open VS, because the launch is skipped.
6. **Run the tests:** `dotnet test src/Microsoft.VisualStudio.SlnGen.UnitTests -c Debug`. All TFMs must pass, including the
   existing tests (regression check for single-solution mode). For quick iteration use
   `--framework net472` and `--framework net10.0`, then do one full run at the end.
7. **Manual CLI smoke test (agent-run, matches "Testing the changes" exactly):** in the scratchpad directory, write the 5
   dummy projects with a PowerShell script. Run the built tool
   (`src/Microsoft.VisualStudio.SlnGen/bin/Debug/net472/slngen.exe`, and `dotnet .../net10.0/slngen.dll`) with
   `--solution-per-project --launch:false A.csproj B.csproj C.csproj`. Use a PowerShell script to read each generated .sln
   (`Project(` lines) and compare the project sets with the expected sets, then exit non-zero on any mismatch.
   The script also runs each project alone (without the flag, with `-o <scratch>\single\<name>.sln --launch:false`) and compares
   those sets with the per-project solutions (the spec's equivalence check). The script must exit 0 for both runtimes.

