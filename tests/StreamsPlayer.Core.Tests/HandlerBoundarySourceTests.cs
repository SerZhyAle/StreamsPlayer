namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0119 requirement 2, enforced over the App's own sources (read as text - see <see cref="AppSourceFile"/>).
/// </summary>
public sealed class HandlerBoundarySourceTests
{
    [Fact]
    public void EveryAsynchronousAppHandlerHasAFailureBoundary()
    {
        var sources = AppSourceFile.LoadAll("*.cs");
        var findings = sources.SelectMany(AsyncHandlerBoundaryGate.Findings).ToArray();

        Assert.True(
            findings.Length == 0,
            "An exception escaping these ends the process. Wrap the body in try/catch (Exception) that calls " +
            "HandlerBoundary.Report, or pass the lambda to HandlerBoundary.Run:" + Environment.NewLine +
            string.Join(Environment.NewLine, findings.Select(finding => $"  {finding.File}:{finding.Line} {finding.What}")));
    }

    [Fact]
    public void TheGateSeesTheAppsHandlers()
    {
        // A gate that finds nothing to check passes on anything; this is what keeps it honest.
        // Two shapes carry the App's handlers: a guarded async void method, and a plain handler that hands its
        // body to HandlerBoundary.Run. Both floors sit below today's counts (45 and 28 at SP-0119).
        var sources = AppSourceFile.LoadAll("*.cs");
        var methods = sources.SelectMany(AsyncHandlerBoundaryGate.AsyncVoidMethods).Count();
        var delegated = sources.Sum(source => source.Invocations("HandlerBoundary.Run").Count);

        Assert.True(methods >= 35, $"Only {methods} async void methods were found.");
        Assert.True(delegated >= 20, $"Only {delegated} HandlerBoundary.Run call sites were found.");
    }

    [Theory]
    [InlineData("""
        class C { async void A(object s, EventArgs e) { await Task.Yield(); } }
        """)]
    [InlineData("""
        class C { async void A(object s, EventArgs e) => await Task.Yield(); }
        """)]
    [InlineData("""
        class C { async void A() { if (x) return; try { await Task.Yield(); } catch (Exception e) { R(e); } } }
        """)]
    [InlineData("""
        class C { async void A() { try { await Task.Yield(); } catch (Exception e) { R(e); } Log(); } }
        """)]
    [InlineData("""
        class C { async void A() { try { await Task.Yield(); } catch (IOException e) { R(e); } } }
        """)]
    [InlineData("""
        class C { async void A() { try { await Task.Yield(); } catch (Exception e) when (e is IOException) { R(e); } } }
        """)]
    [InlineData("""
        class C { void A() { Dispatcher.BeginInvoke(new Action(async () => { await Task.Yield(); })); } }
        """)]
    [InlineData("""
        class C { void A() { button.Click += async (_, _) => await Task.Yield(); } }
        """)]
    [InlineData("""
        class C { void A() { Loaded += new RoutedEventHandler(async (s, e) => await Task.Yield()); } }
        """)]
    [InlineData("""
        class C { void A() { Action run = async () => await Task.Yield(); } }
        """)]
    public void AnUnguardedHandlerIsReported(string code)
    {
        var findings = AsyncHandlerBoundaryGate.Findings(AppSourceFile.Parse("Sample.cs", code));

        Assert.Single(findings);
    }

    [Theory]
    [InlineData("""
        class C { async void A() { try { await Task.Yield(); } catch (Exception exception) { HandlerBoundary.Report(nameof(A), exception); } } }
        """)]
    [InlineData("""
        class C { async void A() { try { await Task.Yield(); } catch (OperationCanceledException) { } catch { } finally { Done(); } } }
        """)]
    [InlineData("""
        class C { void A() { button.Click += (_, _) => HandlerBoundary.Run("A", async () => await Task.Yield()); } }
        """)]
    [InlineData("""
        class C { async Task A() { await Task.Yield(); } void B() { Dispatcher.InvokeAsync(async () => await A()); } }
        """)]
    [InlineData("""
        class C { void A() { var text = "async void X() { }"; } // async void Y() => Z();
        }
        """)]
    public void AGuardedOrNonVoidHandlerPasses(string code)
    {
        var findings = AsyncHandlerBoundaryGate.Findings(AppSourceFile.Parse("Sample.cs", code));

        Assert.Empty(findings);
    }
}
