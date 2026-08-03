using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class ContentIndexGenerationTests
{
    private static IndexIngestionPolicy Policy => new(0, null, null, true, false, 0);

    private static IndexContentClassification Admitted(params Trigram[] trigrams)
        => new(IndexSkipReason.None, trigrams);

    private static ContentIndexGeneration Build(ContentIndexGenerationBuilder builder)
        => builder.Build(
            "scope",
            "volume",
            @"C:\r",
            new UsnCheckpoint(1, 10),
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void HasCapturedContentIdentity_ValidatesBoundsAndPresence()
    {
        var builder = new ContentIndexGenerationBuilder(
            Policy,
            identityProvider: path => path.EndsWith("captured.txt", StringComparison.Ordinal)
                ? new FileIdentity(7, new UsnFileIdentity(11, 0))
                : null);
        builder.AddClassifiedContent(@"C:\r\captured.txt", Admitted(new Trigram(1, 2, 3)));
        builder.AddClassifiedContent(@"C:\r\missing.txt", Admitted(new Trigram(4, 5, 6)));
        ContentIndexGeneration generation = Build(builder);

        Assert.False(generation.HasCapturedContentIdentity(-1));
        Assert.True(generation.HasCapturedContentIdentity(0));
        Assert.False(generation.HasCapturedContentIdentity(1));
        Assert.False(generation.HasCapturedContentIdentity(2));
    }

    [Fact]
    public void FromPersisted_ControlsDocumentsAndDefaultIdentities()
    {
        ContentIndexGeneration template = Build(new ContentIndexGenerationBuilder(Policy));
        IReadOnlyList<IReadOnlyCollection<Trigram>> documents =
            [new[] { new Trigram(1, 2, 3) }];
        var aliases = new Dictionary<string, (long AliasId, long ContentId)>
        {
            [@"C:\r\a.txt"] = (0, 0),
        };

        ContentIndexGeneration query = ContentIndexGeneration.FromPersisted(
            template.Manifest, documents, aliases, contentIdentities: null, retainDocuments: false);
        Assert.Empty(query.Documents);
        Assert.Single(query.ContentIdentities);
        Assert.Null(query.ContentIdentities[0]);

        UsnFileIdentity identity = new(9, 0);
        ContentIndexGeneration retained = ContentIndexGeneration.FromPersisted(
            template.Manifest, documents, aliases, new UsnFileIdentity?[] { identity }, retainDocuments: true);
        Assert.Same(documents, retained.Documents);
        Assert.Equal(identity, retained.ContentIdentities[0]);
    }

    [Fact]
    public void FromPersistedPostings_UsesProvidedOrDefaultIdentities()
    {
        ContentIndexGeneration template = Build(new ContentIndexGenerationBuilder(Policy));
        IReadOnlyList<IReadOnlyCollection<Trigram>> documents =
            [new[] { new Trigram(1, 2, 3) }];
        TrigramPostingIndex postings = TrigramPostingIndex.Build(documents);
        var aliases = new Dictionary<string, (long AliasId, long ContentId)>();

        ContentIndexGeneration defaults = ContentIndexGeneration.FromPersistedPostings(
            template.Manifest, postings, 1, aliases);
        Assert.Null(Assert.Single(defaults.ContentIdentities));

        UsnFileIdentity identity = new(12, 0);
        ContentIndexGeneration provided = ContentIndexGeneration.FromPersistedPostings(
            template.Manifest, postings, 1, aliases, new UsnFileIdentity?[] { identity });
        Assert.Equal(identity, Assert.Single(provided.ContentIdentities));
    }

    [Fact]
    public void AddClassifiedContent_UsesProviderAndRejectsUnadmittedContent()
    {
        int providerCalls = 0;
        var withProvider = new ContentIndexGenerationBuilder(
            Policy,
            identityProvider: _ =>
            {
                providerCalls++;
                return new FileIdentity(7, new UsnFileIdentity(21, 0));
            });
        Assert.Equal(0, withProvider.AddClassifiedContent(
            @"C:\r\a.txt", Admitted(new Trigram(1, 2, 3))));
        Assert.Equal(1, providerCalls);

        var withoutProvider = new ContentIndexGenerationBuilder(Policy);
        Assert.Equal(0, withoutProvider.AddClassifiedContent(
            @"C:\r\b.txt", Admitted(new Trigram(4, 5, 6))));
        Assert.Equal(-1, withoutProvider.AddClassifiedContent(
            @"C:\r\binary.dat", new IndexContentClassification(IndexSkipReason.Binary, [])));
    }

    [Fact]
    public void VolumeSerialInvariants_RejectConflictingSeedsAndFileIdentities()
    {
        var seeded = new ContentIndexGenerationBuilder(Policy);
        seeded.SeedVolumeSerialNumber(0);
        seeded.SeedVolumeSerialNumber(7);
        seeded.SeedVolumeSerialNumber(7);
        Assert.Throws<IndexVolumeChangedException>(() => seeded.SeedVolumeSerialNumber(8));

        var fileIdentity = new ContentIndexGenerationBuilder(Policy);
        fileIdentity.SeedVolumeSerialNumber(7);
        Assert.Equal(0, fileIdentity.AddClassifiedContent(
            @"C:\r\same-volume.txt",
            Admitted(new Trigram(1, 2, 3)),
            new FileIdentity(7, new UsnFileIdentity(1, 0))));
        Assert.Throws<IndexVolumeChangedException>(() => fileIdentity.AddClassifiedContent(
            @"C:\r\other-volume.txt",
            Admitted(new Trigram(4, 5, 6)),
            new FileIdentity(8, new UsnFileIdentity(2, 0))));
    }
}