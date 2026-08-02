using System.Net;
using System.Net.Http;
using MyASR.Models;

namespace MyASR.Tests.Models;

public class ModelProviderTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls.Add(req);
            return Task.FromResult(reply(req));
        }
    }

    private sealed class FixedSource(string baseUri) : IModelSourceResolver
    {
        public string ResolveBaseUri(ModelDescriptor model) => baseUri;
    }

    /// <summary>
    /// Unlike <see cref="Progress{T}"/>, which marshals its callback through the captured
    /// <see cref="SynchronizationContext"/> (or the thread pool when there is none) and only
    /// guarantees eventual delivery, this invokes the handler inline and synchronously on the
    /// calling thread so post-await assertions can rely on every report having already happened.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static string NewTempDir()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "MyASR-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    private static ModelDescriptor OneFileModel(string? sha = null) => new(
        Id: "test-model",
        DisplayName: "Test",
        LicenceNote: null,
        Kind: AsrEngineKind.RnnT,
        FolderName: "test-model",
        DefaultBaseUri: "https://example.test/base/",
        Files: [new ModelFileEntry("weights.bin", "sub/weights.bin", 8, sha)]);

    // --- HTTP source -------------------------------------------------------

    [Test]
    public async Task Http_DownloadsAllFiles_ToTheModelFolder()
    {
        var tmp = NewTempDir();
        try
        {
            var paths = new ModelPaths(tmp, ModelCatalog.GigaAmV3);
            var handler = new FakeHandler(_ =>
            {
                var content = new ByteArrayContent(new byte[16]);
                content.Headers.ContentLength = 16;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            });
            var provider = new ModelProvider(
                new HttpClient(handler),
                new FixedSource(ModelCatalog.GigaAmV3.DefaultBaseUri));

            await provider.EnsureDownloadedAsync(paths, progress: null, CancellationToken.None);

            foreach (var f in ModelCatalog.GigaAmV3.Files)
                await Assert.That(File.Exists(paths.File(f.LocalName))).IsTrue();
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Test]
    public async Task Http_JoinsBaseUriWithRelativePath()
    {
        var tmp = NewTempDir();
        try
        {
            var model = OneFileModel();
            var paths = new ModelPaths(tmp, model);
            var handler = new FakeHandler(_ =>
            {
                var content = new ByteArrayContent(new byte[8]);
                content.Headers.ContentLength = 8;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            });
            var provider = new ModelProvider(new HttpClient(handler), new FixedSource("https://example.test/base"));

            await provider.EnsureDownloadedAsync(paths, progress: null, CancellationToken.None);

            await Assert.That(handler.Calls[0].RequestUri!.ToString())
                .IsEqualTo("https://example.test/base/sub/weights.bin");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Test]
    public async Task Http_ResumesPartialFile_WithRangeHeader()
    {
        var tmp = NewTempDir();
        try
        {
            var model = OneFileModel();
            var paths = new ModelPaths(tmp, model);
            Directory.CreateDirectory(paths.Root);
            File.WriteAllBytes(paths.File("weights.bin.partial"), new byte[5]);

            var handler = new FakeHandler(_ =>
            {
                var content = new ByteArrayContent(new byte[3]);
                content.Headers.ContentLength = 3;
                content.Headers.ContentRange = new(5, 7, 8);
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
            });
            var provider = new ModelProvider(new HttpClient(handler), new FixedSource("https://example.test/base/"));

            await provider.EnsureDownloadedAsync(paths, progress: null, CancellationToken.None);

            await Assert.That(handler.Calls[0].Headers.Range!.ToString()).IsEqualTo("bytes=5-");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // --- Local folder source ----------------------------------------------

    [Test]
    public async Task LocalFolder_CopiesFiles_AndReportsProgress()
    {
        var src = NewTempDir();
        var dst = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllBytes(Path.Combine(src, "sub", "weights.bin"), new byte[8]);

            var model = OneFileModel();
            var paths = new ModelPaths(dst, model);
            var seen = new List<DownloadProgress>();
            var provider = new ModelProvider(new HttpClient(), new FixedSource(src));

            await provider.EnsureDownloadedAsync(
                paths, new SynchronousProgress<DownloadProgress>(seen.Add), CancellationToken.None);

            await Assert.That(File.Exists(paths.File("weights.bin"))).IsTrue();
            await Assert.That(new FileInfo(paths.File("weights.bin")).Length).IsEqualTo(8L);
            await Assert.That(seen).IsNotEmpty();
            await Assert.That(seen[^1].Downloaded).IsEqualTo(seen[^1].Total);
            await Assert.That(seen[^1].FileCount).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(src, recursive: true);
            Directory.Delete(dst, recursive: true);
        }
    }

    [Test]
    public async Task LocalFolder_MissingSourceFile_Throws()
    {
        var src = NewTempDir();
        var dst = NewTempDir();
        try
        {
            var paths = new ModelPaths(dst, OneFileModel());
            var provider = new ModelProvider(new HttpClient(), new FixedSource(src));

            await Assert.That(async () => await provider.EnsureDownloadedAsync(
                    paths, progress: null, CancellationToken.None))
                .Throws<FileNotFoundException>();
        }
        finally
        {
            Directory.Delete(src, recursive: true);
            Directory.Delete(dst, recursive: true);
        }
    }

    [Test]
    public async Task Sha256Mismatch_DeletesPartial_AndThrows()
    {
        var src = NewTempDir();
        var dst = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllBytes(Path.Combine(src, "sub", "weights.bin"), new byte[8]);

            var model = OneFileModel(sha: new string('a', 64));
            var paths = new ModelPaths(dst, model);
            var provider = new ModelProvider(new HttpClient(), new FixedSource(src));

            await Assert.That(async () => await provider.EnsureDownloadedAsync(
                    paths, progress: null, CancellationToken.None))
                .Throws<InvalidDataException>();

            await Assert.That(File.Exists(paths.File("weights.bin.partial"))).IsFalse();
            await Assert.That(File.Exists(paths.File("weights.bin"))).IsFalse();
        }
        finally
        {
            Directory.Delete(src, recursive: true);
            Directory.Delete(dst, recursive: true);
        }
    }

    // --- Availability ------------------------------------------------------

    [Test]
    public async Task AreModelsAvailable_AllPresent_ReturnsTrue()
    {
        var tmp = NewTempDir();
        try
        {
            var paths = new ModelPaths(tmp, ModelCatalog.GigaAmV3);
            Directory.CreateDirectory(paths.Root);
            foreach (var f in ModelCatalog.GigaAmV3.Files)
                File.WriteAllText(paths.File(f.LocalName), "x");

            var provider = new ModelProvider(new HttpClient(), new FixedSource("https://example.test/"));
            await Assert.That(provider.AreModelsAvailable(paths)).IsTrue();
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Test]
    public async Task AreModelsAvailable_AnyMissing_ReturnsFalse()
    {
        var tmp = NewTempDir();
        try
        {
            var paths = new ModelPaths(tmp, ModelCatalog.GigaAmV3);
            Directory.CreateDirectory(paths.Root);
            File.WriteAllText(paths.File(GigaAmFiles.Vocab), "x");

            var provider = new ModelProvider(new HttpClient(), new FixedSource("https://example.test/"));
            await Assert.That(provider.AreModelsAvailable(paths)).IsFalse();
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Test]
    public async Task AreModelsAvailable_IsPerModel()
    {
        var tmp = NewTempDir();
        try
        {
            var gigaam = new ModelPaths(tmp, ModelCatalog.GigaAmV3);
            Directory.CreateDirectory(gigaam.Root);
            foreach (var f in ModelCatalog.GigaAmV3.Files)
                File.WriteAllText(gigaam.File(f.LocalName), "x");

            var other = new ModelPaths(tmp, OneFileModel());
            var provider = new ModelProvider(new HttpClient(), new FixedSource("https://example.test/"));

            await Assert.That(provider.AreModelsAvailable(gigaam)).IsTrue();
            await Assert.That(provider.AreModelsAvailable(other)).IsFalse();
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Test]
    public async Task ExistingFile_IsNotRefetched()
    {
        var src = NewTempDir();
        var dst = NewTempDir();
        try
        {
            var model = OneFileModel();
            var paths = new ModelPaths(dst, model);
            Directory.CreateDirectory(paths.Root);
            File.WriteAllText(paths.File("weights.bin"), "already here");

            var provider = new ModelProvider(new HttpClient(), new FixedSource(src));
            await provider.EnsureDownloadedAsync(paths, progress: null, CancellationToken.None);

            await Assert.That(File.ReadAllText(paths.File("weights.bin"))).IsEqualTo("already here");
        }
        finally
        {
            Directory.Delete(src, recursive: true);
            Directory.Delete(dst, recursive: true);
        }
    }
}
