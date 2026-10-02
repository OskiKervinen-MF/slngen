// Copyright (c) Microsoft Corporation.
//
// Licensed under the MIT license.
#if NETFRAMEWORK
using Microsoft.Build.Construction;
using Microsoft.Build.Utilities.ProjectCreation;
using Shouldly;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Microsoft.VisualStudio.SlnGen.UnitTests
{
    /// <summary>
    /// End-to-end tests that run slngen.exe with --solution-per-project.
    /// </summary>
    public sealed class SolutionPerProjectTests : TestBase
    {
        /// <summary>
        /// Verifies the scenario from the specification: each entry project gets exactly its own dependencies, the graph is loaded once,
        /// and each solution equals the result of running slngen for that project alone.
        /// </summary>
        [Fact]
        public void EachEntryProjectGetsItsOwnSolution()
        {
            ProjectCreator l1 = CreateProject("L1");
            ProjectCreator l2 = CreateProject("L2");
            ProjectCreator c = CreateProject("C", l2);
            ProjectCreator b = CreateProject("B", l1);
            ProjectCreator a = CreateProject("A", l1, c);

            (int exitCode, string output) = RunSlnGen("--solution-per-project", "--launch:false", a, b, c);

            exitCode.ShouldBe(0, output);

            GetSolutionProjects(Path.ChangeExtension(a, ".sln")).ShouldBe(GetPaths(a, c, l1, l2), ignoreOrder: true);
            GetSolutionProjects(Path.ChangeExtension(b, ".sln")).ShouldBe(GetPaths(b, l1), ignoreOrder: true);
            GetSolutionProjects(Path.ChangeExtension(c, ".sln")).ShouldBe(GetPaths(c, l2), ignoreOrder: true);

            // Only the entry projects get a solution
            File.Exists(Path.ChangeExtension(l1, ".sln")).ShouldBeFalse();
            File.Exists(Path.ChangeExtension(l2, ".sln")).ShouldBeFalse();

            // One load of the combined graph is what makes the mode worthwhile
            CountOccurrences(output, "Loading project references...").ShouldBe(1, output);

            // Each solution must match what a run for that single project produces
            foreach (ProjectCreator entry in new[] { a, b, c })
            {
                string singleSolution = Path.Combine(TestRootPath, "single", Path.GetFileName(Path.ChangeExtension(entry, ".sln")));

                (int singleExitCode, string singleOutput) = RunSlnGen($"--launch:false", $"--solutionfile:{singleSolution}", entry);

                singleExitCode.ShouldBe(0, singleOutput);

                GetSolutionProjects(Path.ChangeExtension(entry, ".sln")).ShouldBe(GetSolutionProjects(singleSolution), ignoreOrder: true);
            }
        }

        /// <summary>
        /// Verifies that --solutionfile is refused and nothing is written.
        /// </summary>
        [Fact]
        public void SolutionFileIsRejected()
        {
            ProjectCreator a = CreateProject("A");
            ProjectCreator b = CreateProject("B");

            (int exitCode, string output) = RunSlnGen("--solution-per-project", "--launch:false", $"--solutionfile:{Path.Combine(TestRootPath, "x.sln")}", a, b);

            exitCode.ShouldNotBe(0, output);
            output.ShouldContain("--solutionfile");
            Directory.GetFiles(TestRootPath, "*.sln", SearchOption.AllDirectories).ShouldBeEmpty();
        }

        /// <summary>
        /// Verifies that two projects that would write the same solution file are reported and nothing is written.
        /// </summary>
        [Fact]
        public void CollidingSolutionPathsAreRejected()
        {
            ProjectCreator first = ProjectCreator.Create(Path.Combine(TestRootPath, "d1", "X.csproj")).Save();
            ProjectCreator second = ProjectCreator.Create(Path.Combine(TestRootPath, "d2", "X.csproj")).Save();
            string sharedDirectory = Path.Combine(TestRootPath, "shared");

            (int exitCode, string output) = RunSlnGen("--solution-per-project", "--launch:false", $"--solutiondir:{sharedDirectory}", first, second);

            exitCode.ShouldNotBe(0, output);
            output.ShouldContain("would all generate the solution");
            output.ShouldContain(first.FullPath);
            output.ShouldContain(second.FullPath);
            Directory.Exists(sharedDirectory).ShouldBeFalse();
            Directory.GetFiles(TestRootPath, "*.sln", SearchOption.AllDirectories).ShouldBeEmpty();
        }

        /// <summary>
        /// Verifies that an explicit request to launch Visual Studio is ignored with a warning while the solutions are still generated.
        /// </summary>
        [Fact]
        public void ExplicitLaunchIsIgnoredWithWarning()
        {
            ProjectCreator a = CreateProject("A");

            (int exitCode, string output) = RunSlnGen("--solution-per-project", "--launch:true", a);

            exitCode.ShouldBe(0, output);
            output.ShouldContain("Visual Studio is not launched when --solution-per-project is specified.");
            File.Exists(Path.ChangeExtension(a, ".sln")).ShouldBeTrue();
        }

        /// <summary>
        /// Appends a line to a buffer that the output and error streams write to concurrently.
        /// </summary>
        /// <param name="buffer">The shared buffer.</param>
        /// <param name="line">The line to append.</param>
        private static void AppendLine(StringBuilder buffer, string line)
        {
            lock (buffer)
            {
                buffer.AppendLine(line);
            }
        }

        /// <summary>
        /// Counts how often a value appears in a string.
        /// </summary>
        /// <param name="text">The text to search.</param>
        /// <param name="value">The value to count.</param>
        /// <returns>The number of occurrences.</returns>
        private static int CountOccurrences(string text, string value)
        {
            int count = 0;

            for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        /// <summary>
        /// Gets the full paths of the specified project files.
        /// </summary>
        /// <param name="projects">The project files.</param>
        /// <returns>The full paths.</returns>
        private static string[] GetPaths(params ProjectCreator[] projects) => projects.Select(i => Path.GetFullPath(i.FullPath)).ToArray();

        /// <summary>
        /// Gets the full paths of the projects in a solution file.
        /// </summary>
        /// <param name="solutionPath">The path to the solution file.</param>
        /// <returns>The full paths of the projects in the solution.</returns>
        private static string[] GetSolutionProjects(string solutionPath)
        {
            File.Exists(solutionPath).ShouldBeTrue($"Solution file \"{solutionPath}\" was not generated");

            return SolutionFile.Parse(solutionPath).ProjectsInOrder.Select(i => Path.GetFullPath(i.AbsolutePath)).ToArray();
        }

        /// <summary>
        /// Creates a project in its own directory that references the specified projects.
        /// </summary>
        /// <param name="name">The name of the project.</param>
        /// <param name="references">The projects it references.</param>
        /// <returns>The saved project.</returns>
        private ProjectCreator CreateProject(string name, params ProjectCreator[] references)
        {
            ProjectCreator project = ProjectCreator.Create(Path.Combine(TestRootPath, name, $"{name}.csproj"));

            foreach (ProjectCreator reference in references)
            {
                project.ItemProjectReference(reference);
            }

            return project.Save();
        }

        /// <summary>
        /// Runs slngen.exe from the test output directory.
        /// </summary>
        /// <param name="arguments">Command-line switches followed by the projects to generate solutions for.</param>
        /// <returns>The exit code and the combined standard output and error.</returns>
        private (int ExitCode, string Output) RunSlnGen(params object[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(Path.Combine(Environment.CurrentDirectory, "slngen.exe"))
            {
                Arguments = string.Join(" ", new[] { "--nologo" }.Concat(arguments.Select(i => $"\"{(i is ProjectCreator project ? project.FullPath : i)}\""))),
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = TestRootPath,
            };

            StringBuilder output = new StringBuilder();

            using Process process = new Process { StartInfo = startInfo };

            // Both streams are read asynchronously so a full pipe cannot block the child process
            process.OutputDataReceived += (_, e) => AppendLine(output, e.Data);
            process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
            {
                process.Kill();

                throw new TimeoutException($"slngen.exe did not exit in time.  Output:{Environment.NewLine}{output}");
            }

            // The parameterless overload waits until the redirected streams are fully read
            process.WaitForExit();

            return (process.ExitCode, output.ToString());
        }
    }
}
#endif
