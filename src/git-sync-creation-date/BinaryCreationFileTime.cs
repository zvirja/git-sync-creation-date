using System;
using System.Collections.Generic;
using System.IO;
using MessagePack;

namespace CreationDateSync
{
    public static class BinaryCreationFileTime
    {
        public static SerializedTree Deseriazize(FileInfo file)
        {
            using var stream = file.OpenRead();
            return MessagePackSerializer.Deserialize<SerializedTree>(stream);
        }

        [MessagePackObject]
        public class SerializedTree
        {
            [Key(0)] public char[] Strings = Array.Empty<char>();
            [Key(1)] public Node[] Nodes = Array.Empty<Node>();

            public IEnumerable<(string path, DateTime time)> GetCreationStampsFromPath(string rootPath)
            {
                var rootNodeIdx = FindNodeIndexByPath(rootPath);
                if (rootNodeIdx < 0)
                    throw new ArgumentException($"Bin file does not contain directory by path: {rootPath}");

                return IterateDescendants(rootNodeIdx, "");

                IEnumerable<(string, DateTime)> IterateDescendants(int nodeIdx, string nodePath)
                {
                    for (var childIdx = Nodes[nodeIdx].FirstChildIndex; childIdx != 0; childIdx = Nodes[childIdx].IsLastChild ? 0 : childIdx + 1)
                    {
                        var name = GetName((int) childIdx).ToString();
                        var path = nodePath == "" ? name : nodePath + "/" + name;

                        // If no children, it's a file, not a folder, so we return it.
                        if (Nodes[childIdx].FirstChildIndex == 0)
                        {
                            // Choose date in a middle of a year, as only year matters.
                            yield return (path, Nodes[childIdx].Time);
                        }
                        else
                        {
                            foreach (var d in IterateDescendants((int) childIdx, path))
                                yield return d;
                        }
                    }
                }
            }

            private ReadOnlyMemory<char> GetName(int nodeIdx)
            {
                var strIdx = Nodes[nodeIdx].NameStrIdx;
                var len = (int) Strings[strIdx];

                return Strings.AsMemory((int) (strIdx + 1), len);
            }

            private int FindNodeIndexByPath(string path)
            {
                var chunks = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return FindNodeIndexByChunks(chunks, Nodes.Length - 1);

                int FindNodeIndexByChunks(Span<string> chunks, int current)
                {
                    if (chunks.IsEmpty)
                        return current;

                    var childName = chunks[0];

                    for (var childIdx = Nodes[current].FirstChildIndex; childIdx != 0; childIdx = Nodes[childIdx].IsLastChild ? 0 : childIdx + 1)
                    {
                        if (childName.Equals(GetName((int) childIdx).ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            return FindNodeIndexByChunks(chunks.Slice(1), (int) childIdx);
                        }
                    }

                    return -1;
                }
            }

            [MessagePackObject]
            public struct Node
            {
                private const uint FirstChildIndexMask = 0x_7FFF_FFFF;
                private const uint IsLastChildMask = 0x_8000_0000;

                [Key(0)] public int NameStrIdx;
                [Key(1)] public uint _childInfo;
                [Key(2)] public DateTime Time;

                [IgnoreMember]
                public uint FirstChildIndex => _childInfo & FirstChildIndexMask;

                [IgnoreMember]
                public bool IsLastChild => (_childInfo & IsLastChildMask) != 0;
            }
        }
    }
}