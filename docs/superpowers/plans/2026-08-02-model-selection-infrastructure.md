# Model Selection Infrastructure Implementation Plan (Phase 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user pick the ASR model from a combo box in the main window, with per-model folders, on-demand acquisition from either an HTTP base URI or a local folder, and a hot engine swap — all working end to end with GigaAM v3 as the only catalogue entry.

**Architecture:** The feature extractor and the decoder stop being independent dependencies and become one replaceable `IAsrEngine`, because Whisper and RNN-T features are incompatible and a mismatch produces plausible nonsense rather than an exception. A `ModelCatalog` of `ModelDescriptor` records drives paths, downloads and UI. Each model gets its own subfolder under the models root, with a one-shot migration of the existing flat GigaAM layout.

**Tech Stack:** .NET 10, WinForms, `Microsoft.ML.OnnxRuntime` 1.26.0, `Microsoft.Extensions.DependencyInjection` 10.0.7, TUnit 1.43.37.

**Spec:** `docs/superpowers/specs/2026-08-02-crisperwhisper-model-selection-design.md`

## Global Constraints

- Private fields are camelCase **without** a `_` prefix (`private readonly List<string> folderOrder;`). Use `this.` when a field name collides with a parameter. Existing designer-generated fields (`_deviceCombo`, `_vuMeter`, `_modelLabel`) keep their current names; do not rename them.
- Target framework `net10.0-windows`, `Nullable` enabled, `ImplicitUsings` enabled. `System.IO` and `System.Net.Http` need explicit `using` in `MyASR` (they are not in the implicit set for WinForms projects).
- User-facing strings in the UI are Russian, matching the existing forms.
- Tests are TUnit: `[Test] public async Task Name()` with `await Assert.That(actual).IsEqualTo(expected)`.
- Test command for every verification step: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
- No model files exist on CI. Every test in this phase must pass without any `.onnx` on disk.
- Models root resolution stays exactly as today: `MYASR_MODELS_DIR` when set and non-blank, otherwise `%LOCALAPPDATA%\MyASR\models`.

---

### Task 1: Model catalogue types

**Files:**
- Create: `MyASR/Models/ModelDescriptor.cs`
- Create: `MyASR/Models/ModelCatalog.cs`
- Delete: `MyASR/Models/ModelManifest.cs`
- Modify: `MyASR/Models/HuggingFaceModelProvider.cs:13,23` (property rename only)
- Modify: `MyASR/Program.cs:28` (registration of the manifest)
- Modify: `MyASR.Tests/Models/HuggingFaceModelProviderTests.cs:33,66,86,89,105` (manifest references)
- Test: `MyASR.Tests/Models/ModelCatalogTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `AsrEngineKind` enum (`RnnT`, `Whisper`); `ModelFileEntry(string LocalName, string SourceRelativePath, long Size, string? Sha256)`; `ModelDescriptor(string Id, string DisplayName, string? LicenceNote, AsrEngineKind Kind, string FolderName, string DefaultBaseUri, IReadOnlyList<ModelFileEntry> Files)` with `long TotalBytes`; `ModelCatalog.All`, `ModelCatalog.Default`, `ModelCatalog.GigaAmV3`, `ModelCatalog.ById(string? id)`; `GigaAmFiles.Encoder/Decoder/Joint/Vocab` constants.

- [ ] **Step 1: Write the failing test**

Create `MyASR.Tests/Models/ModelCatalogTests.cs`:

```csharp
using MyASR.Models;

namespace MyASR.Tests.Models;

public class ModelCatalogTests
{
    [Test]
    public async Task All_IsNotEmpty_AndDefaultIsGigaAm()
    {
        await Assert.That(ModelCatalog.All).IsNotEmpty();
        await Assert.That(ModelCatalog.Default.Id).IsEqualTo("gigaam-v3-rnnt");
    }

