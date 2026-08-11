namespace MetricsPusher.Tests
{
    public class ConstantsTests
    {
        [Fact]
        public void IsValidTemperature_ShouldReturnTrue_ForNormalTemperature()
        {
            // Arrange
            float temp = 65.5f;

            // Act
            var result = Constants.IsValidTemperature(temp);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void IsValidTemperature_ShouldReturnTrue_ForMinBoundary()
        {
            // Arrange
            float temp = Constants.MinValidTemperature;

            // Act
            var result = Constants.IsValidTemperature(temp);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void IsValidTemperature_ShouldReturnTrue_ForMaxBoundary()
        {
            // Arrange
            float temp = Constants.MaxValidTemperature;

            // Act
            var result = Constants.IsValidTemperature(temp);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void IsValidTemperature_ShouldReturnFalse_ForNaN()
        {
            // Arrange
            float temp = float.NaN;

            // Act
            var result = Constants.IsValidTemperature(temp);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void IsValidTemperature_ShouldReturnFalse_ForPositiveInfinity()
        {
            // Arrange
            float temp = float.PositiveInfinity;

            // Act
            var result = Constants.IsValidTemperature(temp);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void IsValidTemperature_ShouldReturnFalse_ForNegativeInfinity()
        {
            // Arrange
            float temp = float.NegativeInfinity;

            // Act
            var result = Constants.IsValidTemperature(temp);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void IsValidTemperature_ShouldReturnFalse_ForBelowMinimum()
        {
            // Arrange
            float temp = -1f;

            // Act
            var result = Constants.IsValidTemperature(temp);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void IsValidTemperature_ShouldReturnFalse_ForAboveMaximum()
        {
            // Arrange
            float temp = 151f;

            // Act
            var result = Constants.IsValidTemperature(temp);

            // Assert
            Assert.False(result);
        }

        [Theory]
        [InlineData(0f, true)]
        [InlineData(50f, true)]
        [InlineData(100f, true)]
        [InlineData(150f, true)]
        [InlineData(-0.1f, false)]
        [InlineData(150.1f, false)]
        public void IsValidTemperature_ShouldReturnExpected_ForVariousValues(float temp, bool expected)
        {
            // Act
            var result = Constants.IsValidTemperature(temp);

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void SingleInstanceMutexName_ShouldStartWithLocalPrefix()
        {
            // Assert - Local\, not Global\: fast user switching and RDP sessions each
            // get their own tray icon
            Assert.StartsWith("Local\\", Constants.SingleInstanceMutexName);
        }

        [Fact]
        public void MinValidTemperature_ShouldBeZero()
        {
            // Assert
            Assert.Equal(0f, Constants.MinValidTemperature);
        }

        [Fact]
        public void MaxValidTemperature_ShouldBe150()
        {
            // Assert
            Assert.Equal(150f, Constants.MaxValidTemperature);
        }

        [Fact]
        public void DisplayUdpPort_ShouldBeWithinValidPortRange()
        {
            // Assert - non-privileged, valid UDP port
            Assert.InRange(Constants.DisplayUdpPort, 1024, 65535);
        }

        [Fact]
        public void DisplayHostOctet_ShouldBe99()
        {
            // Assert - the display's fixed host octet convention
            Assert.Equal(99, Constants.DisplayHostOctet);
            Assert.InRange(Constants.DisplayHostOctet, 1, 254);
        }

        [Fact]
        public void DisplayDiscoveryConstants_ShouldBePositive()
        {
            // Assert
            Assert.True(Constants.DisplayDiscoveryAttempts > 0);
            Assert.True(Constants.DisplayDiscoveryIntervalSeconds > 0);
            Assert.True(Constants.DisplayPingTimeoutMs > 0);
        }
    }
}
