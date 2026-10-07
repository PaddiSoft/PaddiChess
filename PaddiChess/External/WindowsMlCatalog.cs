using Microsoft.ML.OnnxRuntime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PaddiXiangqi.External;

/// <summary>Documented WinML C API. Only installed, certified EPs are loaded;
/// first connection never waits for a driver/model download or installs software.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsMlCatalog
{
    private static readonly Lazy<bool> Registered = new(Register);
    internal static void RegisterInstalledProviders() => _ = Registered.Value;

    private static bool Register()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) return false;
        IntPtr catalog = IntPtr.Zero;
        try
        {
            if (WinMLEpCatalogCreate(out catalog) < 0 || catalog == IntPtr.Zero) return false;
            var providers = new List<(IntPtr Handle, string Name)>();
            EnumCallback callback = (handle, pointer, _) =>
            {
                if (pointer == IntPtr.Zero) return 1;
                var info = Marshal.PtrToStructure<EpInfo>(pointer);
                if (info.Certification == 1 && info.ReadyState is 0 or 1 && Marshal.PtrToStringUTF8(info.Name) is { Length: > 0 } name)
                    providers.Add((handle, name));
                return 1;
            };
            if (WinMLEpCatalogEnumProviders(catalog, callback, IntPtr.Zero) < 0) return false;
            GC.KeepAlive(callback);
            foreach (var (handle, name) in providers)
            {
                try
                {
                    if (WinMLEpEnsureReady(handle) < 0 || WinMLEpGetLibraryPathSize(handle, out var length) < 0 || length is 0 or > 32768) continue;
                    var bytes = new byte[(int)length];
                    if (WinMLEpGetLibraryPath(handle, length, bytes, out _) < 0) continue;
                    var path = System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                    if (Path.IsPathFullyQualified(path) && File.Exists(path))
                        OrtEnv.Instance().RegisterExecutionProviderLibrary(name, path);
                }
                catch (Exception ex) when (InferenceModel.IsBackendFailure(ex)) { /* A single incompatible EP cannot disable DirectML. */ }
            }
            return true;
        }
        catch (Exception ex) when (InferenceModel.IsBackendFailure(ex)) { return false; }
        finally { if (catalog != IntPtr.Zero) WinMLEpCatalogRelease(catalog); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EpInfo
    {
        public IntPtr Name, Version, PackageFamilyName, LibraryPath, PackageRootPath;
        public int ReadyState, Certification;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumCallback(IntPtr ep, IntPtr info, IntPtr context);
    private const string Library = "Microsoft.Windows.AI.MachineLearning.dll";
    [DllImport(Library, ExactSpelling = true)] private static extern int WinMLEpCatalogCreate(out IntPtr catalog);
    [DllImport(Library, ExactSpelling = true)] private static extern void WinMLEpCatalogRelease(IntPtr catalog);
    [DllImport(Library, ExactSpelling = true)] private static extern int WinMLEpCatalogEnumProviders(IntPtr catalog, EnumCallback callback, IntPtr context);
    [DllImport(Library, ExactSpelling = true)] private static extern int WinMLEpEnsureReady(IntPtr ep);
    [DllImport(Library, ExactSpelling = true)] private static extern int WinMLEpGetLibraryPathSize(IntPtr ep, out nuint size);
    [DllImport(Library, ExactSpelling = true)] private static extern int WinMLEpGetLibraryPath(IntPtr ep, nuint bufferSize, [Out] byte[] buffer, out nuint bufferUsed);
}
