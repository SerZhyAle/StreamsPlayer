using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0109: the cause a user is told for a failed operation is read from the exception's type, never from
/// its text.
/// </summary>
public sealed class FailureCauseClassifierTests
{
    public static TheoryData<Exception, FailureCause> Cases => new()
    {
        { new HttpRequestException("503"), FailureCause.Network },
        { new HttpIOException(HttpRequestError.ResponseEnded), FailureCause.Network },
        { new SocketException((int)SocketError.HostNotFound), FailureCause.Network },
        { new TimeoutException(), FailureCause.Network },
        { new TaskCanceledException(), FailureCause.Network },
        { new InvalidDataException(), FailureCause.DamagedData },
        { new JsonException(), FailureCause.DamagedData },
        { new IOException(), FailureCause.Storage },
        { new FileNotFoundException(), FailureCause.Storage },
        { new UnauthorizedAccessException(), FailureCause.Storage },
        { new InvalidOperationException(), FailureCause.Unknown },
        { new OperationCanceledException(), FailureCause.Unknown }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachTypeHasItsCause(Exception exception, FailureCause expected) =>
        Assert.Equal(expected, FailureCauseClassifier.Classify(exception));

    [Fact]
    public void ADroppedConnectionWrappedInAnIoExceptionIsTheNetworkNotTheDisk() =>
        Assert.Equal(
            FailureCause.Network,
            FailureCauseClassifier.Classify(
                new IOException("read failed", new SocketException((int)SocketError.ConnectionReset))));

    [Fact]
    public void DamagedDataKeepsItsCauseWhateverItWraps() =>
        Assert.Equal(
            FailureCause.DamagedData,
            FailureCauseClassifier.Classify(
                new InvalidDataException("bad UTF-8", new DecoderFallbackException())));

    [Fact]
    public void AnInnerNetworkCauseIsFoundUnderAnUnrelatedOuterType() =>
        Assert.Equal(
            FailureCause.Network,
            FailureCauseClassifier.Classify(
                new InvalidOperationException("wrapped", new HttpRequestException("dns"))));

    [Fact]
    public void AStorageTypeUnderAnUnrelatedOuterTypeIsStillStorage() =>
        Assert.Equal(
            FailureCause.Storage,
            FailureCauseClassifier.Classify(new AggregateException(new UnauthorizedAccessException())));

    [Fact]
    public void ANetworkCauseAnywhereOutranksAStorageTypeOutsideIt() =>
        Assert.Equal(
            FailureCause.Network,
            FailureCauseClassifier.Classify(
                new UnauthorizedAccessException("outer", new TimeoutException())));
}
