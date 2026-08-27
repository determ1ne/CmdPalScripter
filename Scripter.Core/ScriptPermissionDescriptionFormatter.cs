using System.Text;

namespace Scripter.Core;

public static class ScriptPermissionDescriptionFormatter
{
    public static string BuildDescription(string scriptName, ScriptMetadata metadata)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(scriptName).Append("\" requests the following permissions:\n\n");
        builder.Append("- Script type: ").Append(metadata.Type).Append('\n');
        if (metadata.DynamicImport)
        {
            builder.Append("- Dynamic Library Import: This script can import and execute code from external libraries at runtime.\n");
        }

        if (metadata.CommandExecution)
        {
            builder.Append("- Command Execution: This script can run arbitrary system commands.\n");
        }

        if (metadata.NativeFfi)
        {
            builder.Append("- Native FFI: This script can load arbitrary DLLs and call unmanaged functions inside the Scripter process.\n");
        }

        if (metadata.NativeTypes.Count > 0)
        {
            builder.Append("- Native types: ").Append(string.Join(", ", metadata.NativeTypes.Select(type => type.TypeName))).Append('\n');
        }

        builder.Append("\nGranting these permissions allows the script to perform actions that may affect your system's security and stability. Only grant permissions to scripts from trusted sources.");
        builder.Append("\nApproval is revoked automatically if script content or permissions change.");
        return builder.ToString();
    }
}
