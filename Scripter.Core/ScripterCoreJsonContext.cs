using System.Text.Json.Serialization;

namespace Scripter.Core;

[JsonSerializable(typeof(ScriptMetadataFile))]
[JsonSerializable(typeof(ScriptPermissionStore))]
[JsonSerializable(typeof(ScriptPermissionApproval))]
internal sealed partial class ScripterCoreJsonContext : JsonSerializerContext
{
}
