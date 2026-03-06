using System.Text.Json.Serialization;

namespace Scripter;

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ScriptMetadataFile))]
[JsonSerializable(typeof(ScriptPermissionStore))]
[JsonSerializable(typeof(ScriptPermissionApproval))]
internal sealed partial class ScripterJsonContext : JsonSerializerContext
{
}
