using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0039 step 5.7: the author's one contact address is the same everywhere it is typed by hand.
/// <para>
/// The address lives in three places that no generator connects: the application's own constant
/// (<c>ProductInfo.AuthorEmail</c>), the written offer of the third-party notices, and the Store listing's
/// support-email field. The site derives its copy from the constant, so it follows by construction; these three
/// do not, and a change of address that reaches two of them leaves a reader writing to a mailbox the author no
/// longer reads - or, for the written offer, a legal promise pointing at nobody. The application source, the
/// notices and the listing come in as linked test data and are read as text, never loaded.
/// </para>
/// </summary>
public sealed partial class ContactAddressTests
{
    // The address the product used before the one-address decision. Not a contact any more: the author's
    // identity in AGENTS.md and the git configuration keep it, which is why only the two published holders
    // below are held against it.
    private const string RetiredAddress = "serzhyale@gmail.com";

    [Fact]
    public void TheApplicationAddressIsReadFromTheSource()
    {
        var address = AuthorEmail(ProductInfoSource());

        Assert.False(string.IsNullOrWhiteSpace(address), "ProductInfo.AuthorEmail was not found as a string constant");
        Assert.Matches(AddressShape(), address);
    }

    [Fact]
    public void TheWrittenOfferNamesTheApplicationAddress()
    {
        var offer = SourceOfferAddress(Notices());

        Assert.True(offer is not null, "THIRD-PARTY-NOTICES.txt has no 'requested from the publisher at <address>' source offer");
        Assert.Equal(AuthorEmail(ProductInfoSource()), offer);
    }

    [Fact]
    public void TheStoreSupportEmailIsTheApplicationAddress()
    {
        var field = SupportEmailField(Listing());

        Assert.True(field is not null, "msix/store-listing.md has no '- Support email: `<address>`' field");
        Assert.Equal(AuthorEmail(ProductInfoSource()), field);
    }

