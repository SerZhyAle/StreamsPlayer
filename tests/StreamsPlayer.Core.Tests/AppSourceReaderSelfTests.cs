namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0057 criterion 6, first half: proves the source reader reads what it claims to.
/// </summary>
/// <remarks>
/// Every case here is a way the gate could quietly under-report. A masker that lost the end of an
/// interpolated string, or a matcher that mistook a declaration for a call, would not fail - it would find
/// fewer call sites and pass, which is indistinguishable from a clean run. So each form the application's
/// sources actually contain is fed in deliberately and asserted to be read correctly.
/// </remarks>
public sealed class AppSourceReaderSelfTests
{
    private static AppSourceFile Parse(string body) =>
        AppSourceFile.Parse("Synthetic.cs", $"class C\n{{\n{body}\n}}\n");

    private static IReadOnlyList<Invocation> Calls(string body) => Parse(body).Invocations("SetStatus");

    private static string[] Keys(AppSourceFile file, Invocation call) =>
        [.. file.LiteralsIn(call.Arguments[0])];

    [Fact]
    public void ACallWithOneLiteralArgumentIsRead()
    {
        var file = Parse("""    void M() { SetStatus("Ready"); }""");
        var call = Assert.Single(file.Invocations("SetStatus"));

        Assert.Single(call.Arguments);
        Assert.Equal(["Ready"], Keys(file, call));
    }

    [Fact]
    public void FormatArgumentsAreCounted()
    {
        var file = Parse("""    void M() { SetStatus("ChannelCount", shown, total); }""");
        var call = Assert.Single(file.Invocations("SetStatus"));

        Assert.Equal(3, call.Arguments.Count);
        Assert.Equal(["ChannelCount"], Keys(file, call));
    }

    [Fact]
    public void ACallWithNoArgumentsHasNone() =>
        Assert.Empty(Assert.Single(Calls("""    void M() { SetStatus(); }""")).Arguments);

    [Fact]
    public void ACommaInsideANestedCallDoesNotSplitAnArgument()
    {
        // The real shape of MainWindow.SleepTimer.cs: a nested ToString whose own arguments carry a comma,
        // and one of them is a string. A naive split reports three arguments and a false failure.
        var call = Assert.Single(Calls(
            """    void M() { SetStatus("SleepTimerSet", when.ToString("t", CultureInfo.CurrentUICulture)); }"""));

        Assert.Equal(2, call.Arguments.Count);
    }

    [Fact]
    public void ACallSpanningSeveralLinesIsRead()
    {
        var call = Assert.Single(Calls("""
                void M()
                {
                    SetStatus(
                        percentKey,
                        (int)(fraction * 100),
                        received / (1024 * 1024),
                        total / (1024 * 1024));
                }
            """));

        Assert.Equal(4, call.Arguments.Count);
    }

    [Theory]
    [InlineData("""    void M() { /* SetStatus("Ready"); */ }""")]
    [InlineData("""    void M() { } // SetStatus("Ready");""")]
    [InlineData("""    void M() { Log(@"SetStatus(""Ready"")"); }""")]
    [InlineData("""    void M() { Log($"SetStatus({key})"); }""")]
    public void ACallThatIsNotCodeIsNotFound(string body) => Assert.Empty(Calls(body));

    [Fact]
    public void ADeclarationIsNotACall()
    {
        // Both of these name SetStatus and are followed by a parenthesis. Only one is a call site.
        var file = Parse("""
                private void SetStatus(string resourceKey, params object?[] arguments) { }
                void M() { SetStatus("Ready"); }
            """);

        var call = Assert.Single(file.Invocations("SetStatus"));
        Assert.Single(call.Arguments);
        Assert.Equal(["Ready"], Keys(file, call));
    }

    [Fact]
    public void ACallPrecededByAKeywordIsStillACall()
    {
        // `return`, `await` and the rest read like a declaration's return type to a scanner that only asks
        // whether the previous token is a word.
        var file = Parse("""    string M() { return Describe(SetStatus("Ready")); }""");

        Assert.Single(file.Invocations("SetStatus"));
    }

    [Fact]
    public void AQualifiedNameIsMatchedThroughItsReceiver()
    {
        var file = Parse("""    string M() => LocalizationService.Format("BitrateValue", kbps);""");
        var call = Assert.Single(file.Invocations("LocalizationService.Format"));

        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal(["BitrateValue"], Keys(file, call));
    }

    [Fact]
    public void AVariableKeyYieldsNoLiteral()
    {
        // The formatting helpers forward whatever they were given. Nothing readable is there, and claiming
        // otherwise would be worse than skipping.
        var file = Parse("""    void M() { SetStatus(resourceKey, arguments); }""");
        var call = Assert.Single(file.Invocations("SetStatus"));

        Assert.Equal(2, call.Arguments.Count);
        Assert.Empty(Keys(file, call));
    }

