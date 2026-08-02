using MyASR.Models;

namespace MyASR.Tests.Models;

public class LegacyModelLayoutTests
{
    private static string NewTempRoot()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "MyASR-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    [Test]
    public async Task Migrate_MovesFlatFiles_IntoModelFolder()
    {
        var root = NewTempRoot();
        try
        {
            foreach (var f in ModelCatalog.GigaAmV3.Files)
                File.WriteAllText(Path.Combine(root, f.LocalName), f.LocalName);

            var moved = LegacyModelLayout.Migrate(root, ModelCatalog.GigaAmV3);

            await Assert.That(moved).IsEqualTo(ModelCatalog.GigaAmV3.Files.Count);
            var dir = Path.Combine(root, "gigaam-v3");
            foreach (var f in ModelCatalog.GigaAmV3.Files)
            {
                await Assert.That(File.Exists(Path.Combine(dir, f.LocalName))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(root, f.LocalName))).IsFalse();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Migrate_PreservesFileContent()
    {
        var root = NewTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, GigaAmFiles.Vocab), "первая строка");

            LegacyModelLayout.Migrate(root, ModelCatalog.GigaAmV3);

            var moved = Path.Combine(root, "gigaam-v3", GigaAmFiles.Vocab);
            await Assert.That(File.ReadAllText(moved)).IsEqualTo("первая строка");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Migrate_NothingFlat_IsNoOp()
    {
        var root = NewTempRoot();
        try
        {
            var moved = LegacyModelLayout.Migrate(root, ModelCatalog.GigaAmV3);
            await Assert.That(moved).IsEqualTo(0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Migrate_IsIdempotent()
    {
        var root = NewTempRoot();
        try
        {
            foreach (var f in ModelCatalog.GigaAmV3.Files)
                File.WriteAllText(Path.Combine(root, f.LocalName), "x");

            var first = LegacyModelLayout.Migrate(root, ModelCatalog.GigaAmV3);
            var second = LegacyModelLayout.Migrate(root, ModelCatalog.GigaAmV3);

            await Assert.That(first).IsEqualTo(4);
            await Assert.That(second).IsEqualTo(0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Migrate_TargetAlreadyPresent_LeavesFlatFileUntouched()
    {
        var root = NewTempRoot();
        var dir = Path.Combine(root, "gigaam-v3");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(root, GigaAmFiles.Vocab), "старый");
            File.WriteAllText(Path.Combine(dir, GigaAmFiles.Vocab), "новый");

            var moved = LegacyModelLayout.Migrate(root, ModelCatalog.GigaAmV3);

            await Assert.That(moved).IsEqualTo(0);
            await Assert.That(File.ReadAllText(Path.Combine(dir, GigaAmFiles.Vocab))).IsEqualTo("новый");
            await Assert.That(File.Exists(Path.Combine(root, GigaAmFiles.Vocab))).IsTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task Migrate_MissingRoot_IsNoOp()
    {
        var root = Path.Combine(Path.GetTempPath(), "MyASR-tests-absent-" + Guid.NewGuid());
        var moved = LegacyModelLayout.Migrate(root, ModelCatalog.GigaAmV3);
        await Assert.That(moved).IsEqualTo(0);
    }
}
