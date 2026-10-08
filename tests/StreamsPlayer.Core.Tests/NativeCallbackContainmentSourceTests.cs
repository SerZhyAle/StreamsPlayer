using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0166, enforced over the App's own sources (read as text - see <see cref="AppSourceFile"/>): the callbacks
/// that run on a thread nothing guards contain their own faults, and the native objects of a capture are
/// acquired inside the guard that releases them. The App has no test project of its own, so these gates are what
/// stop the next edit from quietly undoing the containment.
/// </summary>
public sealed class NativeCallbackContainmentSourceTests
{
    private static AppSourceFile Source(string name) =>
        AppSourceFile.LoadAll(name).Single();

    /// <summary>The masked text of the first member called <paramref name="name"/>, from its declaration to its closing brace.</summary>
    private static string Body(AppSourceFile source, string name)
    {
        var declaration = Regex.Match(source.Masked, $@"\b(void|Task|Task<[^>]+>)\s+{Regex.Escape(name)}\s*\(");
        Assert.True(declaration.Success, $"{source.Name}: no member named {name}.");

        var open = source.Masked.IndexOf('{', declaration.Index);
        Assert.True(open >= 0, $"{source.Name}: {name} has no block body.");

        var depth = 0;
        for (var position = open; position < source.Masked.Length; position++)
        {
            depth += source.Masked[position] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source.Masked[declaration.Index..(position + 1)];
            }
        }

        throw new InvalidOperationException($"{source.Name}: unbalanced braces in {name}.");
    }

    [Fact]
    public void TheSnapshotCallbackContainsEveryFault()
    {
        var body = Body(Source("LibVlcVideoBackend.cs"), "MediaPlayer_SnapshotTaken");

        Assert.Contains("catch (Exception", body);
        Assert.Contains("HandlerBoundary.Report", body);
    }

    [Fact]
    public void ThePowerCallbackContainsEveryFaultNotOnlyComErrors()
    {
        // The handler is expression-bodied, so its first block is the lambda it posts to the dispatcher - the
        // frame nothing guards, which is where the broad catch has to sit.
        var body = Body(Source("BackdropEnvironment.cs"), "PowerManager_Changed");

        Assert.Contains("catch (Exception", body);
        Assert.Contains("HandlerBoundary.Report", body);
        Assert.DoesNotContain("catch (COMException)", body);
    }

    [Fact]
    public void ThePreviewWorkerNeverEndsInAFault()
    {
        var source = Source("GridPreviewCoordinator.cs");
        var worker = Body(source, "RunWorkerAsync");
        var stop = Body(source, "StopSessionAsync");

        Assert.Contains("catch (Exception", worker);
        Assert.Contains("PREVIEW WORKER FAULT", source.Text);
        Assert.Contains("catch (Exception", stop);
    }

    [Fact]
    public void TheCaptureAcquiresEveryNativeObjectInsideTheGuardThatReleasesIt()
    {
        // CaptureAsync is the hold-counting wrapper around the disposal of the shared engine (S4-1); the capture
        // proper, and the guard these native objects live in, is CaptureCoreAsync.
        var body = Body(Source("VideoFrameCaptureService.cs"), "CaptureCoreAsync");
        var tryMatch = Regex.Match(body, @"\btry\b");
        var guard = tryMatch.Success ? tryMatch.Index : -1;

        Assert.True(guard >= 0, "CaptureCoreAsync has no try block.");
        foreach (var acquisition in new[] { "GCHandle.Alloc(", "new VlcMediaPlayer(", "new Media(" })
        {
            var at = body.IndexOf(acquisition, StringComparison.Ordinal);
            Assert.True(at > guard, $"{acquisition} is reached before the try whose finally releases it.");
        }
    }

    [Fact]
    public void TheCaptureNamesThePauseWhenTheBudgetIsSpent()
    {
        var source = Source("VideoFrameCaptureService.cs");

        Assert.Contains("PREVIEW CAPTURE PAUSED", source.Text);
        Assert.Contains("_abandoned.IsPaused", source.Masked);
    }

    [Fact]
    public void TheFlyleafTrackListsAreReadOnlyWhereTheEnginePublishesThem()
    {
        var source = Source("FlyleafVideoBackend.cs");
        var publisher = Body(source, "PublishTrackSnapshots");
        var outside = source.Masked.Replace(publisher, string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain("Audio.Streams", outside);
        Assert.DoesNotContain("Subtitles.Streams", outside);
    }
}
