#nullable enable
using System.Diagnostics;

namespace VRCX
{
    public sealed class ShellWorldPhotoLauncher : IWorldPhotoLauncher
    {
        public void Open(string canonicalFilePath)
        {
            Process.Start(CreateStartInfo(canonicalFilePath));
        }

        public static ProcessStartInfo CreateStartInfo(string canonicalFilePath)
        {
            return new ProcessStartInfo
            {
                FileName = canonicalFilePath,
                UseShellExecute = true
            };
        }
    }
}
