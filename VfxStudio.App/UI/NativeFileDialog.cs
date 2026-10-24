using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VfxStudio.App.UI;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct OPENFILENAME
{
    public int lStructSize;
    public IntPtr hwndOwner;
    public IntPtr hInstance;
    public string? lpstrFilter;
    public IntPtr lpstrCustomFilter;
    public int nMaxCustFilter;
    public int nFilterIndex;
    public IntPtr lpstrFile;
    public int nMaxFile;
    public IntPtr lpstrFileTitle;
    public int nMaxFileTitle;
    public string? lpstrInitialDir;
    public string? lpstrTitle;
    public int Flags;
    public short nFileOffset;
    public short nFileExtension;
    public string? lpstrDefExt;
    public IntPtr lCustData;
    public IntPtr lpfnHook;
    public string? lpTemplateName;
    public IntPtr pvReserved;
    public int dwReserved;
    public int FlagsEx;
}

/// <summary>
/// Native Windows File Dialog wrapper using comdlg32.dll (GetOpenFileNameW / GetSaveFileNameW).
/// Zero third-party dependencies, 100% native Explorer dialog.
/// </summary>
public static class NativeFileDialog
{
    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_OVERWRITEPROMPT = 0x00000002;

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

    /// <summary>
    /// Opens a native Windows Open File Dialog.
    /// </summary>
    /// <param name="title">Dialog Window Title</param>
    /// <param name="filter">Pipe-separated filter string, e.g. "MHWilds 动作文件 (*.motlist*)|*.motlist*|所有文件 (*.*)|*.*"</param>
    /// <param name="initialDir">Default directory to browse</param>
    /// <returns>Selected full file path, or null if cancelled</returns>
    public static string? OpenFile(string title, string filter, string? initialDir = null)
    {
        var ofn = new OPENFILENAME();
        ofn.lStructSize = Marshal.SizeOf<OPENFILENAME>();
        ofn.lpstrTitle = title;
        ofn.lpstrFilter = filter.Replace('|', '\0') + "\0\0";
        ofn.nFilterIndex = 1;
        ofn.lpstrInitialDir = !string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir) ? initialDir : null;
        ofn.Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;

        const int maxFile = 2048;
        ofn.lpstrFile = Marshal.AllocHGlobal(maxFile * sizeof(char));
        ofn.nMaxFile = maxFile;

        unsafe
        {
            byte* p = (byte*)ofn.lpstrFile;
            for (int i = 0; i < maxFile * sizeof(char); i++) p[i] = 0;
        }

        try
        {
            if (GetOpenFileNameW(ref ofn))
            {
                return Marshal.PtrToStringUni(ofn.lpstrFile);
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NativeFileDialog] GetOpenFileNameW error: {ex.Message}");
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(ofn.lpstrFile);
        }
    }

    /// <summary>
    /// Opens a native Windows Save File Dialog.
    /// </summary>
    public static string? SaveFile(string title, string filter, string? defaultExt = null, string? initialDir = null)
    {
        var ofn = new OPENFILENAME();
        ofn.lStructSize = Marshal.SizeOf<OPENFILENAME>();
        ofn.lpstrTitle = title;
        ofn.lpstrFilter = filter.Replace('|', '\0') + "\0\0";
        ofn.nFilterIndex = 1;
        ofn.lpstrDefExt = defaultExt;
        ofn.lpstrInitialDir = !string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir) ? initialDir : null;
        ofn.Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR;

        const int maxFile = 2048;
        ofn.lpstrFile = Marshal.AllocHGlobal(maxFile * sizeof(char));
        ofn.nMaxFile = maxFile;

        unsafe
        {
            byte* p = (byte*)ofn.lpstrFile;
            for (int i = 0; i < maxFile * sizeof(char); i++) p[i] = 0;
        }

        try
        {
            if (GetSaveFileNameW(ref ofn))
            {
                return Marshal.PtrToStringUni(ofn.lpstrFile);
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NativeFileDialog] GetSaveFileNameW error: {ex.Message}");
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(ofn.lpstrFile);
        }
    }
}
