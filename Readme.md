# git-sync-creation-date

It's a dotnet tool to synchronize file creation date on file system with the commit history. Tool scans the commit history and extract creation date for each file (date when a particular file appeared for the first time).

It might be useful if your tooling relies on the file creation dates (e.g. to provide right copyright header).

# Installation

Tool is distributed as a dotnet tool. To install it you should have .NET Core 2.1 installed (or above). Then run the following command:
```
dotnet install -g git-sync-creation-date
```

# Usage

To run the tool just navigate to a directory which is a part of the repo (usual repository root) and run the `git-sync-creation-date` command from your command line. The tool will update creation date for all the files in the current and nested directories.
