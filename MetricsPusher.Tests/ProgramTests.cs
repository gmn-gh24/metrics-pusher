using MetricsPusher;

namespace MetricsPusher.Tests
{
    /// <summary>
    /// The elevation refusal's supporting logic. The refusal itself is not exercised here -
    /// it depends on the token the test host happens to hold - but the reading of the UAC
    /// policy decides which of two messages a user sees, and an inverted answer would tell
    /// someone to "relaunch normally" on the one configuration where that cannot work.
    /// </summary>
    public class ProgramTests
    {
        [Fact]
        public void IsUacDisabledValue_ShouldReturnTrue_OnlyForAnExplicitZero()
        {
            Assert.True(Program.IsUacDisabledValue(0));
        }

        [Theory]
        [InlineData(1)]          // The normal, UAC-on value
        [InlineData(2)]
        [InlineData(-1)]
        public void IsUacDisabledValue_ShouldReturnFalse_ForAnyOtherNumber(int enableLua)
        {
            Assert.False(Program.IsUacDisabledValue(enableLua));
        }

        [Theory]
        [InlineData(null)]       // Key or value absent
        [InlineData("0")]        // Written as a string by something that should not have
        [InlineData(0L)]         // QWORD rather than DWORD
        public void IsUacDisabledValue_ShouldReturnFalse_WhenTheValueIsMissingOrNotADword(object? enableLua)
        {
            // "Cannot prove UAC is off" must read as "UAC is on": that keeps the refusal
            // message actionable, and the refusal itself is unaffected either way.
            Assert.False(Program.IsUacDisabledValue(enableLua));
        }
    }
}
