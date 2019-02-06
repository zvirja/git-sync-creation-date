using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LibGit2Sharp;

namespace CreationDateSync
{
    public class Program
    {
        public static int Main()
        {
            WriteLineConsole(
                "Tool scans your repository and updates file creation attributes to match the dates files appeared in the commit history." +
                Environment.NewLine +
                "Written by Alex Povar (@zvirja)", ConsoleColor.DarkGray);
            Console.WriteLine();

            var sw = Stopwatch.StartNew();

            var repositoryPath = GetRepositoryPath();
            if (repositoryPath == null)
            {
                WriteError("Current directory is not a part of Git repo.");
                return 1;
            }

            try
            {
                using (var repo = new Repository(repositoryPath))
                {
                    var repoWorkingDirectory = repo.Info.WorkingDirectory;

                    WriteConsole("Collecting creation dates from commits... ");
                    var creationDates = CollectCreationDates(repo);
                    WriteLineConsole($"Done! Collected {creationDates.Keys.Count} records.", ConsoleColor.DarkGreen);

                    WriteConsole("Discovering files to process from the last commit... ");
                    var allFiles = CollectAllFiles(repo);
                    WriteLineConsole("Done!", ConsoleColor.DarkGreen);

                    const int STATUS_UPDATE_BATCH_SIZE = 20;
                    var warnings = new List<string>();
                    var processed = 0;

                    void PrintStatus(bool final = false)
                    {
                        Console.SetCursorPosition(0, Console.CursorTop);
                        WriteConsole("Processing files... ");
                        if (final)
                            WriteConsole("Done! ", ConsoleColor.DarkGreen);
                        WriteConsole($"Processed: {processed} ", ConsoleColor.DarkGreen);
                        WriteConsole($"Warnings: {warnings.Count}",
                            warnings.Count > 0 ? ConsoleColor.DarkYellow : Console.ForegroundColor);
                    }

                    foreach (var relativePath in allFiles)
                    {
                        processed++;

                        var absolutePath = Path.Combine(repoWorkingDirectory, relativePath);

                        if (!creationDates.TryGetValue(relativePath, out var creationDate))
                        {
                            warnings.Add($"Cannot find info for: {relativePath}");
                            PrintStatus();
                            continue;
                        }

                        if (!File.Exists(absolutePath))
                        {
                            warnings.Add($"File does not exist: {relativePath}");
                            PrintStatus();
                            continue;
                        }

                        File.SetCreationTimeUtc(absolutePath, creationDate.UtcDateTime);
                        if (processed % STATUS_UPDATE_BATCH_SIZE == 0)
                            PrintStatus();
                    }

                    PrintStatus(true);

                    Console.WriteLine();
                    WriteLineConsole($"Time elapsed: {sw.ElapsedMilliseconds}ms");
                    if (warnings.Count > 0)
                    {
                        const string tab = "    ";
                        Console.WriteLine();

                        WriteLineConsole("WARNINGS:", ConsoleColor.DarkYellow);
                        foreach (var warning in warnings)
                            WriteLineConsole($"{tab}{warning}", ConsoleColor.DarkYellow);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteError("Unexpected error happened" + Environment.NewLine + ex);
                return 1;
            }

            return 0;
        }

        private static string GetRepositoryPath()
        {
            var currentDirectory = Directory.GetCurrentDirectory();
            var repositoryPath = Repository.Discover(currentDirectory);

            if (string.IsNullOrEmpty(repositoryPath))
                return null;

            if (!Repository.IsValid(repositoryPath))
                return null;

            return repositoryPath;
        }

        private static Dictionary<string, DateTimeOffset> CollectCreationDates(IRepository repo)
        {
            var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

            var commits = repo.Commits
                .QueryBy(
                    new CommitFilter
                    {
                        SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse,
                        FirstParentOnly = true
                    })
                .ToArray();

            foreach (var commit in commits)
            {
                var commitDate = commit.Committer.When;

                var parentCommit = commit.Parents.FirstOrDefault();
                var diff = repo.Diff.Compare<TreeChanges>(parentCommit?.Tree, commit.Tree);

                foreach (var change in diff.Added)
                {
                    result.TryAdd(change.Path, commitDate);
                }

                // If item has been renamed (or at least git thinks so), let's try to preserve it's old creation date.
                foreach (var change in diff.Renamed)
                {
                    result.TryAdd(
                        change.Path,
                        result.TryGetValue(change.OldPath, out var oldCreationDate)
                            ? oldCreationDate
                            : commitDate);
                }
            }

            return result;
        }

        private static IEnumerable<string> CollectAllFiles(IRepository repo)
        {
            return repo.Diff
                .Compare<TreeChanges>(null, repo.Head.Tip.Tree)
                .Select(x => x.Path);
        }

        private static void WriteError(string message) => WriteLineConsole("ERROR: " + message, ConsoleColor.Red);

        private static void WriteConsole(string message, ConsoleColor? color = null)
        {
            using (new ConsoleColorSwitcher(color ?? Console.ForegroundColor))
            {
                Console.Write(message);
            }
        }

        private static void WriteLineConsole(string message, ConsoleColor? color = null)
        {
            using (new ConsoleColorSwitcher(color ?? Console.ForegroundColor))
            {
                Console.WriteLine(message);
            }
        }

        private class ConsoleColorSwitcher : IDisposable
        {
            public ConsoleColorSwitcher(ConsoleColor color)
            {
                Console.ForegroundColor = color;
            }

            public void Dispose()
            {
                Console.ResetColor();
            }
        }
    }
}