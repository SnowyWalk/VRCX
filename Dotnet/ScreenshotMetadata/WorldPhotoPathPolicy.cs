#nullable enable
using System;
using System.IO;

namespace VRCX
{
    public static class WorldPhotoPathPolicy
    {
        public static bool IsSafeCurrentPng(string rootPath, string candidatePath)
        {
            if (!string.Equals(Path.GetExtension(candidatePath), ".png", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!WorldPhotoIndexDatabase.IsContainedBy(rootPath, candidatePath))
                return false;

            var root = WorldPhotoIndexDatabase.CanonicalizeDirectory(rootPath);
            var candidate = Path.GetFullPath(candidatePath);
            var current = new DirectoryInfo(Path.GetDirectoryName(candidate)!);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.LinkTarget != null)
                    return false;
                if (string.Equals(current.FullName, root, comparison))
                    break;
                current = current.Parent;
            }

            if (current == null)
                return false;
            var file = new FileInfo(candidate);
            return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0 && file.LinkTarget == null;
        }
    }
}
