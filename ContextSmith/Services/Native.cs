using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ContextSmith.Services;

internal static class Native
{
    /// <summary>重命名子键（Vista+）。.NET 的 RegistryKey 没有提供重命名 API。</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int RegRenameKey(SafeRegistryHandle hKey, string? lpSubKeyName, string lpNewKeyName);

    /// <summary>解析 "@shell32.dll,-8506" 这类间接资源字符串</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    [DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    internal const int SHCNE_ASSOCCHANGED = 0x08000000;
    internal const uint SHCNF_IDLIST = 0x0000;
}
