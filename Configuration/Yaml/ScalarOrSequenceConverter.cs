using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace GitBackup.Configuration.Yaml;

/// <summary>
/// Reads a YAML value that may be either a single scalar or a sequence of scalars into a
/// <see cref="List{String}"/>. This lets a config key such as <c>url</c> accept one value
/// (<c>url: https://…</c>) or many (<c>url:</c> followed by a list) without a second key.
/// </summary>
public sealed class ScalarOrSequenceConverter : IYamlTypeConverter
{
    // Only List<string> is intercepted. No other List<string> is bound to YAML in the settings
    // graph (repositories is List<RepositoryJobConfig?>, credentials is a dictionary), so this
    // never changes how another field is parsed.
    public bool Accepts(Type type) => type == typeof(List<string>);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return new List<string> { scalar.Value };
        }

        var values = new List<string>();
        parser.Consume<SequenceStart>();
        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            values.Add(parser.Consume<Scalar>().Value);
        }

        return values;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        => throw new NotSupportedException("Settings are never serialized back to YAML.");
}
