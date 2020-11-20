[![Build status](https://ci.appveyor.com/api/projects/status/t3djtth456h5ff3x/branch/master?svg=true)](https://ci.appveyor.com/project/Zvirja/git-sync-creation-date/branch/master) [![Nuget version](https://img.shields.io/nuget/v/git-sync-creation-date.svg)](https://www.nuget.org/packages/git-sync-creation-date/)

# git-sync-creation-date

It's a dotnet tool to synchronize file creation date on file system with the commit history. Tool scans the commit history and extract creation date for each file (date when a particular file appeared for the first time).

It might be useful if your tooling relies on the file creation dates (e.g. to provide right copyright header).

# Installation

Tool is distributed as a dotnet tool. To install it you should have .NET Core 2.1 installed (or above). Then run the following command:
```
dotnet tool install -g git-sync-creation-date
```

# Usage

To run the tool just navigate to a directory which is a part of the repo (usual repository root), open command line and run the tool by name:
```
git-sync-creation-date
```

The tool will update creation date for all the files in the current and nested directories.

## Options

Run the tool with `--help` or just `-h` flag to see the possible options.
