using System.Runtime.InteropServices;
using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    /// <summary>
    /// Covers the resolver's decision logic - which names are pinned, and to what path.
    /// The load itself is not exercised: it depends on which drivers the build machine
    /// happens to have, and the decision is the part that carries the security property.
    /// </summary>
    public class SystemLibraryResolverTests
    {
        [Theory]
        [InlineData("nvml.dll")]      // Spelled with the extension by NvmlService
        [InlineData("pdh.dll")]
        [InlineData("wscapi.dll")]
        [InlineData("nvapi64")]       // Spelled WITHOUT it by NvAPIWrapper's import table
        public void IsGuarded_ShouldReturnTrue_ForEveryLibraryTheAppPInvokes(string libraryName)
        {
            Assert.True(SystemLibraryResolver.IsGuarded(libraryName));
        }

        [Theory]
        [InlineData("nvml")]
        [InlineData("NVML.DLL")]
        [InlineData("Pdh")]
        [InlineData("nvapi64.dll")]
        public void IsGuarded_ShouldIgnoreCaseAndTheDllSuffix(string libraryName)
        {
            // The two assemblies spell their imports differently, so matching has to be
            // indifferent to both or one of them silently falls through to default probing.
            Assert.True(SystemLibraryResolver.IsGuarded(libraryName));
        }

        [Theory]
        [InlineData("kernel32.dll")]  // A KnownDLL: the loader never searches for it
        [InlineData("nvapi")]         // 32-bit half of NvAPIWrapper's pair; unreachable on an x64-pinned build
        [InlineData("user32.dll")]
        [InlineData("")]
        [InlineData(".dll")]
        [InlineData("nvml2")]
        [InlineData("notnvml")]
        [InlineData("nvml.dll.dll")]
        public void IsGuarded_ShouldReturnFalse_ForEverythingElse(string libraryName)
        {
            // False means "defer to the runtime", so a near-miss must not be pinned and a
            // pinned name must not be matched loosely.
            Assert.False(SystemLibraryResolver.IsGuarded(libraryName));
        }

        [Theory]
        [InlineData("nvml.dll")]
        [InlineData("nvapi64")]
        public void ResolveSystem32Path_ShouldProduceAnAbsolutePathUnderSystem32(string libraryName)
        {
            // Absolute is the security property, not a convenience: the runtime uses such a
            // path verbatim and performs no probing, which is what closes the hijack.
            string path = SystemLibraryResolver.ResolveSystem32Path(libraryName);

            Assert.True(Path.IsPathFullyQualified(path));
            Assert.Equal(Environment.SystemDirectory, Path.GetDirectoryName(path));
            Assert.Equal(".dll", Path.GetExtension(path));
        }

        [Theory]
        [InlineData("pdh.dll")]
        [InlineData("wscapi.dll")]
        public void ResolveSystem32Path_ShouldProduceAPathThatActuallyLoads(string libraryName)
        {
            // The paths for the two libraries Windows always ships are checked for real: a
            // resolver that pins to a WRONG path does not fail loudly, it silently costs the
            // CPU counter and the antivirus/firewall fields. nvml and nvapi64 are excluded -
            // they exist only where an NVIDIA driver is installed, which no build machine
            // can be assumed to have.
            string path = SystemLibraryResolver.ResolveSystem32Path(libraryName);

            Assert.True(File.Exists(path), $"{path} should exist on any Windows install");
            Assert.True(
                NativeLibrary.TryLoad(path, out IntPtr handle),
                $"{path} should load; the resolver deliberately has no fallback if it does not");

            NativeLibrary.Free(handle);
        }

        [Fact]
        public void ResolveSystem32Path_ShouldNotDoubleTheExtension()
        {
            string withSuffix = SystemLibraryResolver.ResolveSystem32Path("nvml.dll");
            string withoutSuffix = SystemLibraryResolver.ResolveSystem32Path("nvml");

            Assert.Equal(withSuffix, withoutSuffix);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "nvml.dll"), withSuffix);
        }
    }
}
