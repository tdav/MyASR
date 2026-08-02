namespace MyASR.Models;

/// <summary>Decides where a model's files are fetched from: an HTTP base URI or a local folder.</summary>
public interface IModelSourceResolver
{
    string ResolveBaseUri(ModelDescriptor model);
}
