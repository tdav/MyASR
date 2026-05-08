using System.Net;
using System.Net.Http;
using MyASR.Models;

namespace MyASR.Tests.Models;

public class HuggingFaceModelProviderTests
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

    [Test]
    public async Task EnsureDownloadedAsync_DownloadsAllFiles_ToCorrectPaths()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "MyASR-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            var paths = new ModelPaths(tmp);
            var handler = new FakeHandler(_ =>
            {
                var content = new ByteArrayContent(new byte[16]);
                content.Headers.ContentLength = 16;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            });
            var provider = new HuggingFaceModelProvider(new HttpClient(handler), ModelManifest.Files);

            await provider.EnsureDownloadedAsync(paths, progress: null, CancellationToken.None);

            await Assert.That(File.Exists(paths.Encoder)).IsTrue();
            await Assert.That(File.Exists(paths.Decoder)).IsTrue();
            await Assert.That(File.Exists(paths.Joint)).IsTrue();
            await Assert.That(File.Exists(paths.Vocab)).IsTrue();
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task EnsureDownloadedAsync_ResumesPartialFile_WithRangeHeader()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "MyASR-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        File.WriteAllBytes(Path.Combine(tmp, "v3_vocab.txt.partial"), new byte[5]);
        try
        {
            var paths = new ModelPaths(tmp);
            var handler = new FakeHandler(_ =>
            {
                var content = new ByteArrayContent(new byte[3]);
                content.Headers.ContentLength = 3;
                content.Headers.ContentRange = new(5, 7, 8);
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
            });
            var provider = new HuggingFaceModelProvider(
                new HttpClient(handler),
                [new ModelFileEntry("v3_vocab.txt", "https://example/vocab.txt", 8, null)]);

            await provider.EnsureDownloadedAsync(paths, progress: null, CancellationToken.None);

            var firstReq = handler.Calls[0];
            await Assert.That(firstReq.Headers.Range!.ToString()).IsEqualTo("bytes=5-");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Test]
    public async Task AreModelsAvailable_AllPresent_ReturnsTrue()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "MyASR-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in ModelManifest.Files)
                File.WriteAllText(Path.Combine(tmp, f.FileName), "x");
            var paths = new ModelPaths(tmp);
            var provider = new HuggingFaceModelProvider(new HttpClient(), ModelManifest.Files);
            await Assert.That(provider.AreModelsAvailable(paths)).IsTrue();
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Test]
    public async Task AreModelsAvailable_AnyMissing_ReturnsFalse()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "MyASR-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            // create only one file
            File.WriteAllText(Path.Combine(tmp, "v3_vocab.txt"), "x");
            var paths = new ModelPaths(tmp);
            var provider = new HuggingFaceModelProvider(new HttpClient(), ModelManifest.Files);
            await Assert.That(provider.AreModelsAvailable(paths)).IsFalse();
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
