using System;
using System.Runtime.InteropServices;

namespace Listenarr.Api.Services
{
    public static class NativeFileMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [DllImport("libc", SetLastError = true)]
        private static extern int link(string oldpath, string newpath);

        public static bool CreateHardLinkWindows(string newPath, string existingPath)
        {
            return CreateHardLink(newPath, existingPath, IntPtr.Zero);
        }

        public static int CreateHardLinkUnix(string existingPath, string newPath)
        {
            return link(existingPath, newPath);
        }
    }
}
