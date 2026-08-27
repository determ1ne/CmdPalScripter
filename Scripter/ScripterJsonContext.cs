using System.Text.Json.Serialization;

namespace Scripter;

[JsonSerializable(typeof(string))]
internal sealed partial class ScripterJsonContext : JsonSerializerContext
{
}
