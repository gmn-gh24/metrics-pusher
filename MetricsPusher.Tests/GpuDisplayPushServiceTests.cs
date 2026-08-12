using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    public class GpuDisplayPushServiceTests
    {
        [Theory]
        [InlineData("192.168.1.42", "192.168.1.99")]
        [InlineData("10.20.30.40", "10.20.30.99")]
        [InlineData("172.16.5.1", "172.16.5.99")]
        public void DeriveDisplayAddress_ShouldReplaceLastOctetWith99_WhenGivenPrivateIPv4(string local, string expected)
        {
            // Arrange
            var localAddress = IPAddress.Parse(local);

            // Act
            var result = GpuDisplayPushService.DeriveDisplayAddress(localAddress);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(IPAddress.Parse(expected), result);
        }

        [Fact]
        public void DeriveDisplayAddress_ShouldReturnNull_WhenHostOctetAlready99()
        {
            // Arrange - the PC itself holds .99; the display cannot share it,
            // and the app must never target the PC's own address
            var localAddress = IPAddress.Parse("192.168.1.99");

            // Act
            var result = GpuDisplayPushService.DeriveDisplayAddress(localAddress);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DeriveDisplayAddress_ShouldReturnNull_WhenAddressIsNull()
        {
            // Act
            var result = GpuDisplayPushService.DeriveDisplayAddress(null);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void DeriveDisplayAddress_ShouldReturnNull_WhenAddressIsIPv6()
        {
            // Arrange
            var localAddress = IPAddress.Parse("fe80::1");

            // Act
            var result = GpuDisplayPushService.DeriveDisplayAddress(localAddress);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData("203.0.113.42")]  // TEST-NET-3, a routable-looking public address
        [InlineData("8.8.8.8")]
        [InlineData("172.15.0.1")]    // Just below the RFC 1918 /12
        [InlineData("172.32.0.1")]    // Just above it
        [InlineData("100.63.0.1")]    // Just below the CGNAT /10
        [InlineData("100.128.0.1")]   // Just above it
        [InlineData("192.167.0.1")]   // Adjacent to 192.168/16
        [InlineData("11.0.0.1")]      // Adjacent to 10/8
        public void DeriveDisplayAddress_ShouldReturnNull_WhenAddressIsNotOnAPrivateNetwork(string local)
        {
            // Arrange - the push is cleartext and unauthenticated and its destination is
            // derived, not configured. On a public address that would mean streaming this
            // machine's security posture to a stranger, so no address is derived at all.
            var localAddress = IPAddress.Parse(local);

            // Act
            var result = GpuDisplayPushService.DeriveDisplayAddress(localAddress);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData("10.0.0.1")]
        [InlineData("10.255.255.254")]
        [InlineData("172.16.0.1")]
        [InlineData("172.31.255.254")]
        [InlineData("192.168.0.1")]
        [InlineData("100.64.0.1")]     // CGNAT
        [InlineData("100.127.255.254")]
        [InlineData("169.254.10.5")]   // Link-local: no DHCP, but still a real local segment
        public void DeriveDisplayAddress_ShouldDerive_AcrossEveryPrivateRangeBoundary(string local)
        {
            // Arrange
            var localAddress = IPAddress.Parse(local);

            // Act
            var result = GpuDisplayPushService.DeriveDisplayAddress(localAddress);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(Constants.DisplayHostOctet, result!.GetAddressBytes()[3]);
        }

        [Fact]
        public void BuildPayloadJson_ShouldIncludeAllFields_WhenMetricsComplete()
        {
            // Arrange
            var metrics = new GpuMetrics
            {
                Name = "NVIDIA GeForce RTX 4070",
                Temperature = 62.5f,
                UsagePercent = 45,
                VramUsedMB = 3821,
                VramTotalMB = 12282,
                FanSpeedPercent = 38,
                PowerPercent = 87,
                PowerWatts = 174,
                PowerLimitWatts = 200, // 174 / 200 = 87 % - matches the power fixture exactly
                CoreClockMHz = 2610,
                MemoryClockMHz = 10501,
            };
            var systemMetrics = new SystemMetrics
            {
                CpuName = "Intel Core i9-14900K",
                CpuUsagePercent = 31,
                CpuTemperature = 71.5f,
                CpuTemperatureSource = CpuTemperatureSource.IntelPackageMsr,
                CpuPowerWatts = 125,
                CpuPowerLimitWatts = 253,
                NvmeTemperature = 42.5f,
                RamUsedMB = 18432,
                RamTotalMB = 65536,
                DiskFreeGB = 512,
                DiskTotalGB = 1863,
                WindowsVersion = "11 23H2",
                AntivirusHealth = 0,
                RebootPending = 0,
                FirewallEnabled = 1,
                UptimeSeconds = 345600,
            };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, systemMetrics, "TEST-HOST-001");

            // Assert - exact string pins the wire key order and number formatting
            Assert.Equal(
                "{\"v\":1,\"gpu\":\"NVIDIA GeForce RTX 4070\",\"host\":\"TEST-HOST-001\",\"temp\":62.5,\"load\":45,\"vramUsed\":3821,\"vramTotal\":12282,\"fan\":38,\"power\":87,\"watts\":174,\"limitW\":200,\"clock\":2610,\"vramClock\":10501,\"cpu\":\"Intel Core i9-14900K\",\"cpuLoad\":31,\"cpuTemp\":71.5,\"cpuWatts\":125,\"cpuLimitW\":253,\"nvmeTemp\":42.5,\"ramUsed\":18432,\"ramTotal\":65536,\"diskFree\":512,\"diskTotal\":1863,\"win\":\"11 23H2\",\"av\":0,\"reboot\":0,\"fw\":1,\"up\":345600}",
                json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldOmitNullFields_WhenSomeMetricsMissing()
        {
            // Arrange - fan, VRAM, CPU usage and disk unavailable this tick
            var metrics = new GpuMetrics
            {
                Name = "NVIDIA GeForce RTX 4070",
                Temperature = 71f,
                UsagePercent = 99,
            };
            var systemMetrics = new SystemMetrics
            {
                CpuName = "AMD Ryzen 9 7950X3D",
                RamUsedMB = 9216,
                RamTotalMB = 32768,
            };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, systemMetrics, "TEST-HOST-001");

            // Assert
            Assert.NotNull(json);
            Assert.Contains("\"host\":\"TEST-HOST-001\"", json);
            Assert.Contains("\"temp\":71", json);
            Assert.Contains("\"load\":99", json);
            Assert.Contains("\"cpu\":\"AMD Ryzen 9 7950X3D\"", json);
            Assert.Contains("\"ramUsed\":9216", json);
            Assert.DoesNotContain("\"fan\"", json);
            Assert.DoesNotContain("\"vramUsed\"", json);
            Assert.DoesNotContain("\"vramTotal\"", json);
            Assert.DoesNotContain("\"cpuLoad\"", json);
            Assert.DoesNotContain("\"diskFree\"", json);
            Assert.DoesNotContain("\"diskTotal\"", json);
            Assert.DoesNotContain("\"power\"", json);
            Assert.DoesNotContain("\"watts\"", json);
            Assert.DoesNotContain("\"limitW\"", json);
            Assert.DoesNotContain("\"clock\"", json);
            Assert.DoesNotContain("\"vramClock\"", json);
            Assert.DoesNotContain("\"cpuTemp\"", json);
            Assert.DoesNotContain("\"cpuWatts\"", json);
            Assert.DoesNotContain("\"cpuLimitW\"", json);
            Assert.DoesNotContain("\"nvmeTemp\"", json);
            Assert.DoesNotContain("\"win\"", json);
            Assert.DoesNotContain("\"av\"", json);
            Assert.DoesNotContain("\"reboot\"", json);
            Assert.DoesNotContain("\"fw\"", json);
            Assert.DoesNotContain("\"up\"", json);
        }

        [Theory]
        [InlineData((int)CpuTemperatureSource.IntelPackageMsr)]
        [InlineData((int)CpuTemperatureSource.AmdTctlSmn)]
        public void BuildPayloadJson_ShouldIncludeCpuTemp_WhenSourceIsCpuDie(int sourceValue)
        {
            // Arrange
            var systemMetrics = new SystemMetrics
            {
                CpuTemperature = 72.5f,
                CpuTemperatureSource = (CpuTemperatureSource)sourceValue,
            };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(new GpuMetrics(), systemMetrics, null);

            // Assert
            Assert.Equal("{\"v\":1,\"cpuTemp\":72.5}", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldOmitCpuTemp_WhenSourceIsAcpiThermalZone()
        {
            // Arrange - ACPI is a motherboard zone, not a CPU die sensor. A GPU reading
            // keeps the payload alive so this test can distinguish omission from suppression.
            var metrics = new GpuMetrics { Temperature = 60f };
            var systemMetrics = new SystemMetrics
            {
                CpuTemperature = 45f,
                CpuTemperatureSource = CpuTemperatureSource.AcpiThermalZone,
            };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, systemMetrics, null);

            // Assert
            Assert.Equal("{\"v\":1,\"temp\":60}", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldIncludeCpuPowerAndNvmeTemperatureIndependently()
        {
            // Arrange
            var systemMetrics = new SystemMetrics
            {
                CpuPowerWatts = 125,
                CpuPowerLimitWatts = 253,
                NvmeTemperature = 42.5f,
            };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(new GpuMetrics(), systemMetrics, null);

            // Assert
            Assert.Equal("{\"v\":1,\"cpuWatts\":125,\"cpuLimitW\":253,\"nvmeTemp\":42.5}", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldReturnNull_WhenOnlyCpuLimitWIsPresent()
        {
            // Arrange - an enforced limit is ambient, like the GPU limitW field.
            var systemMetrics = new SystemMetrics { CpuPowerLimitWatts = 253 };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(new GpuMetrics(), systemMetrics, null);

            // Assert
            Assert.Null(json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldOmitCpuLimitW_WhenRoundedValueIsZero()
        {
            // Arrange - a live GPU value keeps the payload alive so the limit omission
            // is visible. Zero is not a meaningful enforced limit.
            var metrics = new GpuMetrics { Temperature = 60f };
            var systemMetrics = new SystemMetrics { CpuPowerLimitWatts = 0 };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, systemMetrics, null);

            // Assert
            Assert.Equal("{\"v\":1,\"temp\":60}", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldReturnNull_WhenAllMetricsNull()
        {
            // Arrange
            var metrics = new GpuMetrics();

            // Act - a hostname alone is identity, not live data; still no datagram
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), "TEST-HOST-001");

            // Assert
            Assert.Null(json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldReturnNull_WhenOnlyNameIsSet()
        {
            // Arrange - GPU and CPU names alone carry no live data; the display
            // should keep its last reading instead of receiving an empty update
            var metrics = new GpuMetrics { Name = "NVIDIA GeForce RTX 4070" };
            var systemMetrics = new SystemMetrics { CpuName = "Intel Core i9-14900K" };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, systemMetrics, "TEST-HOST-001");

            // Assert
            Assert.Null(json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldProduceJson_WhenOnlySystemMetricsPresent()
        {
            // Arrange - GPU read failed this tick, but system metrics are live
            var metrics = new GpuMetrics();
            var systemMetrics = new SystemMetrics { CpuUsagePercent = 42 };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, systemMetrics, "TEST-HOST-001");

            // Assert
            Assert.NotNull(json);
            Assert.Contains("\"cpuLoad\":42", json);
            Assert.DoesNotContain("\"temp\"", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldProduceJson_WhenOnlyPowerAndClockPresent()
        {
            // Arrange - the new GPU fields are live metrics and must defeat suppression
            var metrics = new GpuMetrics { PowerPercent = 55, CoreClockMHz = 2100 };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), "TEST-HOST-001");

            // Assert
            Assert.NotNull(json);
            Assert.Contains("\"power\":55", json);
            Assert.Contains("\"clock\":2100", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldProduceJson_WhenOnlyWattsPresent()
        {
            // Arrange - watts is a live GPU metric and must defeat suppression on its
            // own. It can legitimately arrive without its percentage: the two share one
            // reading, but the percentage also needs an enforced limit.
            var metrics = new GpuMetrics { PowerWatts = 320 };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), "TEST-HOST-001");

            // Assert
            Assert.NotNull(json);
            Assert.Contains("\"watts\":320", json);
            Assert.DoesNotContain("\"power\"", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldProduceJson_WhenOnlyVramClockPresent()
        {
            // Arrange - vramClock is a live GPU metric like the other clocks, so it must
            // defeat suppression on its own rather than riding along as an ambient field
            var metrics = new GpuMetrics { MemoryClockMHz = 10501 };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), "TEST-HOST-001");

            // Assert
            Assert.NotNull(json);
            Assert.Contains("\"vramClock\":10501", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldReturnNull_WhenOnlyLimitWIsPresent()
        {
            // The enforced limit is acquire-time state, practically always present on
            // the NVML backend for the whole session; counting it as live would make
            // the suppression guard dead code there - exactly the reasoning that
            // keeps the names ambient
            var metrics = new GpuMetrics { PowerLimitWatts = 480 };
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), "TEST-HOST-001");
            Assert.Null(json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldCarryLimitW_WhenBothPowerFieldsAreAbsent()
        {
            // Reachable: the limit latched at acquire time while this sweep's draw
            // read failed, so power and watts are gone while limitW stands. limitW is
            // ambient, so a live metric (temp) is what makes the datagram sendable.
            var metrics = new GpuMetrics { Temperature = 65f, PowerLimitWatts = 480 };
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), "TEST-HOST-001");
            Assert.NotNull(json);
            Assert.Contains("\"limitW\":480", json);
            Assert.DoesNotContain("\"power\"", json);
            Assert.DoesNotContain("\"watts\"", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldOmitLimitWWhileWattsSurvives_WhenTheAcquireTimeLimitReadFailed()
        {
            // The existing section 5 power-session case: the enforced-limit read
            // failed at acquire time, so limitW (and power, which needs the
            // denominator) is absent while watts - which needs none - keeps arriving
            var metrics = new GpuMetrics { PowerWatts = 320 };
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), "TEST-HOST-001");
            Assert.NotNull(json);
            Assert.Contains("\"watts\":320", json);
            Assert.DoesNotContain("\"limitW\"", json);
            Assert.DoesNotContain("\"power\"", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldReturnNull_WhenOnlyWindowsVersionAndUptimeSet()
        {
            // Arrange - win is static identity and up is always available; neither
            // carries a health signal, so alone they must not trigger a datagram
            var systemMetrics = new SystemMetrics { WindowsVersion = "11 23H2", UptimeSeconds = 12345 };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(new GpuMetrics(), systemMetrics, "TEST-HOST-001");

            // Assert
            Assert.Null(json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldReturnNull_WhenOnlyOsHealthPresent()
        {
            // Arrange - av/reboot/fw come from a slow cache that is practically always
            // populated; counting them as live would make suppression dead code and
            // blank the display with a names-only payload when every sensor fails
            var systemMetrics = new SystemMetrics { AntivirusHealth = 2, RebootPending = 1, FirewallEnabled = 0 };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(new GpuMetrics(), systemMetrics, "TEST-HOST-001");

            // Assert
            Assert.Null(json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldIncludeOsHealth_WhenAnyLiveMetricPresent()
        {
            // Arrange - av/reboot/fw ride along whenever a real metric makes the
            // datagram worth sending
            var systemMetrics = new SystemMetrics { CpuUsagePercent = 12, AntivirusHealth = 2, RebootPending = 1, FirewallEnabled = 0 };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(new GpuMetrics(), systemMetrics, "TEST-HOST-001");

            // Assert
            Assert.NotNull(json);
            Assert.Contains("\"av\":2", json);
            Assert.Contains("\"reboot\":1", json);
            Assert.Contains("\"fw\":0", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldTruncateWindowsVersion_WhenLongerThanCap()
        {
            // Arrange
            int cap = GpuDisplayPushService.MaxOsVersionLength;
            var systemMetrics = new SystemMetrics
            {
                WindowsVersion = new string('W', cap + 20),
                CpuUsagePercent = 5, // A live metric so the datagram is sent at all
            };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(new GpuMetrics(), systemMetrics, "TEST-HOST-001");

            // Assert
            Assert.NotNull(json);
            Assert.Contains($"\"win\":\"{new string('W', cap)}\"", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldProduceJson_WhenOnlyGpuMetricsPresent()
        {
            // Arrange - system metrics all unavailable; GPU data alone must still send
            var metrics = new GpuMetrics { Temperature = 65f };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), "TEST-HOST-001");

            // Assert
            Assert.NotNull(json);
            Assert.Contains("\"temp\":65", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldIncludeVersionField_Always()
        {
            // Arrange
            var metrics = new GpuMetrics { Temperature = 50f };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), "TEST-HOST-001");

            // Assert
            Assert.NotNull(json);
            Assert.StartsWith("{\"v\":1,", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldOmitHost_WhenHostNameIsNull()
        {
            // Arrange
            var metrics = new GpuMetrics { Temperature = 50f };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), null);

            // Assert
            Assert.NotNull(json);
            Assert.DoesNotContain("\"host\"", json);
        }

        [Fact]
        public void BuildPayloadJson_ShouldUseInvariantDecimalSeparator_WhenCultureUsesComma()
        {
            // Arrange
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var metrics = new GpuMetrics { Temperature = 62.5f };

                // Act
                var json = GpuDisplayPushService.BuildPayloadJson(metrics, new SystemMetrics(), "TEST-HOST-001");

                // Assert
                Assert.NotNull(json);
                Assert.Contains("\"temp\":62.5", json);
                Assert.DoesNotContain("62,5", json);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [Fact]
        public void BuildPayloadJson_ShouldTruncateIdentityStrings_WhenNamesExceedWireLimit()
        {
            // Arrange - names well past the wire contract's identity cap
            int cap = GpuDisplayPushService.MaxIdentityLength;
            var metrics = new GpuMetrics { Name = new string('G', cap + 20), Temperature = 60f };
            var systemMetrics = new SystemMetrics { CpuName = new string('C', cap + 20) };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, systemMetrics, new string('H', cap + 20));

            // Assert
            Assert.NotNull(json);
            Assert.Contains($"\"gpu\":\"{new string('G', cap)}\"", json);
            Assert.Contains($"\"cpu\":\"{new string('C', cap)}\"", json);
            Assert.Contains($"\"host\":\"{new string('H', cap)}\"", json);
        }

        [Fact]
        public void TruncateIdentity_ShouldCapEncodedBytes_WhenValueNeedsJsonEscaping()
        {
            // Arrange - 'é' JSON-escapes to é: 6 encoded bytes per character
            string value = new string('é', 40);

            // Act
            var result = GpuDisplayPushService.TruncateIdentity(value);

            // Assert - 10 chars * 6 bytes = 60 <= 63; an 11th would overflow
            Assert.NotNull(result);
            Assert.Equal(10, result!.Length);
            Assert.True(
                JsonEncodedText.Encode(result).EncodedUtf8Bytes.Length <= GpuDisplayPushService.MaxIdentityLength);
        }

        [Fact]
        public void BuildPayloadJson_ShouldFitDatagramBudget_WhenIdentitiesAreNonAscii()
        {
            // Arrange - non-ASCII names escape up to 6x; the byte-aware truncation
            // must keep the whole datagram inside the budget regardless
            var metrics = new GpuMetrics
            {
                Name = new string('é', 100),
                Temperature = 60f,
            };
            var systemMetrics = new SystemMetrics
            {
                CpuName = new string('中', 100),
                WindowsVersion = new string('é', 30),
                AntivirusHealth = 0,
                FirewallEnabled = 0,
            };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, systemMetrics, new string('é', 100));

            // Assert
            Assert.NotNull(json);
            Assert.True(Encoding.UTF8.GetByteCount(json) <= GpuDisplayPushService.MaxDatagramBytes);
        }

        [Fact]
        public void BuildPayloadJson_ShouldFitDatagramBudget_WhenEveryFieldIsAtItsWorstCase()
        {
            // Arrange - the per-field identity cap and the whole-datagram budget are
            // separate limits, so this pins them together: three maxed-out identity
            // strings alongside the widest numbers plausible hardware can report.
            var metrics = new GpuMetrics
            {
                Name = new string('G', GpuDisplayPushService.MaxIdentityLength),
                // NOT the widest formatting the validator allows - 149.12344f is inside
                // MaxValidTemperature and formats to 9 characters, 3 more than this one's
                // 6 (shortest-round-trip float formatting, invariant culture).
                // It is the widest REAL one: both backends report whole degrees (NVML
                // returns an integer, NVAPI's sensors are integral), so no datagram this
                // sender can actually produce is wider than the fixture. That deliberate
                // ~3-byte gap is load-bearing at 591/591 - a future field must not lean on
                // it, and if a backend ever reports fractional degrees the budget has to
                // be recomputed, not just this fixture widened.
                Temperature = 105.75f,
                UsagePercent = 100,
                VramUsedMB = 262144,   // 256 GB of VRAM
                VramTotalMB = 262144,
                FanSpeedPercent = 100,
                PowerPercent = 200,    // Validation cap in GpuMonitorService (3 digits)
                PowerWatts = 1999,     // Widest value under the 2000 validation cap
                PowerLimitWatts = 1999, // Widest value under its own exclusive 2000 cap
                CoreClockMHz = 9999,   // Widest value under the 10000 validation cap
                MemoryClockMHz = 19999, // Widest value under the 20000 validation cap
            };
            var systemMetrics = new SystemMetrics
            {
                CpuName = new string('C', GpuDisplayPushService.MaxIdentityLength),
                CpuUsagePercent = 100,
                CpuTemperature = 149.875f, // Widest real AMD 0.125 C step below the cap
                CpuTemperatureSource = CpuTemperatureSource.AmdTctlSmn,
                CpuPowerWatts = 1000,      // The plausibility cap is inclusive
                CpuPowerLimitWatts = 1000,
                NvmeTemperature = 149.85f, // Widest real tier-2 Kelvin conversion below the cap
                RamUsedMB = 8388608,   // 8 TB of RAM
                RamTotalMB = 8388608,
                DiskFreeGB = 1048576,  // 1 PB system volume
                DiskTotalGB = 1048576,
                WindowsVersion = new string('W', GpuDisplayPushService.MaxOsVersionLength),
                AntivirusHealth = 2,
                RebootPending = 1,
                FirewallEnabled = 0,
                UptimeSeconds = 9999999999, // 10 digits ≈ 317 years
            };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(
                metrics, systemMetrics, new string('H', GpuDisplayPushService.MaxIdentityLength));

            // Assert - UTF-8 bytes, not characters: the budget is a wire limit
            Assert.NotNull(json);
            int byteCount = Encoding.UTF8.GetByteCount(json);
            string overBudgetMessage =
                $"Worst-case payload is {byteCount} bytes, over the {GpuDisplayPushService.MaxDatagramBytes}-byte " +
                "budget. Lower MaxIdentityLength or renegotiate the wire contract.";
            Assert.True(byteCount <= GpuDisplayPushService.MaxDatagramBytes, overBudgetMessage);

            // ... and the exact figure is pinned, because there is no slack left
            // between the worst case and the ceiling: the four CPU/NVMe fields add 69
            // bytes to v5.12.0's 522-byte worst case for 591. The receiver floor remains
            // >= 1024, so 433 bytes separate the ceiling from the floor: the NEXT field
            // is still a sender-side change
            // again - raise MaxDatagramBytes, re-pin this test and update
            // push_metrics.md section 3.3 together - until the total approaches 1024.
            Assert.Equal(591, byteCount);
            Assert.Equal(591, GpuDisplayPushService.MaxDatagramBytes);
        }

        [Fact]
        public void BuildPayloadJson_ShouldStayWellUnderBudget_WhenHardwareIsTypical()
        {
            // Arrange - representative desktop; the class doc promises ~415 bytes here
            // (311 at v5.9.0, +18 for vramClock, +12 for a typical three-digit watts,
            // +13 for a typical three-digit limitW)
            var metrics = new GpuMetrics
            {
                Name = "NVIDIA GeForce RTX 4070",
                Temperature = 62.5f,
                UsagePercent = 45,
                VramUsedMB = 3821,
                VramTotalMB = 12282,
                FanSpeedPercent = 38,
                PowerPercent = 87,
                PowerWatts = 174,
                PowerLimitWatts = 200,
                CoreClockMHz = 2610,
                MemoryClockMHz = 10501,
            };
            var systemMetrics = new SystemMetrics
            {
                CpuName = "Intel Core i9-14900K",
                CpuUsagePercent = 31,
                CpuTemperature = 71.5f,
                CpuTemperatureSource = CpuTemperatureSource.IntelPackageMsr,
                CpuPowerWatts = 125,
                CpuPowerLimitWatts = 253,
                NvmeTemperature = 42.5f,
                RamUsedMB = 18432,
                RamTotalMB = 65536,
                DiskFreeGB = 812,
                DiskTotalGB = 1863,
                WindowsVersion = "11 23H2",
                AntivirusHealth = 0,
                RebootPending = 0,
                FirewallEnabled = 1,
                UptimeSeconds = 345600,
            };

            // Act
            var json = GpuDisplayPushService.BuildPayloadJson(metrics, systemMetrics, "TEST-HOST-001");

            // Assert
            Assert.NotNull(json);
            Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, 425);
        }

        [Fact]
        public void BuildPayloadUtf8_ShouldMatchBuildPayloadJsonBytes_ForPopulatedSparseAndSuppressedPayloads()
        {
            // Arrange - the push loop serializes straight to UTF-8, but every wire
            // guarantee is pinned against BuildPayloadJson. This ties the two paths
            // together forever: same bytes, same suppression decision.
            var populatedGpu = new GpuMetrics
            {
                Name = "NVIDIA GeForce RTX 4070",
                Temperature = 62.5f,
                UsagePercent = 45,
                VramUsedMB = 3821,
                VramTotalMB = 12282,
                FanSpeedPercent = 38,
                PowerPercent = 87,
                PowerWatts = 174,
                PowerLimitWatts = 200,
                CoreClockMHz = 2610,
                MemoryClockMHz = 10501,
            };
            var populatedSystem = new SystemMetrics
            {
                CpuName = "Intel Core i9-14900K",
                CpuUsagePercent = 31,
                RamUsedMB = 18432,
                RamTotalMB = 65536,
                DiskFreeGB = 512,
                DiskTotalGB = 1863,
                WindowsVersion = "11 23H2",
                AntivirusHealth = 0,
                RebootPending = 0,
                FirewallEnabled = 1,
                UptimeSeconds = 345600,
            };

            // Sparse: most fields omitted, plus a non-ASCII name so the encoder path
            // is exercised on both sides
            var sparseGpu = new GpuMetrics { Name = new string('é', 100), Temperature = 71f };
            var sparseSystem = new SystemMetrics { RamUsedMB = 9216, RamTotalMB = 32768 };

            // Act
            var populatedJson = GpuDisplayPushService.BuildPayloadJson(populatedGpu, populatedSystem, "TEST-HOST-001");
            var populatedUtf8 = GpuDisplayPushService.BuildPayloadUtf8(populatedGpu, populatedSystem, "TEST-HOST-001");
            var sparseJson = GpuDisplayPushService.BuildPayloadJson(sparseGpu, sparseSystem, null);
            var sparseUtf8 = GpuDisplayPushService.BuildPayloadUtf8(sparseGpu, sparseSystem, null);
            var suppressedJson = GpuDisplayPushService.BuildPayloadJson(new GpuMetrics(), new SystemMetrics(), "TEST-HOST-001");
            var suppressedUtf8 = GpuDisplayPushService.BuildPayloadUtf8(new GpuMetrics(), new SystemMetrics(), "TEST-HOST-001");

            // Assert
            Assert.NotNull(populatedJson);
            Assert.Equal(Encoding.UTF8.GetBytes(populatedJson!), populatedUtf8);
            Assert.NotNull(sparseJson);
            Assert.Equal(Encoding.UTF8.GetBytes(sparseJson!), sparseUtf8);
            Assert.Null(suppressedJson);
            Assert.Null(suppressedUtf8);
        }

        [Fact]
        public void TruncateIdentity_ShouldReturnSameInstance_WhenAsciiValueIsAtTheCap()
        {
            // Arrange - the allocation-free fast path's happy case: plain ASCII whose
            // char count already equals the encoded byte count
            string value = new string('G', GpuDisplayPushService.MaxIdentityLength);

            // Act
            var result = GpuDisplayPushService.TruncateIdentity(value);

            // Assert
            Assert.Same(value, result);
        }

        [Fact]
        public void TruncateIdentity_ShouldTruncate_WhenAsciiValueUsesEscapeSetCharacters()
        {
            // Arrange - '"' is printable ASCII but the default JSON encoder escapes it,
            // so char count under-reports encoded bytes and the fast path must decline
            int cap = GpuDisplayPushService.MaxIdentityLength;
            string value = new string('"', cap);

            // Act
            var result = GpuDisplayPushService.TruncateIdentity(value);

            // Assert
            Assert.NotNull(result);
            Assert.True(
                result!.Length < value.Length,
                "Escape-set characters must fall through to encoded-byte truncation, not the ASCII fast path.");
            Assert.True(JsonEncodedText.Encode(result).EncodedUtf8Bytes.Length <= cap);
        }

        [Fact]
        public void TruncateIdentity_ShouldMatchEncodeOnlyReference_ForEveryPrintableAsciiCharacter()
        {
            // Arrange - the fast path claims "for these chars, encoded length == char
            // count". Sweep the whole 0x20..0x7E range against the encode-only oracle
            // so a wrong allow-list (e.g. forgetting '&' or '+') cannot slip through.
            int cap = GpuDisplayPushService.MaxIdentityLength;

            for (char c = ' '; c <= '~'; c++)
            {
                string[] values = { c.ToString(), new string(c, cap), new string(c, cap + 5) };
                foreach (string value in values)
                {
                    // Act
                    var result = GpuDisplayPushService.TruncateIdentity(value);

                    // Assert
                    Assert.Equal(EncodeOnlyTruncateIdentity(value, cap), result);
                }
            }
        }

        [Fact]
        public void TruncateIdentity_ShouldMatchEncodeOnlyReference_ForNonAsciiAndControlValues()
        {
            // Arrange - everything the fast path must decline: non-ASCII, control
            // characters, DEL, surrogate pairs, a lone surrogate, and the empty string
            int cap = GpuDisplayPushService.MaxIdentityLength;
            string[] values =
            {
                string.Empty,
                "NVIDIA GeForce RTX 4070",
                new string('é', 40),
                new string('中', 100),
                "GPU\0name",
                "GPU\tname",
                "GPU" + ((char)0x01) + "name", // Control character below 0x20
                new string((char)0x7F, cap + 5), // DEL, above 0x7E
                string.Concat(Enumerable.Repeat("\U0001F600", 40)),
                "tail\uD83D", // Lone high surrogate: the encoder rejects it
                "\uDE00lead", // Lone low surrogate
            };

            foreach (string value in values)
            {
                foreach (int maxLength in new[] { cap, GpuDisplayPushService.MaxOsVersionLength })
                {
                    // Act
                    var result = GpuDisplayPushService.TruncateIdentity(value, maxLength);

                    // Assert
                    Assert.Equal(EncodeOnlyTruncateIdentity(value, maxLength), result);
                }
            }
        }

        [Fact]
        public void NoteOversizeDatagram_ShouldNotWarn_WhenTheDatagramFitsTheBudget()
        {
            // Assert - the ceiling itself is legal (the worst case IS the ceiling since
            // v5.10.0's watts field), so it must not trip the guard
            Assert.False(GpuDisplayPushService.NoteOversizeDatagram(GpuDisplayPushService.MaxDatagramBytes, alreadyWarned: false));
            Assert.False(GpuDisplayPushService.NoteOversizeDatagram(1, alreadyWarned: false));
        }

        [Fact]
        public void NoteOversizeDatagram_ShouldEnterTheWarnedState_OnTheFirstOversizeDatagram()
        {
            // Assert - one byte past the ceiling is the whole point: the budget has no
            // slack left, so a future field that overruns it must announce itself at
            // runtime instead of only failing a unit test nobody ran
            Assert.True(GpuDisplayPushService.NoteOversizeDatagram(GpuDisplayPushService.MaxDatagramBytes + 1, alreadyWarned: false));
        }

        [Fact]
        public void NoteOversizeDatagram_ShouldStayQuiet_WhileTheSameOversizeStreakContinues()
        {
            // Assert - edge-triggered like the send-failure logging beside it: an
            // oversize payload repeats every second, and one line per streak is a
            // diagnostic while one line per tick is a log flood
            Assert.True(GpuDisplayPushService.NoteOversizeDatagram(600, alreadyWarned: true));
        }

        [Fact]
        public void NoteOversizeDatagram_ShouldRearm_WhenDatagramsFitAgain()
        {
            // Arrange - a streak that ends (the GPU name shortened, a field went null)
            bool warned = GpuDisplayPushService.NoteOversizeDatagram(600, alreadyWarned: false);
            Assert.True(warned);

            // Act
            warned = GpuDisplayPushService.NoteOversizeDatagram(400, warned);

            // Assert - back under budget clears the latch, so a LATER overrun is reported
            // again rather than swallowed for the rest of the session
            Assert.False(warned);
            Assert.True(GpuDisplayPushService.NoteOversizeDatagram(600, warned));
        }

        [Fact]
        public async Task RunAsync_ShouldReturnQuickly_WhenTokenAlreadyCancelled()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert - must complete promptly without throwing
            // (RunAsync swallows cancellation) and without touching the network
            await GpuDisplayPushService.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// The pre-fast-path <c>TruncateIdentity</c> algorithm, kept verbatim as the
        /// equivalence oracle: the ASCII fast path is only legal if it is
        /// indistinguishable from always asking the encoder.
        /// </summary>
        private static string? EncodeOnlyTruncateIdentity(string? value, int maxLength)
        {
            if (value == null)
                return null;

            try
            {
                if (JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length <= maxLength)
                    return value;

                int length = Math.Min(value.Length, maxLength);
                while (length > 0)
                {
                    if (char.IsHighSurrogate(value[length - 1]))
                    {
                        length--;
                        continue;
                    }

                    if (JsonEncodedText.Encode(value[..length]).EncodedUtf8Bytes.Length <= maxLength)
                        return value[..length];

                    length--;
                }

                return string.Empty;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
