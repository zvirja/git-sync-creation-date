using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;
using static CreationDateSync.ConsoleUtil;

namespace CreationDateSync
{
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var rootCmd = new RootCommand
            {
                new Option<DateTimeOffset?>(
                    "--creation-time",
                    "Date and time to use for files that were committed in the first commit. Useful if repo was migrated from other place. " +
                    "Format: DateTime in ISO format (e.g. 1994-02-13 or 1994-02-13T17:14:00). " +
                    "Default: Use initial commit date and time."),
                new Option<FileInfo>(
                    "--creation-time-file",
                    "File with date and time to use for files that were committed in the first commit. Useful if repo was migrated from other place. " +
                    "Format: A line per file. Each line should contain {relativePath}:DateTime pairs. " +
                    "Default: Use initial commit date and time.")
            };
            rootCmd.Description = "Scan the repository and update the File Creation attribute for committed files to match the dates files appeared in the commit history";
            rootCmd.Handler = CommandHandler.Create<DateTimeOffset?, FileInfo>(Run);

            return await rootCmd.InvokeAsync(args);
        }

        private static int Run(DateTimeOffset? creationTime, FileInfo creationTimeFile)
        {
            WriteLineConsole(
                "Scanning the repository and updating the File Creation attribute for committed files to match the dates files appeared in the commit history." +
                Environment.NewLine +
                "Written by Oleksandr Povar (@zvirja)", ConsoleColor.DarkGray);
            WriteNewLine();

            var sw = Stopwatch.StartNew();

            var repositoryPath = GetRepositoryPath();
            if (repositoryPath == null)
            {
                WriteError("Current directory is not a part of Git repo.");
                return 1;
            }

            try
            {
                using var repo = new Repository(repositoryPath);
 
                var repoWorkingDirectory = repo.Info.WorkingDirectory;
                WriteLineConsole($"Current HEAD: {repo.Head.Tip.Sha} ({repo.Head.FriendlyName})");

                var creationStamps = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

                if (creationTimeFile != null)
                {
                    WriteConsole("Importing creation time from file... ");
                    var importedCount = ImportCreationStampsFromFile(creationTimeFile, creationStamps);
                    WriteLineConsole($"Done! Imported {importedCount} records.", ConsoleColor.DarkGreen);
                }

                var initialCommitTimeDesc = creationTime != null ? $" (initial commit time: {creationTime.Value:s})" : string.Empty;
                WriteConsole($"Collecting creation dates from commits{initialCommitTimeDesc}... ");
                var importedCommitsCount = ImportCreationStampsFromCommits(repo, creationStamps, creationTime);
                WriteLineConsole($"Done! Collected {importedCommitsCount} new records.", ConsoleColor.DarkGreen);

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

                    if (!creationStamps.TryGetValue(relativePath, out var creationDate))
                    {
                        warnings.Add($"[Skipped] Cannot find file in commits: {relativePath}");
                        PrintStatus();
                        continue;
                    }

                    if (!File.Exists(absolutePath))
                    {
                        warnings.Add($"[Skipped] File no longer exist: {relativePath}");
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

        private static int ImportCreationStampsFromFile(FileInfo creationStampsFile, Dictionary<string, DateTimeOffset> stamps)
        {
            using var fileReader = creationStampsFile.OpenText();

            int linePos = -1;
            int importedCount = 0;

            try
            {
                while (!fileReader.EndOfStream)
                {
                    linePos++;
                    var line = fileReader.ReadLine();
                    if(string.IsNullOrEmpty(line))
                        continue;
                    
                    var pair = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
                    var path = pair[0];
                    var stamp = pair[1];
 
                    // Normalize slashes
                    path = path.Replace("\\", "/");
                    
                    stamps[path] = DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture);
                    importedCount++;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Wrong creation time file format. Error during parsing line {linePos}", ex);
            }

            return importedCount;
        }

        private static int ImportCreationStampsFromCommits(IRepository repo, Dictionary<string, DateTimeOffset> stamps, DateTimeOffset? initialCommitTimeOverride)
        {
            int importedCount = 0;
 
            var commits = repo.Commits
                .QueryBy(
                    new CommitFilter
                    {
                        SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse,
                        FirstParentOnly = true
                    })
                .ToArray();

            var initialCommit = commits.First();

            foreach (var commit in commits)
            {
                var commitDate = commit.Committer.When;
                if (commit == initialCommit && initialCommitTimeOverride != null)
                {
                    commitDate = initialCommitTimeOverride.Value;
                }

                var parentCommit = commit.Parents.FirstOrDefault();
                var diff = repo.Diff.Compare<TreeChanges>(parentCommit?.Tree, commit.Tree);

                foreach (var change in diff.Added)
                {
                    ImportStamp(change.Path, commitDate);
                }

                // If item has been renamed (or at least git thinks so), let's try to preserve it's old creation date.
                foreach (var change in diff.Renamed)
                {
                    ImportStamp(
                        change.Path,
                        stamps.TryGetValue(change.OldPath, out var oldCreationDate)
                            ? oldCreationDate
                            : commitDate);
                }
            }

            void ImportStamp(string path, DateTimeOffset stamp)
            {
                if (stamps.TryAdd(path, stamp))
                    importedCount++;
            }

            return importedCount;
        }

        private static IEnumerable<string> CollectAllFiles(IRepository repo)
        {
            return repo.Diff
                .Compare<TreeChanges>(null, repo.Head.Tip.Tree)
                .Select(x => x.Path);
        }
    }
}