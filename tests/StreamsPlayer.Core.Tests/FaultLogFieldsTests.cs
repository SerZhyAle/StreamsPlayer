namespace StreamsPlayer.Core.Tests;

public sealed class FaultLogFieldsTests
{
    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    [Fact]
    public void DescribesTheTypeTheTextAndTheFrame()
    {
        var fields = FaultLogFields.Of(Thrown(new UnauthorizedAccessException("denied")));

        Assert.Equal("type=UnauthorizedAccessException", fields[0]);
        Assert.Equal("err=denied", fields[1]);
        Assert.StartsWith("at=at ", fields[2]);
        Assert.Contains(nameof(Thrown), fields[2]); // the frame that threw - the first line of the trace
    }

    [Fact]
    public void AnExceptionThatWasNeverThrownHasNoFrame() =>
        Assert.Equal("at=none", FaultLogFields.Of(new InvalidOperationException("x"))[2]);

    [Fact]
    public void AMultiLineMessageStaysOnOneLine()
    {
        var fields = FaultLogFields.Of(new InvalidOperationException("first\r\nsecond\nthird"));

        Assert.DoesNotContain(fields, field => field.Contains('\n') || field.Contains('\r'));
    }

    [Fact]
    public void ALongMessageIsCut()
    {
        var fields = FaultLogFields.Of(new InvalidOperationException(new string('x', 5000)));

        Assert.Equal("err=".Length + FaultLogFields.MaximumMessageLength, fields[1].Length);
    }

    [Fact]
    public void TextOf_IsTheErrFieldsValueAlone()
    {
        // SP-0189: a line that names the fault's kind its own way takes the text alone, cleaned the same way.
        var exception = new InvalidOperationException("first\r\nsecond " + new string('x', 5000));

        Assert.Equal(FaultLogFields.Of(exception)[1], "err=" + FaultLogFields.TextOf(exception));
        Assert.Equal(FaultLogFields.MaximumMessageLength, FaultLogFields.TextOf(exception).Length);
        Assert.StartsWith("first second ", FaultLogFields.TextOf(exception));
    }
}
