namespace Restate.Sdk.Tests.Endpoint;

[Collection("Console")]
public class DurableConsoleTests
{
    [Fact]
    public void Log_String_OutputsWhenNotReplaying()
    {
        var console = new DurableConsole(() => false);
        var originalError = Console.Error;

        try
        {
            using var sw = new StringWriter();
            Console.SetError(sw);

            // DurableConsole.Log writes to Console.WriteLine which goes to stdout
            // but let's capture stdout instead
            var originalOut = Console.Out;
            try
            {
                using var outWriter = new StringWriter();
                Console.SetOut(outWriter);

                console.Log("test message");

                var output = outWriter.ToString();
                Assert.Contains("test message", output);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void Log_String_SuppressesWhenReplaying()
    {
        var console = new DurableConsole(() => true);
        var originalOut = Console.Out;

        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            console.Log("should not appear");

            Assert.Empty(sw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Log_Interpolated_SuppressesWhenReplaying()
    {
        var console = new DurableConsole(() => true);
        var originalOut = Console.Out;

        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var value = 42;
            console.Log($"The value is {value}");

            Assert.Empty(sw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Log_InterpolatedWithFormatSpecifier_AppliesFormat()
    {
        // A format specifier (`{value:X4}`) routes through the AppendFormatted<T>(T, string?) handler
        // overload — distinct from the format-less overload the other tests exercise.
        var console = new DurableConsole(() => false);
        var originalOut = Console.Out;

        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var value = 255;
            console.Log($"hex {value:X4}");

            Assert.Contains("hex 00FF", sw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Log_Interpolated_OutputsWhenNotReplaying()
    {
        var console = new DurableConsole(() => false);
        var originalOut = Console.Out;

        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var value = 42;
            console.Log($"The value is {value}");

            var output = sw.ToString();
            Assert.Contains("The value is 42", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Log_ChecksReplayStateOnEachCall()
    {
        var isReplaying = true;
        var console = new DurableConsole(() => isReplaying);
        var originalOut = Console.Out;

        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            console.Log("suppressed");
            Assert.Empty(sw.ToString());

            // Transition out of replay
            isReplaying = false;

            console.Log("visible");
            Assert.Contains("visible", sw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ReplayAwareInterpolatedStringHandler_SkipsFormattingDuringReplay()
    {
        var formatCount = 0;
        var console = new DurableConsole(() => true);

        // The interpolated string handler should not call ToString on the object
        // when replaying, but we can verify no output is produced
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var tracker = new FormattingTracker(() => formatCount++);
            console.Log($"value: {tracker}");

            Assert.Empty(sw.ToString());
            // When replaying, the handler is not valid, so AppendFormatted is never called
            Assert.Equal(0, formatCount);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    /// <summary>
    ///     Helper that tracks when ToString is called.
    /// </summary>
    private sealed class FormattingTracker(Action onFormat)
    {
        public override string ToString()
        {
            onFormat();
            return "formatted";
        }
    }
}