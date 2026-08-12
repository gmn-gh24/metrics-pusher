using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    public class SystemMetricsServiceTests
    {
        [Theory]
        [InlineData("Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz", "Intel Core i7-8700")]
        [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", "12th Gen Intel Core i7-12700K")]
        [InlineData("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", "Intel Xeon E5-2680 v4")]
        [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", "AMD Ryzen 9 7950X3D")]
        [InlineData("AMD Ryzen 7 5700G with Radeon Graphics", "AMD Ryzen 7 5700G")]
        [InlineData("  Intel(R)  Celeron(R)   N4020  ", "Intel Celeron N4020")]
        [InlineData("AMD Ryzen 9 7950X3D 16-Core  Processor", "AMD Ryzen 9 7950X3D")]
        [InlineData("SomeVendor(C) Model X CPU @ 2.00GHz", "SomeVendor Model X")] // (C) strips like (R)/(TM)
        [InlineData("intel(r) core(tm) i5-6500", "intel core i5-6500")] // Marks match case-insensitively
        public void NormalizeCpuName_ShouldStripMarketingNoise_ForVariousCpus(string input, string expected)
        {
            // Act
            var result = SystemMetricsService.NormalizeCpuName(input);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("Intel(R) Ethernet Controller I225-V", "Intel Ethernet Controller I225-V")]
        [InlineData("Killer(TM) Wi-Fi 6E AX1675", "Killer Wi-Fi 6E AX1675")]
        [InlineData("Contoso(C) 10G NIC", "Contoso 10G NIC")]
        [InlineData("realtek(r) pcie gbe family controller", "realtek pcie gbe family controller")]
        [InlineData("Realtek PCIe 5GbE Family Controller", "Realtek PCIe 5GbE Family Controller")] // No marks: unchanged
        [InlineData("Intel(R)   Ethernet(R)  Connection", "Intel Ethernet Connection")] // Whitespace collapses
        public void StripTrademarkMarks_ShouldStripMarksAndCollapseWhitespace_ForAdapterNames(string input, string expected)
        {
            // Act
            var result = SystemMetricsService.StripTrademarkMarks(input);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("(R)(TM)(C)")]
        public void StripTrademarkMarks_ShouldReturnNull_WhenNothingSurvives(string? input)
        {
            // Act
            var result = SystemMetricsService.StripTrademarkMarks(input);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void StripTrademarkMarks_ShouldNotApplyCpuSpecificRules_ToAdapterNames()
        {
            // Arrange - the shared rule is marks-and-whitespace ONLY. The CPU-specific
            // patterns ("CPU", clock suffixes, "N-Core Processor") must not leak into
            // adapter names, where those tokens can be legitimate model text.
            const string name = "Broadcom NetXtreme CPU Offload Adapter @ 2.5GHz";

            // Act
            var result = SystemMetricsService.StripTrademarkMarks(name);

            // Assert
            Assert.Equal(name, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("CPU")]
        public void NormalizeCpuName_ShouldReturnNull_WhenNothingSurvivesCleaning(string? input)
        {
            // Act
            var result = SystemMetricsService.NormalizeCpuName(input);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void GetSystemMetrics_ShouldReturnNonNull_WhenCalled()
        {
            // Act
            var metrics = SystemMetricsService.GetSystemMetrics();

            // Assert
            Assert.NotNull(metrics);
        }

        [Fact]
        public void GetSystemMetrics_ShouldReturnCpuName_WhenCalled()
        {
            // Act - any Windows box exposes ProcessorNameString in the registry
            var metrics = SystemMetricsService.GetSystemMetrics();

            // Assert
            Assert.False(string.IsNullOrWhiteSpace(metrics.CpuName));
        }

        [Fact]
        public void GetSystemMetrics_ShouldReturnPlausibleRamAndDisk_WhenCalled()
        {
            // Act - RAM and disk reads have no warm-up; they must succeed on any Windows box
            var metrics = SystemMetricsService.GetSystemMetrics();

            // Assert
            Assert.NotNull(metrics.RamTotalMB);
            Assert.NotNull(metrics.RamUsedMB);
            Assert.True(metrics.RamTotalMB > 0);
            Assert.InRange(metrics.RamUsedMB.Value, 1, metrics.RamTotalMB.Value);
            Assert.NotNull(metrics.DiskTotalGB);
            Assert.NotNull(metrics.DiskFreeGB);
            Assert.True(metrics.DiskTotalGB > 0);
            Assert.InRange(metrics.DiskFreeGB.Value, 0, metrics.DiskTotalGB.Value);
        }

        [Theory]
        [InlineData("22631", "23H2", "Windows 10 Pro", "11 23H2")] // ProductName lies on Win11 - build wins
        [InlineData("19045", "22H2", "Windows 10 Pro", "10 22H2")]
        [InlineData("26100", null, "Windows 11 Pro", "11 26100")]
        [InlineData("20348", "21H2", "Windows Server 2022 Standard", "Srv 21H2")] // Server 2022 shares the 10/11 build range
        [InlineData("26100", "24H2", "Windows Server 2025 Datacenter", "Srv 24H2")]
        [InlineData("22631", "23H2", null, "11 23H2")]
        [InlineData(null, "23H2", "Windows 10 Pro", null)]
        [InlineData("abc", "23H2", "Windows 10 Pro", null)]
        public void FormatWindowsVersion_ShouldReturnExpected_ForVariousRegistryValues(string? currentBuild, string? displayVersion, string? productName, string? expected)
        {
            // Act
            var result = SystemMetricsService.FormatWindowsVersion(currentBuild, displayVersion, productName);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(0, 0)]  // GOOD -> green
        [InlineData(1, 1)]  // NOTMONITORED -> yellow
        [InlineData(3, 1)]  // SNOOZE -> yellow
        [InlineData(2, 2)]  // POOR -> red
        [InlineData(99, null)]
        public void MapAvHealth_ShouldReturnExpected_ForWscValues(int wscHealth, int? expected)
        {
            // Act
            var result = SystemMetricsService.MapAvHealth(wscHealth);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(0, 1)]  // GOOD -> enabled
        [InlineData(1, 0)]  // NOTMONITORED -> at risk
        [InlineData(3, 0)]  // SNOOZE -> at risk
        [InlineData(2, 0)]  // POOR -> disabled/at risk
        [InlineData(99, null)]
        public void MapFirewallStatus_ShouldReturnExpected_ForWscValues(int wscHealth, int? expected)
        {
            // Act
            var result = SystemMetricsService.MapFirewallStatus(wscHealth);

            // Assert
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(false, false, 0)]
        [InlineData(true, false, 1)]
        [InlineData(false, true, 1)]
        [InlineData(true, true, 1)]
        public void DetectPendingReboot_ShouldReturnExpected_ForKeyPresence(bool cbsKeyExists, bool wuKeyExists, int expected)
        {
            // Act - fake probe keyed off which signal path is being asked about
            var result = SystemMetricsService.DetectPendingReboot(
                key => key.Contains("Component Based Servicing") ? cbsKeyExists : wuKeyExists);

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void GetSystemMetrics_ShouldReturnWindowsVersionAndUptime_WhenCalled()
        {
            // Act - both are machine-independent on any Windows box: the version
            // registry key always exists and uptime is always positive
            var metrics = SystemMetricsService.GetSystemMetrics();

            // Assert
            Assert.False(string.IsNullOrWhiteSpace(metrics.WindowsVersion));
            Assert.NotNull(metrics.UptimeSeconds);
            Assert.True(metrics.UptimeSeconds > 0);
        }

        [Fact]
        public void GetSystemMetrics_ShouldReturnCpuUsageInRange_WhenCalledTwiceWithDelay()
        {
            // Arrange - the first call only establishes the PDH baseline sample;
            // a rate value needs two samples at least ~1 second apart
            _ = SystemMetricsService.GetSystemMetrics();
            Thread.Sleep(1100);

            // Act
            var metrics = SystemMetricsService.GetSystemMetrics();

            // Assert
            Assert.NotNull(metrics.CpuUsagePercent);
            Assert.InRange(metrics.CpuUsagePercent.Value, 0, 100);
        }
    }
}