    [Fact]
    public void AConditionalKeyYieldsBothLiterals()
    {
        // MainWindow.ResumePlayback.cs writes exactly this. Returning a list rather than one value is what
        // gates both branches without the reader knowing what a conditional expression is.
        var file = Parse("""    void M() { SetStatus(resumed > 0 ? "ResumedPlayback" : "ResumeNothingToPlay"); }""");
        var call = Assert.Single(file.Invocations("SetStatus"));

        Assert.Single(call.Arguments);
        Assert.Equal(["ResumedPlayback", "ResumeNothingToPlay"], Keys(file, call));
    }

    [Fact]
    public void AVerbatimStringArgumentIsOneArgument()
    {
        var call = Assert.Single(Calls("""    void M() { SetStatus("K", @"a,b,c"); }"""));

        Assert.Equal(2, call.Arguments.Count);
    }

    [Fact]
    public void AnInterpolationHoleMayHoldItsOwnLiteral()
    {
        // The case that breaks a scanner which stops at the first quote: the literal inside the hole ends
        // the outer string three characters early, and every bracket after it is then counted wrong.
        var file = Parse("""    void M() { SetStatus("K", $"{map["a,b"]} done"); }""");
        var call = Assert.Single(file.Invocations("SetStatus"));

        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal(["K"], Keys(file, call));
        // The nested literal is inside a masked hole, so it is not offered as a key of its own.
        Assert.Empty(file.LiteralsIn(call.Arguments[1]));
    }

    [Fact]
    public void AnEscapedBraceInAnInterpolatedStringDoesNotOpenAHole()
    {
        var call = Assert.Single(Calls("""    void M() { SetStatus("K", $"{{literal}} {value}"); }"""));

        Assert.Equal(2, call.Arguments.Count);
    }

    [Fact]
    public void AnEscapedQuoteDoesNotEndAString()
    {
        var file = Parse("""    void M() { SetStatus("K", "say \"a, b\""); }""");
        var call = Assert.Single(file.Invocations("SetStatus"));

        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal(["K"], Keys(file, call));
        Assert.Equal(["say \"a, b\""], file.LiteralsIn(call.Arguments[1]).ToArray());
    }

    [Fact]
    public void ASlashPairInsideAStringIsNotAComment()
    {
        var file = Parse("""    void M() { SetStatus("K", "http://example.invalid/a,b"); }""");
        var call = Assert.Single(file.Invocations("SetStatus"));

        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal(["http://example.invalid/a,b"], file.LiteralsIn(call.Arguments[1]).ToArray());
    }

    [Fact]
    public void ACharLiteralHoldingAQuoteDoesNotOpenAString()
    {
        var call = Assert.Single(Calls("""    void M() { SetStatus("K", text.Replace('"', ',')); }"""));

        Assert.Equal(2, call.Arguments.Count);
    }

    [Fact]
    public void ASingleLineRawStringIsReadAsALiteral()
    {
        var file = Parse(""""    void M() { SetStatus("""K"""); }"""");
        var call = Assert.Single(file.Invocations("SetStatus"));

        Assert.Single(call.Arguments);
        Assert.Equal(["K"], Keys(file, call));
    }

    [Fact]
    public void AMultiLineRawStringIsNotOfferedAsALiteralButStillEndsCorrectly()
    {
        // Its indentation rules are their own small language and no key is written that way, so the reader
        // declines to decode it. What it must still do is find the end - otherwise everything after it is
        // read as string content.
        var file = Parse(""""
                void M()
                {
                    Log("""
                        a, b
                        """);
                    SetStatus("K", one);
                }
            """");

        var call = Assert.Single(file.Invocations("SetStatus"));
        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal(["K"], Keys(file, call));
    }

    [Fact]
    public void AnUnclosedArgumentListIsReportedRatherThanSkipped()
    {
        // A silent skip is the exact failure mode this ticket exists to close, so the reader refuses.
        var file = AppSourceFile.Parse("Synthetic.cs", """class C { void M() { SetStatus("K", one; } """);

        var failure = Assert.Throws<InvalidOperationException>(() => file.Invocations("SetStatus"));
        Assert.Contains("never closes", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LineNumbersPointAtTheCall()
    {
        var file = Parse("""
                void M()
                {
                    SetStatus("K");
                }
            """);

        // class C = 1, { = 2, "void M()" = 3, "{" = 4, the call = 5.
        Assert.Equal(5, file.LineAt(Assert.Single(file.Invocations("SetStatus")).Offset));
    }

    [Fact]
    public void TheApplicationSourcesAreActuallyLinkedIn()
    {
        // Without this the whole gate would pass on an empty directory.
        var sources = AppSourceFile.LoadAll("*.cs");

        Assert.True(sources.Count >= 50, $"Only {sources.Count} application sources were copied in.");
        Assert.Contains(sources, source => source.Name == "MainWindow.Localization.cs");
        Assert.Contains(sources, source => source.Name == "LocalizationService.cs");
    }
}
