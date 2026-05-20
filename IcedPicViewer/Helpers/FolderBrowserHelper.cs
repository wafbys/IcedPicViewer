using System.Runtime.InteropServices;

namespace IcedPicViewer.Helpers;

public static class FolderBrowserHelper
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, IntPtr pszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;
    private const uint BIF_EDITBOX = 0x0010;

    public static string? SelectFolder(string title, IntPtr ownerHandle = default)
    {
        var bi = new BROWSEINFO
        {
            hwndOwner = ownerHandle,
            pidlRoot = IntPtr.Zero,
            pszDisplayName = Marshal.AllocHGlobal(260 * 2),
            lpszTitle = title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX,
            lpfn = IntPtr.Zero,
            lParam = IntPtr.Zero,
            iImage = 0
        };

        try
        {
            var pidl = SHBrowseForFolder(ref bi);
            if (pidl == IntPtr.Zero) return null;

            var pathPtr = Marshal.AllocHGlobal(260 * 2);
            try
            {
                if (!SHGetPathFromIDList(pidl, pathPtr))
                    return null;

                return Marshal.PtrToStringUni(pathPtr);
            }
            finally
            {
                CoTaskMemFree(pathPtr);
                CoTaskMemFree(pidl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(bi.pszDisplayName);
        }
    }
}