    [Fact]
    public void TheRetiredAddressIsInNeitherPublishedHolder()
    {
        Assert.DoesNotContain(RetiredAddress, Notices(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RetiredAddress, Listing(), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(RetiredAddress, AuthorEmail(ProductInfoSource()), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRealHoldersAgreeAsOne()
    {
        var problems = Problems(AuthorEmail(ProductInfoSource()), Notices(), Listing());

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ADisagreeingHolderIsCaught()
    {
        // The assertions above pass quietly when every holder agrees, so prove each way of disagreeing fails:
        // the offer left on the retired address, the listing field absent, and the listing on the retired address.
        const string notices = "Written offer\nthe corresponding source code may also be\nrequested from the publisher at sza@ukr.net. The publisher will provide\nit.\n";
        const string listing = "- Support: `https://example.invalid/support.html`\n- Support email: `sza@ukr.net`\n";

        Assert.Empty(Problems("sza@ukr.net", notices, listing));

        var staleOffer = Problems("sza@ukr.net", notices.Replace("sza@ukr.net", RetiredAddress, StringComparison.Ordinal), listing);
        Assert.Contains(staleOffer, problem => problem.Contains("source offer", StringComparison.Ordinal));
        Assert.Contains(staleOffer, problem => problem.Contains("retired", StringComparison.Ordinal));

        var noField = Problems("sza@ukr.net", notices, "- Support: `https://example.invalid/support.html`\n");
        Assert.Contains(noField, problem => problem.Contains("Support email", StringComparison.Ordinal));

        var staleListing = Problems("sza@ukr.net", notices, listing.Replace("sza@ukr.net", RetiredAddress, StringComparison.Ordinal));
        Assert.Contains(staleListing, problem => problem.Contains("Support email", StringComparison.Ordinal));
        Assert.Contains(staleListing, problem => problem.Contains("retired", StringComparison.Ordinal));

        var movedConstant = Problems("author@example.invalid", notices, listing);
        Assert.Equal(2, movedConstant.Count);

        var noOffer = Problems("sza@ukr.net", "no offer here\n", listing);
        Assert.Contains(noOffer, problem => problem.Contains("source offer", StringComparison.Ordinal));
    }

    [Fact]
    public void TheConstantReaderIgnoresCommentsAndOtherConstants()
    {
        // The reader works on the masked source, so a commented-out constant or the same words inside a string
        // cannot stand in for the real declaration.
        const string source = """
            public static class ProductInfo
            {
                // public const string AuthorEmail = "old@example.invalid";
                public const string Note = "public const string AuthorEmail = \"fake@example.invalid\";";
                public const string AuthorEmail = "real@example.invalid";
                public const string AuthorEmailDomain = "example.invalid";
            }
            """;

        Assert.Equal("real@example.invalid", AuthorEmail(AppSourceFile.Parse("ProductInfo.cs", source)));
        Assert.Null(AuthorEmail(AppSourceFile.Parse("ProductInfo.cs", "public static class ProductInfo { }")));
    }

    private static List<string> Problems(string? authorEmail, string notices, string listing)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(authorEmail))
        {
            problems.Add("ProductInfo.AuthorEmail is empty or missing");
            return problems;
        }

        var offer = SourceOfferAddress(notices);
        if (!string.Equals(offer, authorEmail, StringComparison.Ordinal))
        {
            problems.Add($"the source offer names '{offer ?? "(nothing)"}', the application says '{authorEmail}'");
        }

        var field = SupportEmailField(listing);
        if (!string.Equals(field, authorEmail, StringComparison.Ordinal))
        {
            problems.Add($"the Support email field says '{field ?? "(nothing)"}', the application says '{authorEmail}'");
        }

        foreach (var (name, text) in new[] { ("THIRD-PARTY-NOTICES.txt", notices), ("msix/store-listing.md", listing) })
        {
            if (text.Contains(RetiredAddress, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{name} still carries the retired address {RetiredAddress}");
            }
        }

        return problems;
    }

    // The string literal of `const string AuthorEmail`, found on the masked text so a comment or a string that
    // merely repeats the declaration is not mistaken for it. Null when there is no such constant.
    private static string? AuthorEmail(AppSourceFile source)
    {
        var declaration = AuthorEmailDeclaration().Match(source.Masked);
        return declaration.Success && source.Literals.TryGetValue(declaration.Groups["quote"].Index, out var value)
            ? value
            : null;
    }

    private static string? SourceOfferAddress(string notices)
    {
        var offer = SourceOffer().Match(notices);
        return offer.Success ? offer.Groups["address"].Value : null;
    }

    private static string? SupportEmailField(string listing)
    {
        var field = SupportEmail().Match(listing);
        return field.Success ? field.Groups["address"].Value : null;
    }

    private static AppSourceFile ProductInfoSource() => Assert.Single(AppSourceFile.LoadAll("ProductInfo.cs"));

    private static string Notices() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "notices", "THIRD-PARTY-NOTICES.txt"));

    private static string Listing() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "msix-listing", "store-listing.md"));

    [GeneratedRegex("""\bconst\s+string\s+AuthorEmail\s*=\s*(?<quote>")""")]
    private static partial Regex AuthorEmailDeclaration();

    // "...may also be requested from the publisher at <address>. The publisher will..." - the sentence wraps, and
    // a full stop ends it, so the address is whatever precedes the first full stop followed by white space.
    [GeneratedRegex("""requested\s+from\s+the\s+publisher\s+at\s+(?<address>[^\s@]+@[^\s@]+?)\.(?:\s|$)""")]
    private static partial Regex SourceOffer();

    [GeneratedRegex("""^-\s+Support email:\s*`(?<address>[^`\s]+)`\s*$""", RegexOptions.Multiline)]
    private static partial Regex SupportEmail();

    [GeneratedRegex("""^[^\s@]+@[^\s@]+\.[^\s@.]+$""")]
    private static partial Regex AddressShape();
}
