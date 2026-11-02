global using ByteScape.Core.Localization;
global using static ByteScape.Core.Localization.Loc;

// the Windows DLLs this code calls are loaded from System32 only, never from the program's folder (a portable exe
// often runs from Downloads, where a planted crypt32.dll or cldapi.dll would otherwise be picked up first)
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
