using MetricsPusher.Services;

namespace MetricsPusher.Tests
{
    public class LoggingServiceTests
    {
        [Fact]
        public void Info_ShouldNotThrow_WhenCalled()
        {
            // Act & Assert - Should not throw
            var exception = Record.Exception(() => LoggingService.Info("Test info message"));
            Assert.Null(exception);
        }

        [Fact]
        public void Debug_ShouldNotThrow_WhenCalled()
        {
            // Act & Assert - Should not throw
            var exception = Record.Exception(() => LoggingService.Debug("Test debug message"));
            Assert.Null(exception);
        }

        [Fact]
        public void Warn_ShouldNotThrow_WhenCalled()
        {
            // Act & Assert - Should not throw
            var exception = Record.Exception(() => LoggingService.Warn("Test warning message"));
            Assert.Null(exception);
        }

        [Fact]
        public void Error_ShouldNotThrow_WhenCalledWithMessageOnly()
        {
            // Act & Assert - Should not throw
            var exception = Record.Exception(() => LoggingService.Error("Test error message"));
            Assert.Null(exception);
        }

        [Fact]
        public void Error_ShouldNotThrow_WhenCalledWithException()
        {
            // Arrange
            var testException = new InvalidOperationException("Test exception");

            // Act & Assert - Should not throw
            var exception = Record.Exception(() => LoggingService.Error("Test error with exception", testException));
            Assert.Null(exception);
        }

        [Fact]
        public void Error_ShouldNotThrow_WhenCalledWithNullException()
        {
            // Act & Assert - Should not throw
            var exception = Record.Exception(() => LoggingService.Error("Test error with null exception", null));
            Assert.Null(exception);
        }

        [Fact]
        public void Info_ShouldHandleEmptyMessage()
        {
            // Act & Assert - Should not throw
            var exception = Record.Exception(() => LoggingService.Info(string.Empty));
            Assert.Null(exception);
        }

        [Fact]
        public void Info_ShouldHandleLongMessage()
        {
            // Arrange
            var longMessage = new string('A', 10000);

            // Act & Assert - Should not throw
            var exception = Record.Exception(() => LoggingService.Info(longMessage));
            Assert.Null(exception);
        }

        [Fact]
        public void Info_ShouldHandleSpecialCharacters()
        {
            // Arrange
            var specialMessage = "Test with special chars: <>&\"'\\n\\r\\t\u0000\u001f";

            // Act & Assert - Should not throw
            var exception = Record.Exception(() => LoggingService.Info(specialMessage));
            Assert.Null(exception);
        }

        [Fact]
        public void AllLogLevels_ShouldNotThrow_WhenCalledConcurrently()
        {
            // Arrange
            var tasks = new List<Task>();

            // Act - Call all log methods concurrently
            for (int i = 0; i < 10; i++)
            {
                int index = i;
                tasks.Add(Task.Run(() => LoggingService.Info($"Concurrent info {index}")));
                tasks.Add(Task.Run(() => LoggingService.Debug($"Concurrent debug {index}")));
                tasks.Add(Task.Run(() => LoggingService.Warn($"Concurrent warn {index}")));
                tasks.Add(Task.Run(() => LoggingService.Error($"Concurrent error {index}")));
            }

            // Assert - Should complete without exception
            var exception = Record.Exception(() => Task.WaitAll(tasks.ToArray()));
            Assert.Null(exception);
        }
    }
}
