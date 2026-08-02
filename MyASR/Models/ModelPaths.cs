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
