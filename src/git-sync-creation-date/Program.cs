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
        public static int Main(string[] args)
        {
            WriteLineConsole("Tool scans your repository and updates file creation attributes to match the dates files appeared in the commit history." +
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

                    WriteConsole("Collecting creation dates from commits... ", ConsoleColor.DarkGray);
                    var creationDates = CollectCreationDates(repo);
                    WriteLineConsole($" Done! Collected {creationDates.Keys.Count} records.", ConsoleColor.DarkGreen);

                    WriteConsole("Discovering files to process...", ConsoleColor.DarkGray);
                    var allFiles = CollectAllFiles(repo).ToArray();
                    WriteLineConsole($" Done! Discovered {allFiles.Length} files.", ConsoleColor.DarkGreen);

                    WriteLineConsole("Updating files...", ConsoleColor.DarkGray);

                    int counterUpdated = 0;
                    int counterWarnings = 0;
                    foreach (var relativePath in allFiles)
                    {
                        var absolutePath = Path.Combine(repoWorkingDirectory, relativePath);

                        if (!creationDates.TryGetValue(relativePath, out var creationDate))
                        {
                            WriteWarn($"Cannot find info for: {relativePath}");
                            counterWarnings++;
                            continue;
                        }

                        if (!File.Exists(absolutePath))
                        {
                            WriteWarn($"File does not exist: {relativePath}");
                            counterWarnings++;
                            continue;
                        }

                        File.SetCreationTimeUtc(absolutePath, creationDate.UtcDateTime);
                        WriteLineConsole($"[{creationDate:O}] => {relativePath}");
                        counterUpdated++;
                    }

                    Console.WriteLine();
                    const string tab = "    ";
                    WriteLineConsole("~~~~~~~~~~~~~~~~~~~~~~~~~~~", ConsoleColor.DarkGray);
                    WriteLineConsole("STATISTICS", ConsoleColor.DarkGray);
                    WriteLineConsole($"{tab}Updated: {counterUpdated}", ConsoleColor.DarkGray);
                    WriteLineConsole(
                        $"{tab}Skipped: {counterWarnings}",
                        counterWarnings > 0 ? ConsoleColor.Yellow : ConsoleColor.DarkGray);
                    WriteLineConsole($"{tab}Time: {sw.ElapsedMilliseconds}ms", ConsoleColor.DarkGray);
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
            var committedFiles = repo.Diff.Compare<TreeChanges>(null, repo.Head.Tip.Tree).Select(x => x.Path);

            var workDirectoryStatus = repo.RetrieveStatus();
            return committedFiles
                .Except(workDirectoryStatus.Removed.Select(x => x.FilePath));
        }

        private static void WriteError(string message) => WriteLineConsole("ERROR: " + message, ConsoleColor.Red);
        private static void WriteWarn(string message) => WriteLineConsole("WARN: " + message, ConsoleColor.Yellow);

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