using System.Reflection;
using System.Runtime.InteropServices;
using NvAPIWrapper;

namespace MetricsPusher.Services
{
    /// <summary>
    /// Pins every native library this app P/Invokes to <c>%WINDIR%\System32</c>, closing the
    /// DLL search-order hijack a bare-name <c>DllImport</c> otherwise opens.
    /// <para>
    /// The hazard: .NET probes <c>NATIVE_DLL_SEARCH_DIRECTORIES</c> BEFORE it ever reaches
    /// the OS loader, and on .NET 8 the single-file host put the executable's own directory
    /// in that list (changed in .NET 10). None of the libraries below is a KnownDLL, so a
    /// file named <c>nvml.dll</c> dropped beside a portable MetricsPusher.exe - in Downloads,
    /// on a stick, in a shared folder - used to win over the real one and run as native code
    /// in a process that talks to the network. An import resolver is consulted before all of
    /// that probing, and an ABSOLUTE path is loaded as-is with no search at all, which is
    /// what actually closes the hole. The assembly-level
    /// <c>DefaultDllImportSearchPaths(System32)</c> in Program.cs is the second layer, for
    /// any import added later that is not named here.
    /// </para>
    /// <para>
    /// A guarded name that cannot be loaded from System32 throws rather than falling back to
    /// default probing, because falling back is precisely the hole. Every caller already
    /// treats that as "this stack is unavailable": NvmlService latches it, SystemMetricsService
    /// disables the counter, and the NVAPI probe returns false - so a machine without an
    /// NVIDIA driver degrades exactly as it did before, just without the search.
    /// </para>
    /// </summary>
    internal static class SystemLibraryResolver
    {
        /// <summary>
        /// The libraries to pin, spelled as the runtime asks for them - which is the name in
        /// the <c>DllImport</c>, extension or not. <c>nvml</c>, <c>pdh</c> and <c>wscapi</c>
        /// come from this assembly; <c>nvapi64</c> comes from NvAPIWrapper's own import table,
        /// which declares it without the extension.
        /// <para>
        /// <c>kernel32</c> is deliberately absent: it is a KnownDLL, so the loader maps the
        /// already-resolved section and never searches for it. <c>nvapi</c> (the 32-bit half
        /// of NvAPIWrapper's pair) is absent for the same reason it can never be requested -
        /// PlatformTarget is pinned x64, and on x64 Windows the 32-bit library does not live
        /// in System32 anyway.
        /// </para>
        /// </summary>
        private static readonly string[] GuardedLibraries = { "nvml", "pdh", "wscapi", "nvapi64" };

        /// <summary>
        /// Registers the resolver for this assembly and for NvAPIWrapper's. Must run before
        /// the first P/Invoke into either - a resolver is only consulted for loads that have
        /// not happened yet - so <c>Program.Main</c> calls it as its very first statement.
        /// Call once; the runtime allows only one resolver per assembly.
        /// </summary>
        internal static void Install()
        {
            NativeLibrary.SetDllImportResolver(typeof(SystemLibraryResolver).Assembly, Resolve);
            NativeLibrary.SetDllImportResolver(typeof(NVIDIA).Assembly, Resolve);
        }

        /// <summary>
        /// Whether <paramref name="libraryName"/> is one this app pins to System32. Matched
        /// with the <c>.dll</c> suffix optional, since the two assemblies spell their imports
        /// differently.
        /// </summary>
        /// <param name="libraryName">The name the runtime is trying to resolve.</param>
        /// <returns>True when the name must come from System32 or not at all.</returns>
        internal static bool IsGuarded(string libraryName)
        {
            string bare = TrimDllSuffix(libraryName);
            foreach (string guarded in GuardedLibraries)
            {
                if (string.Equals(bare, guarded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The absolute System32 path a guarded name resolves to. Absolute is the whole
        /// point: the runtime uses such a path verbatim and performs no probing.
        /// </summary>
        /// <param name="libraryName">A name <see cref="IsGuarded"/> accepted.</param>
        /// <returns>The full path, e.g. <c>C:\Windows\system32\nvml.dll</c>.</returns>
        internal static string ResolveSystem32Path(string libraryName)
        {
            return Path.Combine(Environment.SystemDirectory, TrimDllSuffix(libraryName) + ".dll");
        }

        /// <summary>
        /// The resolver itself: loads guarded names from System32 and declines everything
        /// else, which leaves the runtime's own resolution in place for libraries this app
        /// does not import directly.
        /// </summary>
        /// <param name="libraryName">The name being resolved.</param>
        /// <param name="assembly">The assembly whose import triggered the load.</param>
        /// <param name="searchPath">The declared search path; unused - a guarded name is absolute.</param>
        /// <returns>The loaded handle, or <see cref="IntPtr.Zero"/> to defer to the runtime.</returns>
        /// <exception cref="DllNotFoundException">
        /// A guarded library is not present in System32. Thrown rather than deferred: the
        /// fallback path is the hijack this class exists to prevent.
        /// </exception>
        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!IsGuarded(libraryName))
                return IntPtr.Zero;

            string path = ResolveSystem32Path(libraryName);
            if (NativeLibrary.TryLoad(path, out IntPtr handle))
                return handle;

            throw new DllNotFoundException(
                $"{libraryName} was not loaded from {path}. It is pinned to System32 on purpose; " +
                "MetricsPusher will not fall back to searching other directories for it.");
        }

        /// <summary>
        /// Drops a trailing <c>.dll</c> so the two spellings compare equal.
        /// </summary>
        /// <param name="libraryName">The name to normalize.</param>
        /// <returns>The name without its <c>.dll</c> suffix.</returns>
        private static string TrimDllSuffix(string libraryName)
        {
            const string suffix = ".dll";
            return libraryName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? libraryName[..^suffix.Length]
                : libraryName;
        }
    }
}
