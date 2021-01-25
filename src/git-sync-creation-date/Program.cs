using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
                new Option<FileInfo?>(
                    "--creation-time-txt-file",
                    "Path to a text file with creation stamps for files which were created prior to the first commit. Useful if repo was migrated from other place. " +
                    "Format: A line per file. Each line should contain {relativePath}:DateTime pairs. " +
                    "Default: Use initial commit date and time."),
                new Option<FileInfo?>(
                    "--creation-time-bin-file",
                    "Path to a binary file with creation stamps for files which were created prior to the first commit. Useful if repo was migrated from other place. " +
                    "Default: Use initial commit date and time."),
                new Option<string>(
                    "--creation-time-file-prefix",
                    "Path to a folder which should be used as a repo root. Allows to import a subset of the file entries only. " +
                    "Specify a slash ('/') to read the whole file.")
            };
            rootCmd.Description = GetHelpInfoLine();
            rootCmd.Handler = CommandHandler.Create<DateTimeOffset?, FileInfo?, FileInfo?, string?>(Run);

            return await rootCmd.InvokeAsync(args);
        }

        private static int Run(DateTimeOffset? creationTime, FileInfo? creationTimeTxtFile, FileInfo? creationTimeBinFile, string? creationTimeFilePrefix)
        {
            WriteLineConsole(GetHelpInfoLine(), ConsoleColor.DarkGray);
            WriteNewLine();

            if (creationTimeTxtFile != null && creationTimeBinFile != null)
            {
                WriteError("Cannot specify both 'txt' and 'bin' files in the same time.");
                return 1;
            }

            if ((creationTimeTxtFile != null || creationTimeBinFile != null) && string.IsNullOrEmpty(creationTimeFilePrefix))
            {
                WriteError("The --creation-time-file-prefix parameter should be specified when importing file. Set it to '/' to import the whole file.");
                return 1;
            }

            // Normalize slashes
            creationTimeFilePrefix = creationTimeFilePrefix?.Replace("\\", "/");

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

                if (creationTimeTxtFile != null)
                {
                    WriteConsole($"Importing creation time from text file (with prefix to import: '{creationTimeFilePrefix}')... ");
                    var stats = ImportCreationStampsFromTextFile(creationTimeTxtFile, creationTimeFilePrefix!, creationStamps);
                    WriteLineConsole($"Done! Imported records: {stats.imported}, skipped due to prefix mismatch: {stats.skipped}.", ConsoleColor.DarkGreen);
                }

                if (creationTimeBinFile != null)
                {
                    WriteConsole("Deserializing binary file with creation stamps to memory... ");
                    var treeInfo = BinaryCreationFileTime.Deseriazize(creationTimeBinFile);
                    WriteLineConsole("Done!", ConsoleColor.DarkGreen);

                    WriteConsole($"Importing creation time from bin file (with prefix to import: '{creationTimeFilePrefix}')... ");
                    int importCount = 0;
                    foreach (var (path, time) in treeInfo.GetCreationStampsFromPath(creationTimeFilePrefix))
                    {
                        creationStamps[path] = time;
                        importCount++;
                    }
                    WriteLineConsole($"Done! Imported records: {importCount}.", ConsoleColor.DarkGreen);
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

        private static string? GetRepositoryPath()
        {
            var currentDirectory = Directory.GetCurrentDirectory();
            var repositoryPath = Repository.Discover(currentDirectory);

            if (string.IsNullOrEmpty(repositoryPath))
                return null;

            if (!Repository.IsValid(repositoryPath))
                return null;

            return repositoryPath;
        }

        private static (int imported, int skipped) ImportCreationStampsFromTextFile(FileInfo creationStampsFile, string prefixToPath, Dictionary<string, DateTimeOffset> stamps)
        {
            if (!prefixToPath.EndsWith('/'))
                prefixToPath += "/";

            using var fileReader = creationStampsFile.OpenText();

            int linePos = -1;
            int importedCount = 0;
            int skippedCount = 0;

            try
            {
                while (!fileReader.EndOfStream)
                {
                    linePos++;
                    var line = fileReader.ReadLine();
                    if(string.IsNullOrEmpty(line))
                        continue;
                    
                    // Normalize slashes
                    line = line!.Replace("\\", "/");

                    if (prefixToPath != "/")
                    {
                        if (!line.StartsWith(prefixToPath, StringComparison.OrdinalIgnoreCase))
                        {
                            skippedCount++;
                            continue;
                        }
 
                        // Relative path starts inside the folder specified by prefix
                        line = line.Substring(prefixToPath.Length);
                    }

                    var pair = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
                    var path = pair[0];
                    var stamp = pair[1];
 
                    stamps[path] = DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture);
                    importedCount++;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Wrong creation time file format. Error during parsing line {linePos}", ex);
            }

            return (importedCount, skippedCount);
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

        private static string GetHelpInfoLine()
        {
            var infoVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            return 
                $"Scan the repository and update the File Creation attribute for committed files to match the dates files appeared in the commit history.{Environment.NewLine}" +
                $"Version {infoVersion}. Written by Oleksandr Povar (@zvirja)";
        }
    }
}