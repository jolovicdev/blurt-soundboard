using System.Runtime.InteropServices;

namespace Blurt;

/// <summary>Deletes files the way Explorer does, so a removed sound can be restored.</summary>
static class RecycleBin
{
    const uint FoDelete = 3;
    const ushort FofSilent = 0x0004;
    const ushort FofNoConfirmation = 0x0010;
    const ushort FofAllowUndo = 0x0040;
    const ushort FofNoErrorUi = 0x0400;
    const ushort FofWantNukeWarning = 0x4000;

    /// <summary>
    /// Returns false when nothing was deleted. Where Windows cannot recycle the file, for example on
    /// a network share or when it is larger than the Recycle Bin allows, it asks the user before
    /// deleting it for good instead of doing so silently.
    /// </summary>
    public static bool Send(string path, IntPtr owner)
    {
        var operation = new ShFileOpStruct
        {
            hwnd = owner,
            wFunc = FoDelete,
            // The shell reads a list of paths ended by an empty one, so the string needs a second terminator.
            pFrom = path + '\0',
            fFlags = FofAllowUndo | FofNoConfirmation | FofWantNukeWarning | FofSilent | FofNoErrorUi,
        };
        return SHFileOperation(ref operation) == 0 && operation.fAnyOperationsAborted == 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref ShFileOpStruct operation);
}
