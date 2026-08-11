#pragma warning disable RCS1102 // Make class static - xUnit collection definitions are marker classes and must stay concrete

namespace MetricsPusher.Tests
{
    /// <summary>
    /// xUnit runs test classes in parallel by default, and runs the classes of one
    /// collection serially. This collection holds every class that touches state the
    /// whole process shares, of which there are two kinds:
    /// <list type="number">
    /// <item><description>
    /// <b>The GPU driver stacks</b> - <c>NvmlService</c>'s NVML latch and device
    /// handle, <c>GpuMonitorService</c>'s NVAPI lifecycle, and (since the NVML
    /// backend) each other. Racing them produces false failures on BOTH sides: the
    /// "not initialized" assertions see a live GPU, the live-metric assertions see the
    /// empty metrics a foreign <c>Shutdown</c> left behind.
    /// </description></item>
    /// <item><description>
    /// <b>WinForms</b> - controls and their layout engines are not safe to build or
    /// lay out from several threads at once, whatever thread owns each control. Two
    /// layout-asserting classes running concurrently intermittently measure a
    /// container against a size the other thread's pass left stale, which surfaces as
    /// "bounds exceed client area" on an innocent dialog. Measured before this rule
    /// existed: roughly 1 suite run in 10 red with three such classes, ~3 in 10 with
    /// four.
    /// </description></item>
    /// </list>
    /// <para>
    /// The rule, therefore: <b>a test class that touches NVML/NVAPI, or that
    /// constructs or lays out any WinForms control, must carry
    /// <c>[Collection(ProcessGlobalCollection.Name)]</c></b>. Everything else stays
    /// parallel, which is what keeps the suite at a few seconds instead of the ~10 s a
    /// fully serialized assembly costs.
    /// </para>
    /// </summary>
    [CollectionDefinition(Name)]
    public class ProcessGlobalCollection
    {
        /// <summary>The collection name; every class described above must carry it.</summary>
        internal const string Name = "ProcessGlobal";
    }
}
