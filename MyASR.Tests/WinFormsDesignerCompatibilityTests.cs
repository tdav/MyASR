using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using MyASR;

namespace MyASR.Tests;

public class WinFormsDesignerCompatibilityTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MyASR.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Не удалось найти корень репозитория MyASR.slnx.");
    }

    [Test]
    public async Task MyAsrCsproj_DoesNotForceUnconditionalPublishAotOrRuntimeIdentifierOnRegularBuilds()
    {
        var root = FindRepoRoot();
        var csprojPath = Path.Combine(root, "MyASR", "MyASR.csproj");
        var doc = XDocument.Load(csprojPath);

        var unconditionalGroups = doc.Root!
            .Elements("PropertyGroup")
            .Where(pg => pg.Attribute("Condition") is null)
            .ToList();

        var unconditionalPublishAot = unconditionalGroups
            .Elements("PublishAot")
            .Where(e => e.Attribute("Condition") is null)
            .Select(e => e.Value.Trim())
            .FirstOrDefault();

        var unconditionalRid = unconditionalGroups
            .Elements("RuntimeIdentifier")
            .Where(e => e.Attribute("Condition") is null)
            .Select(e => e.Value.Trim())
            .FirstOrDefault();

        await Assert.That(unconditionalPublishAot).IsNotEqualTo("true");
        await Assert.That(unconditionalRid).IsNull();

        var pubxmlPath = Path.Combine(root, "MyASR", "Properties", "PublishProfiles", "FolderProfile.pubxml");
        var pubDoc = XDocument.Load(pubxmlPath);
        var pubAot = pubDoc.Descendants("PublishAot").Select(e => e.Value.Trim()).FirstOrDefault();
        var pubRid = pubDoc.Descendants("RuntimeIdentifier").Select(e => e.Value.Trim()).FirstOrDefault();

        await Assert.That(pubAot).IsEqualTo("true");
        await Assert.That(pubRid).IsEqualTo("win-x64");
    }

    [Test]
    public async Task BuiltRuntimeConfigs_AllowWinFormsDesignToolsServerFeaturesInDebugAndRelease()
    {
        var root = FindRepoRoot();

        foreach (var cfg in new[] { "Debug", "Release" })
        {
            var staleRidRuntimeConfig = Path.Combine(
                root, "MyASR", "bin", cfg, "net11.0-windows", "win-x64", "MyASR.runtimeconfig.json");
            await Assert.That(File.Exists(staleRidRuntimeConfig)).IsFalse();
        }

#if DEBUG
        const string activeConfig = "Debug";
#else
        const string activeConfig = "Release";
#endif
        var runtimeConfigPath = Path.Combine(
            root, "MyASR", "bin", activeConfig, "net11.0-windows", "MyASR.runtimeconfig.json");

        await Assert.That(File.Exists(runtimeConfigPath)).IsTrue();
        await VerifyRuntimeConfigAllowsDesignerAsync(runtimeConfigPath);

        var otherConfig = activeConfig == "Debug" ? "Release" : "Debug";
        var otherRuntimeConfigPath = Path.Combine(
            root, "MyASR", "bin", otherConfig, "net11.0-windows", "MyASR.runtimeconfig.json");
        if (File.Exists(otherRuntimeConfigPath))
            await VerifyRuntimeConfigAllowsDesignerAsync(otherRuntimeConfigPath);
    }

    private static async Task VerifyRuntimeConfigAllowsDesignerAsync(string runtimeConfigPath)
    {
        using var stream = File.OpenRead(runtimeConfigPath);
        using var json = await JsonDocument.ParseAsync(stream);

        if (json.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions) &&
            runtimeOptions.TryGetProperty("configProperties", out var props))
        {
            string[] requiredForDesigner =
            [
                "System.StartupHookProvider.IsSupported",
                "System.ComponentModel.Design.IDesignerHost.IsSupported",
                "System.Windows.Forms.Control.AreDesignTimeFeaturesSupported",
                "System.Drawing.Design.UITypeEditor.IsSupported"
            ];

            foreach (var key in requiredForDesigner)
            {
                if (props.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.False)
                {
                    Assert.Fail($"Свойство '{key}' отключено (false) в {runtimeConfigPath}, из-за чего падает WinForms DesignToolsServer.");
                }
            }
        }
    }

    [Test]
    public async Task AllWinFormsInAssembly_HavePureDesignerFilesAndWorkingParameterlessConstructors()
    {
        var root = FindRepoRoot();
        var projectDir = Path.Combine(root, "MyASR");

        var formTypes = typeof(Main).Assembly
            .GetTypes()
            .Where(t => typeof(Form).IsAssignableFrom(t) && !t.IsAbstract)
            .OrderBy(t => t.Name)
            .ToList();

        // Must discover all three forms: DownloadForm, Main, SplashForm.
        await Assert.That(formTypes.Select(t => t.Name).ToArray())
            .IsEquivalentTo(["DownloadForm", "Main", "SplashForm"]);

        foreach (var formType in formTypes)
        {
            // 1. Must not be blocked from opening in Visual Studio WinForms Designer via DesignerCategory("Code").
            var categoryAttr = formType.GetCustomAttribute<DesignerCategoryAttribute>(inherit: false);
            if (categoryAttr is not null)
                await Assert.That(categoryAttr.Category).IsEqualTo("Form");

            // 2. Standard WinForms Designer convention: partial unsealed Form class.
            await Assert.That(formType.IsSealed).IsFalse();

            // 3. Must have a <FormName>.Designer.cs with pure InitializeComponent() (no AppIcon.Get()).
            var designerPath = Path.Combine(projectDir, $"{formType.Name}.Designer.cs");
            await Assert.That(File.Exists(designerPath)).IsTrue();

            var designerSource = await File.ReadAllTextAsync(designerPath);
            await Assert.That(designerSource.Contains("void InitializeComponent()")).IsTrue();
            await Assert.That(designerSource.Contains("AppIcon.Get")).IsFalse();

            // 4. Must have a public parameterless constructor that succeeds and initializes layout + Icon.
            var ctor = formType.GetConstructor(Type.EmptyTypes);
            await Assert.That(ctor).IsNotNull();

            using var instance = (Form)ctor!.Invoke(null);
            await Assert.That(instance.Icon).IsNotNull();
            await Assert.That(instance.ClientSize.Width).IsGreaterThan(100);
            await Assert.That(instance.ClientSize.Height).IsGreaterThan(80);
        }
    }
}