    [Test]
    public async Task Ids_AreUnique()
    {
        var ids = ModelCatalog.All.Select(m => m.Id).ToList();
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);
    }

    [Test]
    public async Task FolderNames_AreUnique()
    {
        var folders = ModelCatalog.All.Select(m => m.FolderName).ToList();
        await Assert.That(folders.Distinct().Count()).IsEqualTo(folders.Count);
    }

    [Test]
    public async Task EveryDescriptor_HasFiles_AndPositiveTotalBytes()
    {
        foreach (var m in ModelCatalog.All)
        {
            await Assert.That(m.Files).IsNotEmpty();
            await Assert.That(m.TotalBytes).IsGreaterThan(0L);
        }
    }

    [Test]
    public async Task EveryDescriptor_HasNonBlankDefaultBaseUri()
    {
        foreach (var m in ModelCatalog.All)
            await Assert.That(string.IsNullOrWhiteSpace(m.DefaultBaseUri)).IsFalse();
    }

    [Test]
    public async Task ById_UnknownOrNull_FallsBackToDefault()
    {
        await Assert.That(ModelCatalog.ById("no-such-model").Id).IsEqualTo(ModelCatalog.Default.Id);
        await Assert.That(ModelCatalog.ById(null).Id).IsEqualTo(ModelCatalog.Default.Id);
    }

    [Test]
    public async Task ById_KnownId_ReturnsThatModel()
    {
        await Assert.That(ModelCatalog.ById("gigaam-v3-rnnt").Kind).IsEqualTo(AsrEngineKind.RnnT);
    }

    [Test]
    public async Task GigaAm_ListsTheFourExpectedFiles()
    {
        var names = ModelCatalog.GigaAmV3.Files.Select(f => f.LocalName).ToList();
        await Assert.That(names).Contains(GigaAmFiles.Encoder);
        await Assert.That(names).Contains(GigaAmFiles.Decoder);
        await Assert.That(names).Contains(GigaAmFiles.Joint);
        await Assert.That(names).Contains(GigaAmFiles.Vocab);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile errors — `The name 'ModelCatalog' does not exist`, `The type or namespace name 'AsrEngineKind' could not be found`, `'GigaAmFiles' does not exist`.

- [ ] **Step 3: Create the descriptor types**

Create `MyASR/Models/ModelDescriptor.cs`:

```csharp
namespace MyASR.Models;

/// <summary>Which recognition pipeline a model needs. Features and decoder must always match.</summary>
public enum AsrEngineKind
{
    RnnT,
    Whisper
}

/// <param name="LocalName">File name inside the model's own folder.</param>
/// <param name="SourceRelativePath">Appended to the resolved base URI to locate the source.</param>
/// <param name="Size">Approximate size in bytes; used for progress before the source reports one.</param>
/// <param name="Sha256">Lower-case hex digest, or null when no digest has been captured yet.</param>
public sealed record ModelFileEntry(
    string LocalName,
    string SourceRelativePath,
    long Size,
    string? Sha256);

public sealed record ModelDescriptor(
    string Id,
    string DisplayName,
    string? LicenceNote,
    AsrEngineKind Kind,
    string FolderName,
    string DefaultBaseUri,
    IReadOnlyList<ModelFileEntry> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);
}
```

- [ ] **Step 4: Create the catalogue**

Create `MyASR/Models/ModelCatalog.cs`:

```csharp
namespace MyASR.Models;

/// <summary>File names of the GigaAM v3 RNN-T artefacts, shared by the catalogue and the engine.</summary>
public static class GigaAmFiles
{
    public const string Encoder = "v3_rnnt_encoder.onnx";
    public const string Decoder = "v3_rnnt_decoder.onnx";
    public const string Joint   = "v3_rnnt_joint.onnx";
    public const string Vocab   = "v3_vocab.txt";
}

public static class ModelCatalog
{
    private const string GigaAmBase = "https://huggingface.co/istupakov/gigaam-v3-onnx/resolve/main/";

    // SHA-256 digests stay null until known-good hashes are captured after a first download.
    public static readonly ModelDescriptor GigaAmV3 = new(
        Id: "gigaam-v3-rnnt",
        DisplayName: "GigaAM v3 (RU, RNN-T)",
        LicenceNote: null,
        Kind: AsrEngineKind.RnnT,
        FolderName: "gigaam-v3",
        DefaultBaseUri: GigaAmBase,
        Files:
        [
            new(GigaAmFiles.Encoder, GigaAmFiles.Encoder, 928_000_000, null),
            new(GigaAmFiles.Decoder, GigaAmFiles.Decoder, 3_490_000,   null),
            new(GigaAmFiles.Joint,   GigaAmFiles.Joint,   1_510_000,   null),
            new(GigaAmFiles.Vocab,   GigaAmFiles.Vocab,   198,         null),
        ]);

    public static IReadOnlyList<ModelDescriptor> All { get; } = [GigaAmV3];

    public static ModelDescriptor Default => GigaAmV3;

    /// <summary>Looks a model up by its persisted id, falling back to <see cref="Default"/>.</summary>
    public static ModelDescriptor ById(string? id) =>
        All.FirstOrDefault(m => m.Id == id) ?? Default;
}
```

- [ ] **Step 5: Retire ModelManifest and fix its call sites**

Delete `MyASR/Models/ModelManifest.cs`.

In `MyASR/Models/HuggingFaceModelProvider.cs`, the manifest entries now expose `LocalName` instead of `FileName`. Replace line 13 and line 23:

```csharp
            if (!File.Exists(Path.Combine(paths.Root, f.LocalName))) return false;
```

```csharp
            var dst = Path.Combine(paths.Root, f.LocalName);
```

and in `DownloadOneAsync` replace the two `f.FileName` progress arguments and the exception message:

```csharp
                progress.Report(new DownloadProgress(f.LocalName, downloaded, total, bps));
```

```csharp
            throw new InvalidDataException($"SHA-256 mismatch for {f.LocalName}");
```

```csharp
        progress?.Report(new DownloadProgress(f.LocalName, total, total, 0));
```

The `f.Url` reference on line 34 becomes `f.SourceRelativePath` temporarily — it still holds a full URL for GigaAM because `SourceRelativePath` equals the file name and the base is joined in Task 5. To keep this task compiling and honest, change the manifest entries' `SourceRelativePath` usage in the provider to build the full URL inline for now:

```csharp
        // Scaffolding: Task 5 replaces this with a resolved base URI. Until then the provider
        // can only serve GigaAM, and says so instead of silently fetching the wrong model.
        if (!ReferenceEquals(manifest, ModelCatalog.GigaAmV3.Files))
            throw new NotSupportedException("Пока поддерживается загрузка только GigaAM v3.");
        using var req = new HttpRequestMessage(HttpMethod.Get, ModelCatalog.GigaAmV3.DefaultBaseUri + f.SourceRelativePath);
```

This block is replaced properly in Task 5; it exists only so the tree builds between tasks.
The `ModelProviderTests` in Task 5 replace the hand-built manifest tests that would trip the
guard, and the one Task 1 test that passes a custom entry list
(`EnsureDownloadedAsync_ResumesPartialFile_WithRangeHeader`) must be marked
`[Skip("Восстанавливается в задаче 5, когда источник станет настраиваемым")]` for the duration
of Tasks 1-4.

In `MyASR/Program.cs` line 28, replace the manifest registration:

```csharp
        services.AddSingleton<IReadOnlyList<ModelFileEntry>>(_ => ModelCatalog.GigaAmV3.Files);
```

In `MyASR.Tests/Models/HuggingFaceModelProviderTests.cs`, replace every `ModelManifest.Files` with `ModelCatalog.GigaAmV3.Files`, replace `f.FileName` with `f.LocalName` on line 87, and change the hand-built entry on line 66 to the new record shape:

```csharp
                [new ModelFileEntry("v3_vocab.txt", "v3_vocab.txt", 8, null)]);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS, including the eight new `ModelCatalogTests`.

- [ ] **Step 7: Commit**

```bash
git add MyASR/Models MyASR/Program.cs MyASR.Tests/Models
git commit -m "feat(models): introduce ModelDescriptor catalogue replacing ModelManifest"
```

---

### Task 2: Per-model paths and models root

**Files:**
- Create: `MyASR/Models/ModelRoot.cs`
- Modify: `MyASR/Models/ModelPaths.cs` (full rewrite)
- Modify: `MyASR/Asr/RnnTGreedyDecoder.cs:22-35` (constructor)
- Modify: `MyASR/Program.cs:29,36-41`
- Modify: `MyASR.Tests/Models/ModelPathsTests.cs` (full rewrite)
- Modify: `MyASR.Tests/Models/HuggingFaceModelProviderTests.cs` (all `new ModelPaths(tmp)` call sites)
- Test: `MyASR.Tests/Models/ModelPathsTests.cs`

**Interfaces:**
- Consumes: `ModelDescriptor`, `ModelCatalog`, `GigaAmFiles` from Task 1.
- Produces: `ModelRoot.Resolve() -> string`; `ModelPaths(string root, ModelDescriptor model)` with `ModelDescriptor Model { get; }`, `string Root { get; }`, `string File(string localName)`; `RnnTGreedyDecoder(string encoderPath, string decoderPath, string jointPath, Vocabulary vocab)`.

- [ ] **Step 1: Write the failing test**

Replace the contents of `MyASR.Tests/Models/ModelPathsTests.cs`:

```csharp
using MyASR.Models;

namespace MyASR.Tests.Models;

public class ModelPathsTests
{
    [Test]
    [NotInParallel("env-var-mutex")]
    public async Task ModelRoot_Default_PointsTo_LocalAppData_MyASR_Models()
    {
        var saved = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", null);
            await Assert.That(ModelRoot.Resolve()).EndsWith("MyASR\\models");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", saved);
        }
    }

    [Test]
    [NotInParallel("env-var-mutex")]
    public async Task ModelRoot_EnvironmentOverride_TakesPrecedence()
    {
        var saved = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", @"C:\my-override");
            await Assert.That(ModelRoot.Resolve()).IsEqualTo(@"C:\my-override");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", saved);
        }
    }

    [Test]
    [NotInParallel("env-var-mutex")]
    public async Task ModelRoot_BlankEnvironmentValue_IsIgnored()
    {
        var saved = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", "   ");
            await Assert.That(ModelRoot.Resolve()).EndsWith("MyASR\\models");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYASR_MODELS_DIR", saved);
        }
    }

    [Test]
    public async Task Root_IsRootPlusModelFolder()
    {
        var paths = new ModelPaths(@"C:\m", ModelCatalog.GigaAmV3);
        await Assert.That(paths.Root).IsEqualTo(@"C:\m\gigaam-v3");
    }

    [Test]
    public async Task File_CombinesModelRootWithLocalName()
    {
        var paths = new ModelPaths(@"C:\m", ModelCatalog.GigaAmV3);
        await Assert.That(paths.File(GigaAmFiles.Encoder)).IsEqualTo(@"C:\m\gigaam-v3\v3_rnnt_encoder.onnx");
        await Assert.That(paths.File(GigaAmFiles.Vocab)).IsEqualTo(@"C:\m\gigaam-v3\v3_vocab.txt");
    }

    [Test]
    public async Task Model_IsExposedForDownstreamConsumers()
    {
        var paths = new ModelPaths(@"C:\m", ModelCatalog.GigaAmV3);
        await Assert.That(paths.Model.Id).IsEqualTo("gigaam-v3-rnnt");
    }

    [Test]
    public async Task TwoModels_DoNotShareAFolder()
    {
        var other = ModelCatalog.GigaAmV3 with { Id = "other", FolderName = "other-model" };
        var a = new ModelPaths(@"C:\m", ModelCatalog.GigaAmV3);
        var b = new ModelPaths(@"C:\m", other);
        await Assert.That(a.Root).IsNotEqualTo(b.Root);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile errors — `The name 'ModelRoot' does not exist`, `ModelPaths does not contain a constructor that takes 2 arguments`.

- [ ] **Step 3: Write the implementation**

Create `MyASR/Models/ModelRoot.cs`:

```csharp
using System.IO;

namespace MyASR.Models;

/// <summary>Resolves the directory that holds every model's folder.</summary>
public static class ModelRoot
{
    public static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("MYASR_MODELS_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "MyASR", "models");
    }
}
```

Replace the contents of `MyASR/Models/ModelPaths.cs`:

```csharp
using System.IO;

namespace MyASR.Models;

/// <summary>Locates one model's files inside its own folder under the models root.</summary>
public sealed class ModelPaths
{
    public ModelPaths(string root, ModelDescriptor model)
    {
        Model = model;
        Root = Path.Combine(root, model.FolderName);
    }

    public ModelDescriptor Model { get; }

    /// <summary>Absolute path of this model's own folder.</summary>
    public string Root { get; }

    public string File(string localName) => Path.Combine(Root, localName);
}
```

In `MyASR/Asr/RnnTGreedyDecoder.cs`, replace the constructor (lines 22-35) so the decoder no longer knows about `ModelPaths`:

```csharp
    public RnnTGreedyDecoder(string encoderPath, string decoderPath, string jointPath, Vocabulary vocab)
    {
        var opts = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_PARALLEL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
        };
        _encoder = new InferenceSession(encoderPath, opts);
        _decoder = new InferenceSession(decoderPath, opts);
        _joint   = new InferenceSession(jointPath,   opts);
        _vocab   = vocab;
    }
```

Remove the now-unused `using MyASR.Models;` from that file.

In `MyASR/Program.cs`, replace the `ModelPaths` registration on line 29 and the decoder factory on lines 36-41:

```csharp
        services.AddSingleton<Func<ModelDescriptor, ModelPaths>>(_ =>
        {
            var root = ModelRoot.Resolve();
            return descriptor => new ModelPaths(root, descriptor);
        });
        services.AddSingleton<ModelPaths>(sp =>
            sp.GetRequiredService<Func<ModelDescriptor, ModelPaths>>()(ModelCatalog.Default));
```

```csharp
        // Decoder is built lazily because it requires model files on disk.
        services.AddSingleton<Func<IRnnTDecoder>>(sp => () =>
        {
            var paths = sp.GetRequiredService<ModelPaths>();
            var vocab = Vocabulary.Load(paths.File(GigaAmFiles.Vocab));
            return new RnnTGreedyDecoder(
                paths.File(GigaAmFiles.Encoder),
                paths.File(GigaAmFiles.Decoder),
                paths.File(GigaAmFiles.Joint),
                vocab);
        });
```

In `MyASR.Tests/Models/HuggingFaceModelProviderTests.cs`, every `new ModelPaths(tmp)` becomes `new ModelPaths(tmp, ModelCatalog.GigaAmV3)`. The four `File.Exists(paths.Encoder)` assertions in the first test become:

```csharp
            await Assert.That(File.Exists(paths.File(GigaAmFiles.Encoder))).IsTrue();
            await Assert.That(File.Exists(paths.File(GigaAmFiles.Decoder))).IsTrue();
            await Assert.That(File.Exists(paths.File(GigaAmFiles.Joint))).IsTrue();
            await Assert.That(File.Exists(paths.File(GigaAmFiles.Vocab))).IsTrue();
```

The resume test writes its `.partial` into the model folder now, so replace its setup lines:

```csharp
        var modelDir = Path.Combine(tmp, ModelCatalog.GigaAmV3.FolderName);
        Directory.CreateDirectory(modelDir);
        File.WriteAllBytes(Path.Combine(modelDir, "v3_vocab.txt.partial"), new byte[5]);
```

and the two `AreModelsAvailable` tests write their files into `modelDir` built the same way instead of `tmp`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MyASR/Models MyASR/Asr/RnnTGreedyDecoder.cs MyASR/Program.cs MyASR.Tests/Models
git commit -m "feat(models): give every model its own folder under the models root"
```

---

### Task 3: Legacy flat-layout migration

**Files:**
- Create: `MyASR/Models/LegacyModelLayout.cs`
- Test: `MyASR.Tests/Models/LegacyModelLayoutTests.cs`

**Interfaces:**
- Consumes: `ModelDescriptor`, `ModelCatalog` from Task 1.
- Produces: `LegacyModelLayout.Migrate(string root, ModelDescriptor model) -> int` returning the number of files moved.

- [ ] **Step 1: Write the failing test**

Create `MyASR.Tests/Models/LegacyModelLayoutTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile error — `The name 'LegacyModelLayout' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `MyASR/Models/LegacyModelLayout.cs`:

```csharp
using System.IO;

namespace MyASR.Models;

/// <summary>
/// Before per-model folders existed, GigaAM's files lived directly in the models root.
/// Moving them avoids a 928 MB re-download on upgrade. Safe to call on every startup.
/// </summary>
public static class LegacyModelLayout
{
    /// <returns>How many files were moved.</returns>
    public static int Migrate(string root, ModelDescriptor model)
    {
        if (!Directory.Exists(root)) return 0;

        var target = Path.Combine(root, model.FolderName);
        var moved = 0;

        foreach (var f in model.Files)
        {
            var flat = Path.Combine(root, f.LocalName);
            var dest = Path.Combine(target, f.LocalName);

            // A file already in place wins; the stale flat copy is left alone rather than deleted.
            if (!File.Exists(flat) || File.Exists(dest)) continue;

            Directory.CreateDirectory(target);
            File.Move(flat, dest);
            moved++;
        }

        return moved;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MyASR/Models/LegacyModelLayout.cs MyASR.Tests/Models/LegacyModelLayoutTests.cs
git commit -m "feat(models): migrate the legacy flat GigaAM layout into gigaam-v3/"
```

---

### Task 4: Model source resolver

**Files:**
- Create: `MyASR/Models/IModelSourceResolver.cs`
- Create: `MyASR/Models/ModelSourceResolver.cs`
- Test: `MyASR.Tests/Models/ModelSourceResolverTests.cs`

**Interfaces:**
- Consumes: `ModelDescriptor` from Task 1.
- Produces: `IModelSourceResolver.ResolveBaseUri(ModelDescriptor) -> string`; `ModelSourceResolver(IReadOnlyDictionary<string, string>? settingsSources)`; `ModelSourceResolver.EnvVarName(string modelId) -> string`.

- [ ] **Step 1: Write the failing test**

Create `MyASR.Tests/Models/ModelSourceResolverTests.cs`:

```csharp
using MyASR.Models;

namespace MyASR.Tests.Models;

public class ModelSourceResolverTests
{
    private static readonly ModelDescriptor Model = ModelCatalog.GigaAmV3;

    [Test]
    public async Task EnvVarName_UppercasesAndReplacesDashes()
    {
        await Assert.That(ModelSourceResolver.EnvVarName("crisperwhisper2-small-int8"))
            .IsEqualTo("MYASR_MODEL_SOURCE_CRISPERWHISPER2_SMALL_INT8");
    }

    [Test]
    public async Task NoOverrides_ReturnsDescriptorDefault()
    {
        var resolver = new ModelSourceResolver(null);
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(Model.DefaultBaseUri);
    }

    [Test]
    public async Task SettingsOverride_BeatsDefault()
    {
        var resolver = new ModelSourceResolver(new Dictionary<string, string>
        {
            [Model.Id] = @"D:\models\gigaam"
        });
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(@"D:\models\gigaam");
    }

    [Test]
    public async Task BlankSettingsValue_IsIgnored()
    {
        var resolver = new ModelSourceResolver(new Dictionary<string, string> { [Model.Id] = "  " });
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(Model.DefaultBaseUri);
    }

    [Test]
    [NotInParallel("env-var-mutex")]
    public async Task EnvironmentOverride_BeatsSettings()
    {
        var name = ModelSourceResolver.EnvVarName(Model.Id);
        var saved = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "https://example.test/models/");
            var resolver = new ModelSourceResolver(new Dictionary<string, string>
            {
                [Model.Id] = @"D:\models\gigaam"
            });
            await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo("https://example.test/models/");
        }
        finally { Environment.SetEnvironmentVariable(name, saved); }
    }

    [Test]
    public async Task SettingsForAnotherModel_DoNotLeak()
    {
        var resolver = new ModelSourceResolver(new Dictionary<string, string> { ["some-other-id"] = @"D:\x" });
        await Assert.That(resolver.ResolveBaseUri(Model)).IsEqualTo(Model.DefaultBaseUri);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile error — `The name 'ModelSourceResolver' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `MyASR/Models/IModelSourceResolver.cs`:

```csharp
namespace MyASR.Models;

/// <summary>Decides where a model's files are fetched from: an HTTP base URI or a local folder.</summary>
public interface IModelSourceResolver
{
    string ResolveBaseUri(ModelDescriptor model);
}
```

Create `MyASR/Models/ModelSourceResolver.cs`:

```csharp
namespace MyASR.Models;

/// <summary>Environment variable beats user settings beats the descriptor's default.</summary>
public sealed class ModelSourceResolver(IReadOnlyDictionary<string, string>? settingsSources)
    : IModelSourceResolver
{
    public static string EnvVarName(string modelId) =>
        "MYASR_MODEL_SOURCE_" + modelId.ToUpperInvariant().Replace('-', '_');

    public string ResolveBaseUri(ModelDescriptor model)
    {
        var env = Environment.GetEnvironmentVariable(EnvVarName(model.Id));
        if (!string.IsNullOrWhiteSpace(env)) return env;

        if (settingsSources is not null
            && settingsSources.TryGetValue(model.Id, out var configured)
            && !string.IsNullOrWhiteSpace(configured))
            return configured;

        return model.DefaultBaseUri;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add MyASR/Models/IModelSourceResolver.cs MyASR/Models/ModelSourceResolver.cs MyASR.Tests/Models/ModelSourceResolverTests.cs
git commit -m "feat(models): resolve a model's source from env, settings or the catalogue"
```

---

### Task 5: Model provider with HTTP and local-folder sources

**Files:**
- Create: `MyASR/Models/ModelProvider.cs`
- Delete: `MyASR/Models/HuggingFaceModelProvider.cs`
- Modify: `MyASR/Models/IModelProvider.cs` (progress record gains file position)
- Modify: `MyASR/Program.cs:27-30`
- Rename: `MyASR.Tests/Models/HuggingFaceModelProviderTests.cs` → `MyASR.Tests/Models/ModelProviderTests.cs`
- Test: `MyASR.Tests/Models/ModelProviderTests.cs`

**Interfaces:**
- Consumes: `ModelPaths`, `ModelDescriptor`, `IModelSourceResolver` from Tasks 2 and 4.
- Produces: `DownloadProgress(string FileName, long Downloaded, long Total, double BytesPerSecond, int FileIndex, int FileCount)`; `ModelProvider(HttpClient http, IModelSourceResolver sources)` implementing `IModelProvider`.

- [ ] **Step 1: Write the failing test**

Rename the file and replace its contents with `MyASR.Tests/Models/ModelProviderTests.cs`:

```csharp
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
                paths, new Progress<DownloadProgress>(seen.Add), CancellationToken.None);

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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile errors — `The name 'ModelProvider' does not exist`, `'DownloadProgress' does not contain a definition for 'FileCount'`.

- [ ] **Step 3: Extend the progress record**

Replace the contents of `MyASR/Models/IModelProvider.cs`:

```csharp
namespace MyASR.Models;

/// <param name="FileIndex">Zero-based index of the file being fetched.</param>
/// <param name="FileCount">How many files this model needs in total.</param>
public sealed record DownloadProgress(
    string FileName,
    long Downloaded,
    long Total,
    double BytesPerSecond,
    int FileIndex,
    int FileCount);

public interface IModelProvider
{
    bool AreModelsAvailable(ModelPaths paths);

    Task EnsureDownloadedAsync(
        ModelPaths paths,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct);
}
```

- [ ] **Step 4: Write the provider**

Delete `MyASR/Models/HuggingFaceModelProvider.cs` and create `MyASR/Models/ModelProvider.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace MyASR.Models;

/// <summary>
/// Acquires a model's files from either an HTTP(S) base URI or a local folder / UNC path.
/// Both paths share the same staging, progress and digest semantics so they cannot drift apart.
/// </summary>
public sealed class ModelProvider(HttpClient http, IModelSourceResolver sources) : IModelProvider
{
    private const int BufferSize = 81920;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public bool AreModelsAvailable(ModelPaths paths)
    {
        foreach (var f in paths.Model.Files)
            if (!File.Exists(paths.File(f.LocalName))) return false;
        return true;
    }

    public async Task EnsureDownloadedAsync(
        ModelPaths paths, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Root);
        var baseUri = sources.ResolveBaseUri(paths.Model);
        var files = paths.Model.Files;

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var dst = paths.File(f.LocalName);
            if (File.Exists(dst)) continue;

            if (IsHttp(baseUri))
                await FetchOverHttpAsync(baseUri, f, dst, i, files.Count, progress, ct);
            else
                await CopyFromFolderAsync(baseUri, f, dst, i, files.Count, progress, ct);
        }
    }

    internal static bool IsHttp(string baseUri) =>
        Uri.TryCreate(baseUri, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    internal static string CombineHttp(string baseUri, string relative) =>
        new Uri(new Uri(baseUri.EndsWith('/') ? baseUri : baseUri + "/"), relative).ToString();

    internal static string CombineLocal(string baseDir, string relative) =>
        Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar));

    private async Task FetchOverHttpAsync(
        string baseUri, ModelFileEntry f, string dst, int index, int count,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var partial = dst + ".partial";
        long resumeFrom = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, CombineHttp(baseUri, f.SourceRelativePath));
        if (resumeFrom > 0) req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = (resp.Content.Headers.ContentLength ?? f.Size) + resumeFrom;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await PumpAsync(input, partial, dst, f, total, resumeFrom, index, count, progress, ct);
    }

    private static async Task CopyFromFolderAsync(
        string baseDir, ModelFileEntry f, string dst, int index, int count,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var source = CombineLocal(baseDir, f.SourceRelativePath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"Файл модели не найден в источнике: {source}", source);

        var partial = dst + ".partial";
        if (File.Exists(partial)) File.Delete(partial); // a local copy is cheap; always start clean

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await PumpAsync(input, partial, dst, f, input.Length, 0, index, count, progress, ct);
    }

    private static async Task PumpAsync(
        Stream input, string partial, string dst, ModelFileEntry f,
        long total, long resumeFrom, int index, int count,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            output.Position = resumeFrom;

            var sw = Stopwatch.StartNew();
            var buffer = new byte[BufferSize];
            var downloaded = resumeFrom;
            var lastReportedAt = downloaded;
            var lastReportTime = sw.Elapsed;

            int n;
            while ((n = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                downloaded += n;

                var now = sw.Elapsed;
                if (progress is not null && now - lastReportTime >= ProgressInterval)
                {
                    var bps = (downloaded - lastReportedAt) / (now - lastReportTime).TotalSeconds;
                    progress.Report(new DownloadProgress(f.LocalName, downloaded, total, bps, index, count));
                    lastReportedAt = downloaded;
                    lastReportTime = now;
                }
            }

            await output.FlushAsync(ct);
        }

        if (f.Sha256 is not null && !VerifySha(partial, f.Sha256))
        {
            File.Delete(partial);
            throw new InvalidDataException($"SHA-256 mismatch for {f.LocalName}");
        }

        File.Move(partial, dst, overwrite: true);
        progress?.Report(new DownloadProgress(f.LocalName, total, total, 0, index, count));
    }

    private static bool VerifySha(string path, string expectedHex)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        var actual = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        return actual.Equals(expectedHex.ToLowerInvariant(), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 5: Update DI registration**

In `MyASR/Program.cs`, replace the provider registrations (the `HttpClient`, manifest and `IModelProvider` lines):

```csharp
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IModelSourceResolver>(_ =>
            new ModelSourceResolver(UserSettings.Load().ModelSources));
        services.AddSingleton<IModelProvider, ModelProvider>();
```

`UserSettings.ModelSources` does not exist yet — it is added in Task 7. Until then, use `new ModelSourceResolver(null)`:

```csharp
        services.AddSingleton<IModelSourceResolver>(_ => new ModelSourceResolver(null));
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS, including the eleven `ModelProviderTests`.

- [ ] **Step 7: Commit**

```bash
git add MyASR/Models MyASR/Program.cs MyASR.Tests/Models
git commit -m "feat(models): fetch models over HTTP or from a local folder behind one provider"
```

---

### Task 6: IAsrEngine and hot model switching

**Files:**
- Create: `MyASR/Asr/IAsrEngine.cs`
- Create: `MyASR/Asr/GigaAmRnnTEngine.cs`
- Modify: `MyASR/Asr/IAsrService.cs`
- Modify: `MyASR/Asr/AsrService.cs` (full rewrite)
- Modify: `MyASR/Program.cs:36-49`
- Modify: `MyASR.Tests/Asr/AsrServiceTests.cs` (full rewrite)
- Test: `MyASR.Tests/Asr/AsrServiceTests.cs`

**Interfaces:**
- Consumes: `ModelDescriptor`, `ModelCatalog`, `ModelPaths`, `GigaAmFiles` from Tasks 1 and 2.
- Produces: `IAsrEngine.Transcribe(ReadOnlySpan<float> pcm16kMono, TimeSpan audioDuration) -> AsrResult`; `GigaAmRnnTEngine(IFeatureExtractor features, IRnnTDecoder decoder)` and `GigaAmRnnTEngine.Create(ModelPaths paths)`; `IAsrService.CurrentModel` and `IAsrService.SwitchModelAsync(ModelDescriptor, CancellationToken)`; `AsrService(IMicCapture mic, Func<ModelDescriptor, IAsrEngine> engineFactory, ModelDescriptor initialModel)`.

- [ ] **Step 1: Write the failing test**

Replace the contents of `MyASR.Tests/Asr/AsrServiceTests.cs`:

```csharp
using MyASR.Asr;
using MyASR.Audio;
using MyASR.Models;

namespace MyASR.Tests.Asr;

internal sealed class FakeMic : IMicCapture
{
    public List<float> Pcm { get; } = [];
    public bool Started { get; private set; }
    public double CurrentRmsDb => -30.0;
    public IReadOnlyList<DeviceInfo> EnumerateDevices() => [new("0", "fake")];
    public void Start(string deviceId) { Started = true; }
    public float[] StopAndDrain() { Started = false; return Pcm.ToArray(); }
    public void Dispose() { }
}

internal sealed class FakeEngine(string text = "привет") : IAsrEngine
{
    public bool Disposed { get; private set; }
    public int Calls { get; private set; }

    public AsrResult Transcribe(ReadOnlySpan<float> pcm, TimeSpan dur)
    {
        Calls++;
        return new AsrResult(text, dur, TimeSpan.FromMilliseconds(1));
    }

    public void Dispose() => Disposed = true;
}

internal sealed class ThrowingEngine : IAsrEngine
{
    public AsrResult Transcribe(ReadOnlySpan<float> pcm, TimeSpan dur) =>
        throw new InvalidOperationException("boom");
    public void Dispose() { }
}

public class AsrServiceTests
{
    private static readonly ModelDescriptor ModelA = ModelCatalog.GigaAmV3;
    private static readonly ModelDescriptor ModelB =
        ModelCatalog.GigaAmV3 with { Id = "model-b", FolderName = "model-b" };

    private static AsrService Build(IMicCapture mic, Func<ModelDescriptor, IAsrEngine> factory) =>
        new(mic, factory, ModelA);

    [Test]
    public async Task StartCapture_TransitionsToRecording()
    {
        var svc = Build(new FakeMic(), _ => new FakeEngine());
        svc.StartCapture("0");
        await Assert.That(svc.State).IsEqualTo(AsrState.Recording);
    }

    [Test]
    public async Task StopAndRecognizeAsync_RaisesResultReady_AndReturnsToIdle()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[16000]);
        var svc = Build(mic, _ => new FakeEngine());
        AsrResult? captured = null;
        svc.ResultReady += r => captured = r;

        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Text).IsEqualTo("привет");
        await Assert.That(svc.State).IsEqualTo(AsrState.Idle);
    }

    [Test]
    public async Task StopAndRecognizeAsync_TooShort_DoesNotCallEngine()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[1600]);
        var engine = new FakeEngine();
        var svc = Build(mic, _ => engine);
        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(engine.Calls).IsEqualTo(0);
        await Assert.That(svc.State).IsEqualTo(AsrState.Idle);
    }

    [Test]
    public async Task EngineThrows_RaisesFailed_AndReturnsToIdle()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[16000]);
        var svc = Build(mic, _ => new ThrowingEngine());
        Exception? captured = null;
        svc.Failed += e => captured = e;

        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(captured).IsNotNull();
        await Assert.That(svc.State).IsEqualTo(AsrState.Idle);
    }

    [Test]
    public async Task WarmupAsync_BuildsEngineOnce()
    {
        var calls = 0;
        var svc = Build(new FakeMic(), _ => { calls++; return new FakeEngine(); });
        await svc.WarmupAsync();
        await svc.WarmupAsync();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task StartCapture_WhileRecording_IsNoOp()
    {
        var svc = Build(new FakeMic(), _ => new FakeEngine());
        svc.StartCapture("0");
        svc.StartCapture("0");
        await Assert.That(svc.State).IsEqualTo(AsrState.Recording);
    }

    [Test]
    public async Task CurrentModel_StartsAtTheInitialDescriptor()
    {
        var svc = Build(new FakeMic(), _ => new FakeEngine());
        await Assert.That(svc.CurrentModel.Id).IsEqualTo(ModelA.Id);
    }

    [Test]
    public async Task SwitchModelAsync_DisposesTheOldEngine()
    {
        var built = new List<FakeEngine>();
        var svc = Build(new FakeMic(), _ => { var e = new FakeEngine(); built.Add(e); return e; });

        await svc.WarmupAsync();
        await svc.SwitchModelAsync(ModelB, CancellationToken.None);

        await Assert.That(built.Count).IsEqualTo(2);
        await Assert.That(built[0].Disposed).IsTrue();
        await Assert.That(built[1].Disposed).IsFalse();
        await Assert.That(svc.CurrentModel.Id).IsEqualTo(ModelB.Id);
    }

    [Test]
    public async Task SwitchModelAsync_PassesTheNewDescriptorToTheFactory()
    {
        var requested = new List<string>();
        var svc = Build(new FakeMic(), d => { requested.Add(d.Id); return new FakeEngine(); });

        await svc.WarmupAsync();
        await svc.SwitchModelAsync(ModelB, CancellationToken.None);

        await Assert.That(requested).IsEquivalentTo(new[] { ModelA.Id, ModelB.Id });
    }

    [Test]
    public async Task RecognitionAfterSwitch_UsesTheNewEngine()
    {
        var mic = new FakeMic();
        mic.Pcm.AddRange(new float[16000]);
        var svc = Build(mic, d => new FakeEngine(d.Id == ModelB.Id ? "новая модель" : "старая модель"));

        await svc.WarmupAsync();
        await svc.SwitchModelAsync(ModelB, CancellationToken.None);

        AsrResult? captured = null;
        svc.ResultReady += r => captured = r;
        svc.StartCapture("0");
        await svc.StopAndRecognizeAsync();

        await Assert.That(captured!.Text).IsEqualTo("новая модель");
    }

    [Test]
    public async Task SwitchModelAsync_WhileRecording_Throws_AndKeepsTheOldModel()
    {
        var svc = Build(new FakeMic(), _ => new FakeEngine());
        await svc.WarmupAsync();
        svc.StartCapture("0");

        await Assert.That(async () => await svc.SwitchModelAsync(ModelB, CancellationToken.None))
            .Throws<InvalidOperationException>();
        await Assert.That(svc.CurrentModel.Id).IsEqualTo(ModelA.Id);
    }

    [Test]
    public async Task SwitchModelAsync_FactoryThrows_KeepsThePreviousModelAsCurrent()
    {
        var svc = Build(new FakeMic(), d =>
            d.Id == ModelB.Id ? throw new FileNotFoundException("нет файла модели") : new FakeEngine());

        await svc.WarmupAsync();

        await Assert.That(async () => await svc.SwitchModelAsync(ModelB, CancellationToken.None))
            .Throws<FileNotFoundException>();
        await Assert.That(svc.CurrentModel.Id).IsEqualTo(ModelA.Id);
    }

    [Test]
    public async Task SwitchModelAsync_FactoryThrows_ThenRetryingThePreviousModelRebuildsIt()
    {
        var built = 0;
        var svc = Build(new FakeMic(), d =>
        {
            if (d.Id == ModelB.Id) throw new FileNotFoundException("нет файла модели");
            built++;
            return new FakeEngine();
        });

        await svc.WarmupAsync();
        try { await svc.SwitchModelAsync(ModelB, CancellationToken.None); }
        catch (FileNotFoundException) { /* expected */ }

        await svc.WarmupAsync(); // the failed switch disposed the old engine, so this rebuilds

        await Assert.That(built).IsEqualTo(2);
        await Assert.That(svc.CurrentModel.Id).IsEqualTo(ModelA.Id);
    }

    [Test]
    public async Task SwitchModelAsync_ToTheSameModel_KeepsTheWarmEngine()
    {
        var calls = 0;
        var svc = Build(new FakeMic(), _ => { calls++; return new FakeEngine(); });

        await svc.WarmupAsync();
        await svc.SwitchModelAsync(ModelA, CancellationToken.None);

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Dispose_DisposesTheEngine()
    {
        FakeEngine? engine = null;
        var svc = Build(new FakeMic(), _ => engine = new FakeEngine());
        await svc.WarmupAsync();

        svc.Dispose();

        await Assert.That(engine!.Disposed).IsTrue();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile errors — `The type or namespace name 'IAsrEngine' could not be found`, `AsrService does not contain a constructor that takes 3 arguments`.

- [ ] **Step 3: Write the engine abstraction**

Create `MyASR/Asr/IAsrEngine.cs`:

```csharp
namespace MyASR.Asr;

/// <summary>
/// One complete recognition pipeline. Feature extraction and decoding live behind the same
/// interface on purpose: they are model-specific and a mismatched pair produces plausible
/// nonsense rather than an error.
/// </summary>
public interface IAsrEngine : IDisposable
{
    AsrResult Transcribe(ReadOnlySpan<float> pcm16kMono, TimeSpan audioDuration);
}
```

Create `MyASR/Asr/GigaAmRnnTEngine.cs`:

```csharp
using MyASR.Features;
using MyASR.Models;

namespace MyASR.Asr;

/// <summary>GigaAM v3: 64-mel HTK log-mel features into a greedy RNN-T decode.</summary>
public sealed class GigaAmRnnTEngine(IFeatureExtractor features, IRnnTDecoder decoder) : IAsrEngine
{
    public static GigaAmRnnTEngine Create(ModelPaths paths)
    {
        var vocab = Vocabulary.Load(paths.File(GigaAmFiles.Vocab));
        var decoder = new RnnTGreedyDecoder(
            paths.File(GigaAmFiles.Encoder),
            paths.File(GigaAmFiles.Decoder),
            paths.File(GigaAmFiles.Joint),
            vocab);
        return new GigaAmRnnTEngine(new MelSpectrogramExtractor(), decoder);
    }

    public AsrResult Transcribe(ReadOnlySpan<float> pcm16kMono, TimeSpan audioDuration) =>
        decoder.Decode(features.Extract(pcm16kMono), audioDuration);

    public void Dispose()
    {
        decoder.Dispose();
        (features as IDisposable)?.Dispose();
    }
}
```

- [ ] **Step 4: Rewrite the service**

Replace the contents of `MyASR/Asr/IAsrService.cs`:

```csharp
using MyASR.Models;

namespace MyASR.Asr;

public enum AsrState { Idle, Recording, Decoding }

public sealed record AsrStateChanged(AsrState State);

public interface IAsrService
{
    AsrState State { get; }
    ModelDescriptor CurrentModel { get; }

    event Action<AsrStateChanged>? StateChanged;
    event Action<AsrResult>? ResultReady;
    event Action<Exception>? Failed;

    void StartCapture(string deviceId);
    Task StopAndRecognizeAsync();
    Task WarmupAsync();

    /// <summary>
    /// Disposes the current engine and warms up the new model's engine.
    /// Throws <see cref="InvalidOperationException"/> unless the service is idle.
    /// </summary>
    Task SwitchModelAsync(ModelDescriptor model, CancellationToken ct);
}
```

Replace the contents of `MyASR/Asr/AsrService.cs`:

```csharp
using MyASR.Audio;
using MyASR.Models;

namespace MyASR.Asr;

public sealed class AsrService(
    IMicCapture mic,
    Func<ModelDescriptor, IAsrEngine> engineFactory,
    ModelDescriptor initialModel)
    : IAsrService, IDisposable
{
    private const int MinSamples = 16000 * 3 / 10; // 0.3 sec @ 16 kHz

    private IAsrEngine? engine;

    public AsrState State { get; private set; } = AsrState.Idle;

    public ModelDescriptor CurrentModel { get; private set; } = initialModel;

    public event Action<AsrStateChanged>? StateChanged;
    public event Action<AsrResult>? ResultReady;
    public event Action<Exception>? Failed;

    public void StartCapture(string deviceId)
    {
        if (State != AsrState.Idle) return;
        mic.Start(deviceId);
        SetState(AsrState.Recording);
    }

    public Task WarmupAsync()
    {
        var model = CurrentModel;
        return Task.Run(() => engine ??= engineFactory(model));
    }

    public async Task SwitchModelAsync(ModelDescriptor model, CancellationToken ct)
    {
        if (State != AsrState.Idle)
            throw new InvalidOperationException("Модель нельзя менять во время записи или распознавания.");

        if (model.Id == CurrentModel.Id && engine is not null) return;

        // Drop the old engine before building the new one: two loaded ONNX models
        // would double peak memory, and a stale cached engine is the failure mode
        // this method exists to prevent.
        var previousEngine = engine;
        engine = null;
        previousEngine?.Dispose();

        // CurrentModel moves only after the factory succeeds. Advancing it first would leave
        // the service pointing at a model it failed to load, and the next recording would try
        // to build that same broken model through the ??= in StopAndRecognizeAsync.
        var built = await Task.Run(() => engineFactory(model), ct);
        engine = built;
        CurrentModel = model;
    }

    public async Task StopAndRecognizeAsync()
    {
        if (State != AsrState.Recording) return;
        var pcm = mic.StopAndDrain();
        if (pcm.Length < MinSamples) { SetState(AsrState.Idle); return; }

        SetState(AsrState.Decoding);
        try
        {
            var model = CurrentModel;
            var result = await Task.Run(() =>
            {
                engine ??= engineFactory(model);
                return engine.Transcribe(pcm, TimeSpan.FromSeconds(pcm.Length / 16000.0));
            });
            ResultReady?.Invoke(result);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            SetState(AsrState.Idle);
        }
    }

    private void SetState(AsrState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(new AsrStateChanged(s));
    }

    public void Dispose()
    {
        mic.Dispose();
        engine?.Dispose();
    }
}
```

- [ ] **Step 5: Rewire DI**

In `MyASR/Program.cs`, replace the `Func<IRnnTDecoder>` registration and the `IAsrService` registration with an engine factory:

```csharp
        // Engines are built lazily because they require model files on disk.
        services.AddSingleton<Func<ModelDescriptor, IAsrEngine>>(sp =>
        {
            var pathsFor = sp.GetRequiredService<Func<ModelDescriptor, ModelPaths>>();
            return descriptor => descriptor.Kind switch
            {
                AsrEngineKind.RnnT => GigaAmRnnTEngine.Create(pathsFor(descriptor)),
                _ => throw new NotSupportedException($"Движок {descriptor.Kind} ещё не реализован.")
            };
        });

        services.AddSingleton<IAsrService>(sp => new AsrService(
            sp.GetRequiredService<IMicCapture>(),
            sp.GetRequiredService<Func<ModelDescriptor, IAsrEngine>>(),
            ModelCatalog.Default));
```

Remove the now-unused `services.AddSingleton<IFeatureExtractor, MelSpectrogramExtractor>();` line — `GigaAmRnnTEngine.Create` builds its own extractor. Keep the `using MyASR.Features;` only if other code still needs it; if the compiler warns it is unused, delete it.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS, including the fifteen `AsrServiceTests`.

- [ ] **Step 7: Commit**

```bash
git add MyASR/Asr MyASR/Program.cs MyASR.Tests/Asr/AsrServiceTests.cs
git commit -m "feat(asr): replace the extractor+decoder pair with a swappable IAsrEngine"
```

---

### Task 7: Persist the selected model and its source overrides

**Files:**
- Modify: `MyASR/Settings.cs`
- Modify: `MyASR/Program.cs` (source resolver registration from Task 5)
- Test: `MyASR.Tests/SettingsTests.cs`

**Interfaces:**
- Consumes: `ModelCatalog` from Task 1.
- Produces: `UserSettings.ModelId` (string, defaults to `"gigaam-v3-rnnt"`); `UserSettings.ModelSources` (`Dictionary<string, string>`, defaults to empty).

- [ ] **Step 1: Write the failing test**

Create `MyASR.Tests/SettingsTests.cs`:

```csharp
using System.Text.Json;
using MyASR;
using MyASR.Models;

namespace MyASR.Tests;

public class SettingsTests
{
    [Test]
    public async Task Defaults_SelectTheCatalogueDefaultModel()
    {
        var s = new UserSettings();
        await Assert.That(s.ModelId).IsEqualTo(ModelCatalog.Default.Id);
        await Assert.That(s.ModelSources).IsEmpty();
    }

    [Test]
    public async Task ModelId_RoundTripsThroughJson()
    {
        var s = new UserSettings { ModelId = "crisperwhisper2-small-int8" };
        var back = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(s))!;
        await Assert.That(back.ModelId).IsEqualTo("crisperwhisper2-small-int8");
    }

    [Test]
    public async Task ModelSources_RoundTripThroughJson()
    {
        var s = new UserSettings();
        s.ModelSources["some-model"] = @"D:\models\some-model";
        var back = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(s))!;
        await Assert.That(back.ModelSources["some-model"]).IsEqualTo(@"D:\models\some-model");
    }

    [Test]
    public async Task OldSettingsFile_WithoutModelFields_DeserializesToDefaults()
    {
        const string legacy = """{"MicDeviceId":"abc","Hotkey":"Ctrl+Shift+X","GlobalHotkey":true}""";
        var s = JsonSerializer.Deserialize<UserSettings>(legacy)!;
        await Assert.That(s.ModelId).IsEqualTo(ModelCatalog.Default.Id);
        await Assert.That(s.ModelSources).IsEmpty();
        await Assert.That(s.MicDeviceId).IsEqualTo("abc");
    }

    [Test]
    public async Task UnknownModelId_ResolvesToTheDefaultDescriptor()
    {
        var s = new UserSettings { ModelId = "was-removed-in-a-later-release" };
        await Assert.That(ModelCatalog.ById(s.ModelId).Id).IsEqualTo(ModelCatalog.Default.Id);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile error — `'UserSettings' does not contain a definition for 'ModelId'`.

- [ ] **Step 3: Write the implementation**

In `MyASR/Settings.cs`, add two properties after `GlobalHotkey` and a `using`:

```csharp
using System.Text.Json;
using MyASR.Models;

namespace MyASR;

public sealed class UserSettings
{
    public string? MicDeviceId { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+X";
    public bool GlobalHotkey { get; set; } = true;

    /// <summary>Id of the selected <see cref="ModelDescriptor"/>.</summary>
    public string ModelId { get; set; } = ModelCatalog.Default.Id;

    /// <summary>Per-model base URI or folder override, keyed by model id.</summary>
    public Dictionary<string, string> ModelSources { get; set; } = [];
```

The rest of the file is unchanged.

- [ ] **Step 4: Feed the settings into the source resolver**

In `MyASR/Program.cs`, replace the placeholder registration from Task 5:

```csharp
        services.AddSingleton<IModelSourceResolver>(_ =>
            new ModelSourceResolver(UserSettings.Load().ModelSources));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add MyASR/Settings.cs MyASR/Program.cs MyASR.Tests/SettingsTests.cs
git commit -m "feat(settings): persist the selected model id and per-model source overrides"
```

---

### Task 8: DownloadForm per model descriptor

**Files:**
- Modify: `MyASR/DownloadForm.cs` (full rewrite)
- Modify: `MyASR/DownloadForm.Designer.cs:70` (window title)
- Modify: `MyASR/Program.cs` (registration)
- Test: `MyASR.Tests/Models/DownloadProgressFormattingTests.cs`

**Interfaces:**
- Consumes: `ModelDescriptor`, `ModelPaths`, `IModelProvider`, `DownloadProgress` from Tasks 1, 2 and 5.
- Produces: `DownloadForm(IModelProvider provider, ModelPaths paths, ILogger<DownloadForm>? logger = null)` where `paths.Model` supplies the descriptor; `DownloadProgressFormatter.Line(ModelDescriptor, DownloadProgress) -> string`; `DownloadProgressFormatter.Percent(DownloadProgress) -> int`; registration `Func<ModelDescriptor, DownloadForm>`.

The formatting logic is extracted into a static class precisely so it can be tested without a
message loop — WinForms code itself stays untested, as in the rest of this project.

- [ ] **Step 1: Write the failing test**

Create `MyASR.Tests/Models/DownloadProgressFormattingTests.cs`:

```csharp
using MyASR;
using MyASR.Models;

namespace MyASR.Tests.Models;

public class DownloadProgressFormattingTests
{
    private static readonly ModelDescriptor Model = ModelCatalog.GigaAmV3;

    [Test]
    public async Task Line_ShowsModelName_FileName_AndMegabytes()
    {
        var p = new DownloadProgress("v3_rnnt_encoder.onnx", 50_000_000, 928_000_000, 0, 0, 4);
        var line = DownloadProgressFormatter.Line(Model, p);

        await Assert.That(line).Contains("GigaAM v3");
        await Assert.That(line).Contains("v3_rnnt_encoder.onnx");
        await Assert.That(line).Contains("50");
        await Assert.That(line).Contains("928");
    }

    [Test]
    public async Task Line_ShowsFilePositionWithinTheModel()
    {
        var p = new DownloadProgress("v3_vocab.txt", 100, 200, 0, 2, 4);
        await Assert.That(DownloadProgressFormatter.Line(Model, p)).Contains("3/4");
    }

    [Test]
    public async Task Percent_IsOverallAcrossFiles_NotPerFile()
    {
        // Second of four files, half done → (1 + 0.5) / 4 = 37.5% → 37
        var p = new DownloadProgress("f", 5, 10, 0, 1, 4);
        await Assert.That(DownloadProgressFormatter.Percent(p)).IsEqualTo(37);
    }

    [Test]
    public async Task Percent_LastFileComplete_Is100()
    {
        var p = new DownloadProgress("f", 10, 10, 0, 3, 4);
        await Assert.That(DownloadProgressFormatter.Percent(p)).IsEqualTo(100);
    }

    [Test]
    public async Task Percent_ZeroTotal_DoesNotDivideByZero()
    {
        var p = new DownloadProgress("f", 0, 0, 0, 0, 1);
        await Assert.That(DownloadProgressFormatter.Percent(p)).IsEqualTo(0);
    }

    [Test]
    public async Task Percent_IsAlwaysClampedToZeroHundred()
    {
        var p = new DownloadProgress("f", 999, 10, 0, 3, 4);
        await Assert.That(DownloadProgressFormatter.Percent(p)).IsEqualTo(100);
    }

    [Test]
    public async Task Speed_ZeroBytesPerSecond_RendersEmpty()
    {
        var p = new DownloadProgress("f", 1, 2, 0, 0, 1);
        await Assert.That(DownloadProgressFormatter.Speed(p)).IsEqualTo("");
    }

    [Test]
    public async Task Speed_NonZero_RendersMegabytesPerSecond()
    {
        var p = new DownloadProgress("f", 1, 2, 12_300_000, 0, 1);
        await Assert.That(DownloadProgressFormatter.Speed(p)).IsEqualTo("12,3 MB/s");
    }
}
```

Note: `Speed` formats with the invariant culture's decimal separator replaced by a comma is
**not** what the implementation does — it uses `CultureInfo.CurrentCulture`. On a Russian
Windows the separator is a comma. If the test host runs under an English culture this test
fails, so the implementation pins the culture explicitly; see Step 3.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile error — `The name 'DownloadProgressFormatter' does not exist in the current context`.

- [ ] **Step 3: Write the formatter**

Create `MyASR/Models/DownloadProgressFormatter.cs`:

```csharp
using System.Globalization;

namespace MyASR.Models;

/// <summary>Turns a <see cref="DownloadProgress"/> into the strings the download dialog shows.</summary>
public static class DownloadProgressFormatter
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Line(ModelDescriptor model, DownloadProgress p) =>
        $"{model.DisplayName} — {p.FileName} ({p.FileIndex + 1}/{p.FileCount}): " +
        $"{p.Downloaded / 1_000_000} / {p.Total / 1_000_000} MB";

    /// <summary>Overall percentage across the whole model, not just the current file.</summary>
    public static int Percent(DownloadProgress p)
    {
        if (p.FileCount <= 0) return 0;
        var withinFile = p.Total > 0 ? Math.Clamp(p.Downloaded / (double)p.Total, 0, 1) : 0;
        var overall = (p.FileIndex + withinFile) / p.FileCount;
        return (int)Math.Clamp(Math.Round(overall * 100, MidpointRounding.ToZero), 0, 100);
    }

    public static string Speed(DownloadProgress p)
    {
        var mbPerSec = p.BytesPerSecond / 1_000_000.0;
        return mbPerSec > 0 ? string.Format(Ru, "{0:F1} MB/s", mbPerSec) : "";
    }
}
```

- [ ] **Step 4: Rewrite the dialog to take a descriptor**

Replace the contents of `MyASR/DownloadForm.cs`:

```csharp
using Microsoft.Extensions.Logging;
using MyASR.Models;

namespace MyASR;

public partial class DownloadForm : Form
{
    private readonly IModelProvider provider;
    private readonly ModelPaths paths;
    private readonly ILogger<DownloadForm>? logger;
    private CancellationTokenSource? cts;

    // Дизайнер WinForms требует parameterless-конструктор для рендера формы.
    public DownloadForm()
    {
        InitializeComponent();
        provider = null!; paths = null!;
    }

    public DownloadForm(IModelProvider provider, ModelPaths paths, ILogger<DownloadForm>? logger = null)
        : this()
    {
        this.provider = provider; this.paths = paths; this.logger = logger;
        Text = $"Загрузка модели — {paths.Model.DisplayName}";
        _retry.Click += async (_, _) => await StartDownloadAsync();
        Load += async (_, _) => await StartDownloadAsync();
        FormClosing += (_, _) => cts?.Cancel();
    }

    public bool DownloadSucceeded { get; private set; }

    private async Task StartDownloadAsync()
    {
        _retry.Visible = false;
        _progress.Value = 0;
        _label.Text = $"Скачивание модели «{paths.Model.DisplayName}»...";
        _detail.Text = paths.Model.LicenceNote ?? "";

        cts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(OnProgress);
        try
        {
            await provider.EnsureDownloadedAsync(paths, progress, cts.Token);
            DownloadSucceeded = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            DialogResult = DialogResult.Cancel;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Model download failed for {ModelId}", paths.Model.Id);
            _label.Text = "Ошибка загрузки. Проверьте источник и нажмите «Повторить».";
            _detail.Text = ex.Message;
            _retry.Visible = true;
        }
    }

    private void OnProgress(DownloadProgress p)
    {
        if (InvokeRequired) { BeginInvoke(() => OnProgress(p)); return; }
        _label.Text = DownloadProgressFormatter.Line(paths.Model, p);
        _progress.Value = DownloadProgressFormatter.Percent(p);
        _detail.Text = DownloadProgressFormatter.Speed(p);
    }
}
```

In `MyASR/DownloadForm.Designer.cs` line 70, change the designer-time caption so it no longer
claims to load every model:

```csharp
        Text = "Загрузка модели";
```

- [ ] **Step 5: Register a descriptor-aware factory**

In `MyASR/Program.cs`, replace `services.AddTransient<DownloadForm>();`:

```csharp
        services.AddSingleton<Func<ModelDescriptor, DownloadForm>>(sp => descriptor => new DownloadForm(
            sp.GetRequiredService<IModelProvider>(),
            sp.GetRequiredService<Func<ModelDescriptor, ModelPaths>>()(descriptor),
            sp.GetService<ILogger<DownloadForm>>()));
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS, including the eight `DownloadProgressFormattingTests`.

- [ ] **Step 7: Commit**

```bash
git add MyASR/DownloadForm.cs MyASR/DownloadForm.Designer.cs MyASR/Models/DownloadProgressFormatter.cs MyASR/Program.cs MyASR.Tests/Models/DownloadProgressFormattingTests.cs
git commit -m "feat(ui): download one selected model instead of an ambient manifest"
```

---

### Task 9: Model combo box and switching in the main window

**Files:**
- Modify: `MyASR/Main.Designer.cs`
- Modify: `MyASR/Main.cs`
- Modify: `MyASR/Program.cs`
- Test: `MyASR.Tests/ModelSelectionPolicyTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-8.
- Produces: `ModelSelectionPolicy.CanSwitch(AsrState) -> bool`; `ModelSelectionPolicy.StatusText(ModelDescriptor, bool hotkeyRegistered, string hotkey) -> string`; the `modelCombo` control on `Main`.

The switching *decision* lives in a testable static policy; the WinForms plumbing around it does not.

- [ ] **Step 1: Write the failing test**

Create `MyASR.Tests/ModelSelectionPolicyTests.cs`:

```csharp
using MyASR;
using MyASR.Asr;
using MyASR.Models;

namespace MyASR.Tests;

public class ModelSelectionPolicyTests
{
    [Test]
    public async Task CanSwitch_OnlyWhenIdle()
    {
        await Assert.That(ModelSelectionPolicy.CanSwitch(AsrState.Idle)).IsTrue();
        await Assert.That(ModelSelectionPolicy.CanSwitch(AsrState.Recording)).IsFalse();
        await Assert.That(ModelSelectionPolicy.CanSwitch(AsrState.Decoding)).IsFalse();
    }

    [Test]
    public async Task StatusText_RegisteredHotkey_MentionsIt()
    {
        var text = ModelSelectionPolicy.StatusText(ModelCatalog.GigaAmV3, true, "Ctrl+Shift+X");
        await Assert.That(text).Contains("Ctrl+Shift+X");
        await Assert.That(text).Contains("GigaAM v3");
    }

    [Test]
    public async Task StatusText_UnregisteredHotkey_SaysUnavailable()
    {
        var text = ModelSelectionPolicy.StatusText(ModelCatalog.GigaAmV3, false, "Ctrl+Shift+X");
        await Assert.That(text).Contains("PTT недоступен");
    }

    [Test]
    public async Task StatusText_LicenceNote_IsShownWhenPresent()
    {
        var restricted = ModelCatalog.GigaAmV3 with { LicenceNote = "только для исследований" };
        var text = ModelSelectionPolicy.StatusText(restricted, true, "F9");
        await Assert.That(text).Contains("только для исследований");
    }

    [Test]
    public async Task StatusText_NoLicenceNote_OmitsTheParenthesisedNote()
    {
        var note = "только для исследований";
        var withNote = ModelSelectionPolicy.StatusText(
            ModelCatalog.GigaAmV3 with { LicenceNote = note }, true, "F9");
        var withoutNote = ModelSelectionPolicy.StatusText(ModelCatalog.GigaAmV3, true, "F9");

        await Assert.That(withNote).Contains($"({note})");
        await Assert.That(withoutNote).DoesNotContain(note);
        await Assert.That(withoutNote.Length).IsLessThan(withNote.Length);
    }

    [Test]
    public async Task StatusText_EmptyLicenceNote_IsTreatedAsAbsent()
    {
        var text = ModelSelectionPolicy.StatusText(
            ModelCatalog.GigaAmV3 with { LicenceNote = "" }, true, "F9");
        await Assert.That(text).IsEqualTo(
            ModelSelectionPolicy.StatusText(ModelCatalog.GigaAmV3, true, "F9"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: compile error — `The name 'ModelSelectionPolicy' does not exist in the current context`.

- [ ] **Step 3: Write the policy**

Create `MyASR/ModelSelectionPolicy.cs`:

```csharp
using MyASR.Asr;
using MyASR.Models;

namespace MyASR;

/// <summary>Decisions about model switching, kept out of the form so they can be tested.</summary>
public static class ModelSelectionPolicy
{
    public static bool CanSwitch(AsrState state) => state == AsrState.Idle;

    public static string StatusText(ModelDescriptor model, bool hotkeyRegistered, string hotkey)
    {
        var ptt = hotkeyRegistered ? $"PTT: {hotkey}" : "PTT недоступен — занят";
        var licence = model.LicenceNote is { Length: > 0 } note ? $" ({note})" : "";
        return $"● Idle — {model.DisplayName}{licence} [{ptt}]";
    }
}
```

- [ ] **Step 4: Add the combo box to the designer**

In `MyASR/Main.Designer.cs`, declare and lay out two new controls and shift every control below
them down by 29 pixels.

Add to the field declarations at the bottom of the class:

```csharp
    private Label modelDescriptionLabel;
    private ComboBox modelCombo;
```

Add to the top of `InitializeComponent`, next to the other `new` calls:

```csharp
        modelDescriptionLabel = new Label();
        modelCombo = new ComboBox();
```

Insert the two control blocks before the `_deviceLabel` block:

```csharp
        // 
        // modelDescriptionLabel
        // 
        modelDescriptionLabel.Location = new Point(16, 22);
        modelDescriptionLabel.Name = "modelDescriptionLabel";
        modelDescriptionLabel.Size = new Size(80, 16);
        modelDescriptionLabel.TabIndex = 0;
        modelDescriptionLabel.Text = "Модель:";
        // 
        // modelCombo
        // 
        modelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        modelCombo.Location = new Point(102, 19);
        modelCombo.Name = "modelCombo";
        modelCombo.Size = new Size(574, 23);
        modelCombo.TabIndex = 1;
```

Change the existing coordinates:

| Control | Old `Location` | New `Location` |
|---|---|---|
| `_deviceLabel` | `new Point(16, 22)` | `new Point(16, 51)` |
| `_deviceCombo` | `new Point(102, 19)` | `new Point(102, 48)` |
| `micButton` | `new Point(16, 51)` | `new Point(16, 80)` |
| `_vuMeter` | `new Point(186, 54)` | `new Point(186, 83)` |
| `resultBox` | `new Point(16, 93)` | `new Point(16, 122)` |
| `clearBtn` | `new Point(16, 377)` | `new Point(16, 406)` |
| `copyBtn` | `new Point(124, 377)` | `new Point(124, 406)` |
| `saveTxtBtn` | `new Point(252, 377)` | `new Point(252, 406)` |
| `statusStrip1` | `new Point(0, 422)` | `new Point(0, 451)` |

Bump the tab indices of `_deviceLabel` to `2` and `_deviceCombo` to `3`, change `ClientSize` to
`new Size(689, 473)`, and register the two new controls before `_deviceLabel`:

```csharp
        Controls.Add(modelDescriptionLabel);
        Controls.Add(modelCombo);
```

- [ ] **Step 5: Wire the combo into the form**

In `MyASR/Main.cs`, add `using MyASR.Models;` (already present) and a field plus a factory
dependency. Replace the field block and DI constructor signature:

```csharp
    private readonly IAsrService asr;
    private readonly IMicCapture mic;
    private readonly IModelProvider modelProvider;
    private readonly Func<ModelDescriptor, ModelPaths> pathsFor;
    private readonly Func<ModelDescriptor, DownloadForm> downloadFormFor;
    private readonly IHotkeyManager hotkeys;
    private readonly ILogger<Main>? logger;
    private readonly UserSettings settings = UserSettings.Load();

    private ModelDescriptor selectedModel = ModelCatalog.Default;
    private bool suppressModelComboEvent;
    private bool hotkeyRegistered;
```

The `IServiceProvider sp` field goes away with this change: its only use was
`sp.GetRequiredService<DownloadForm>()`, which `downloadFormFor` now replaces. Delete the field,
the constructor parameter and the `this.sp = sp;` assignment, and remove
`using Microsoft.Extensions.DependencyInjection;` if nothing else in the file needs it.

```csharp
    public Main(
        IAsrService asr,
        IMicCapture mic,
        IModelProvider modelProvider,
        Func<ModelDescriptor, ModelPaths> pathsFor,
        Func<ModelDescriptor, DownloadForm> downloadFormFor,
        IHotkeyManager hotkeys,
        ILogger<Main>? logger = null) : this()
    {
        this.asr = asr; this.mic = mic;
        this.modelProvider = modelProvider;
        this.pathsFor = pathsFor;
        this.downloadFormFor = downloadFormFor;
        this.hotkeys = hotkeys; this.logger = logger;
```

Delete the `ModelPaths paths` parameter and the `this.paths = paths;` assignment; also delete
`paths = null!;` from the parameterless constructor and add the two new nulls:

```csharp
    public Main()
    {
        InitializeComponent();
        asr = null!; mic = null!;
        modelProvider = null!; pathsFor = null!; downloadFormFor = null!; hotkeys = null!;
    }
```

Add the combo handler registration next to the other event wiring in the DI constructor:

```csharp
        modelCombo.SelectedIndexChanged += async (_, _) => await OnModelSelectedAsync();
```

Replace `OnFormShownAsync` with a version that populates the combo, migrates the legacy layout and
downloads the selected model:

```csharp
    private async Task OnFormShownAsync()
    {
        PopulateDevices();
        if (settings.MicDeviceId is { } savedId)
        {
            var devices = mic.EnumerateDevices();
            var idx = -1;
            for (var i = 0; i < devices.Count; i++)
                if (devices[i].Id == savedId) { idx = i; break; }
            if (idx >= 0) _deviceCombo.SelectedIndex = idx;
        }

        selectedModel = ModelCatalog.ById(settings.ModelId);
        PopulateModels();

        micButton.Enabled = false;

        LegacyModelLayout.Migrate(ModelRoot.Resolve(), ModelCatalog.GigaAmV3);

        if (!await EnsureModelPresentAsync(selectedModel))
        {
            MessageBox.Show("Модель не загружена — приложение будет закрыто.",
                            "MyASR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }

        if (!await WarmUpAsync(selectedModel, closeOnFailure: true)) return;

        hotkeyRegistered = hotkeys.Register(Handle, HotkeyId, _hotkeyMod, _hotkeyVk);
        if (!hotkeyRegistered)
        {
            logger?.LogWarning("Не удалось зарегистрировать глобальный хоткей {Hotkey}", settings.Hotkey);
            MessageBox.Show(
                $"Не удалось зарегистрировать глобальный хоткей '{settings.Hotkey}'.\n" +
                "Возможно, он уже занят другим приложением.\n" +
                "Кнопка микрофона на форме продолжит работать.",
                "MyASR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        hotkeyPoll.Start();
        micButton.Enabled = true;
        UpdateModelStatus();

        if (!StartupManager.IsEnabled())
            StartupManager.SetEnabled(true);
    }

    private void PopulateModels()
    {
        suppressModelComboEvent = true;
        modelCombo.DataSource = ModelCatalog.All.ToList();
        modelCombo.DisplayMember = nameof(ModelDescriptor.DisplayName);
        modelCombo.SelectedIndex = Math.Max(0, ModelCatalog.All.ToList().FindIndex(m => m.Id == selectedModel.Id));
        suppressModelComboEvent = false;
    }

    /// <returns>true when every file of <paramref name="model"/> is on disk afterwards.</returns>
    private async Task<bool> EnsureModelPresentAsync(ModelDescriptor model)
    {
        var paths = pathsFor(model);
        if (modelProvider.AreModelsAvailable(paths)) return true;

        statusLabel.Text = $"⏬ Загрузка модели «{model.DisplayName}»...";
        using var dlg = downloadFormFor(model);
        var ok = dlg.ShowDialog(this) == DialogResult.OK;
        await Task.Yield(); // let the dialog finish tearing down before the caller continues
        return ok;
    }

    private async Task<bool> WarmUpAsync(ModelDescriptor model, bool closeOnFailure)
    {
        statusLabel.Text = $"⏳ Загрузка «{model.DisplayName}» в память...";
        UseWaitCursor = true;
        Refresh();
        try
        {
            if (asr.CurrentModel.Id == model.Id)
                await asr.WarmupAsync();
            else
                await asr.SwitchModelAsync(model, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Model warmup failed for {ModelId}", model.Id);
            MessageBox.Show($"Не удалось загрузить модель «{model.DisplayName}»:\n{ex.Message}",
                            "MyASR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (closeOnFailure) Close();
            return false;
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async Task OnModelSelectedAsync()
    {
        if (suppressModelComboEvent) return;
        if (modelCombo.SelectedItem is not ModelDescriptor chosen) return;
        if (chosen.Id == selectedModel.Id) return;

        if (!ModelSelectionPolicy.CanSwitch(asr.State))
        {
            statusLabel.Text = "Модель нельзя менять во время записи или распознавания.";
            RevertModelCombo();
            return;
        }

        var previous = selectedModel;
        micButton.Enabled = false;
        try
        {
            if (!await EnsureModelPresentAsync(chosen) || !await WarmUpAsync(chosen, closeOnFailure: false))
            {
                selectedModel = previous;
                RevertModelCombo();
                await WarmUpAsync(previous, closeOnFailure: false);
                return;
            }

            selectedModel = chosen;
            settings.ModelId = chosen.Id;
            settings.Save();
        }
        finally
        {
            micButton.Enabled = true;
            UpdateModelStatus();
        }
    }

    private void RevertModelCombo()
    {
        suppressModelComboEvent = true;
        modelCombo.SelectedIndex = Math.Max(0, ModelCatalog.All.ToList().FindIndex(m => m.Id == selectedModel.Id));
        suppressModelComboEvent = false;
    }

    private void UpdateModelStatus()
    {
        statusLabel.Text = ModelSelectionPolicy.StatusText(selectedModel, hotkeyRegistered, settings.Hotkey);
        _modelLabel.Text = selectedModel.DisplayName;
    }
```

In `OnStateChanged`, the `AsrState.Idle` arm must no longer clobber the model text:

```csharp
        if (s.State == AsrState.Idle) { UpdateModelStatus(); micButton.Enabled = true; return; }
        statusLabel.Text = s.State switch
        {
            AsrState.Recording => $"⏺ Recording {recordingTimer.Elapsed.TotalSeconds:F1}s",
            AsrState.Decoding => "⏳ Decoding...",
            _ => "?"
        };
        micButton.Enabled = s.State != AsrState.Decoding;
```

In `Cleanup`, persist the model id alongside the device:

```csharp
        settings.ModelId = selectedModel.Id;
```

- [ ] **Step 6: Update DI for the new Main constructor**

`Main` now takes `Func<ModelDescriptor, ModelPaths>` and `Func<ModelDescriptor, DownloadForm>`,
both already registered in Tasks 2 and 8, so `services.AddTransient<Main>();` keeps working.

Delete the `services.AddSingleton<ModelPaths>(...)` registration added in Task 2. Nothing resolves
a bare `ModelPaths` any more: the engine factory (Task 6), the download-form factory (Task 8) and
`Main` all go through `Func<ModelDescriptor, ModelPaths>`. Leaving it registered would quietly
hand out paths for the default model regardless of what the user picked.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test MyASR.Tests/MyASR.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Verify the application builds and starts**

Run: `dotnet build MyASR/MyASR.csproj -c Debug`
Expected: `Build succeeded` with zero errors.

Manual check (requires the GigaAM files already on disk from a previous run): launch the app and
confirm the model combo shows "GigaAM v3 (RU, RNN-T)", the status bar reads
`● Idle — GigaAM v3 (RU, RNN-T) [PTT: Ctrl+Shift+X]`, and push-to-talk still transcribes.

- [ ] **Step 9: Commit**

```bash
git add MyASR/Main.cs MyASR/Main.Designer.cs MyASR/ModelSelectionPolicy.cs MyASR/Program.cs MyASR.Tests/ModelSelectionPolicyTests.cs
git commit -m "feat(ui): choose the recognition model from the main window"
```

---

## Phase 1 Done When

- `dotnet test MyASR.Tests/MyASR.Tests.csproj` passes with no model files on disk.
- The app starts, shows a model combo with one entry, and transcribes as before.
- An existing installation's flat GigaAM files are found in `gigaam-v3/` after the first launch,
  with no re-download.
- Setting `MYASR_MODEL_SOURCE_GIGAAM_V3_RNNT` to a folder containing the four GigaAM files makes a
  fresh installation acquire them by copying instead of downloading.

Phase 2 (`docs/superpowers/plans/2026-08-02-crisperwhisper-engine.md`) adds the CrisperWhisper
catalogue entries and the Whisper pipeline behind the same `IAsrEngine`.
