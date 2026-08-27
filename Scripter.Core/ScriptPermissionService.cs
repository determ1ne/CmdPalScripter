using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scripter.Core;

public sealed class ScriptPermissionService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PowerToys.CmdPal.Scripter.Permission.v1");
    private readonly string _approvalsPath;

    public ScriptPermissionService(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _approvalsPath = Path.Combine(Path.GetFullPath(rootDirectory), "script-permissions.bin");
    }

    public static bool RequiresPermission(ScriptMetadata metadata) =>
        metadata.DynamicImport || metadata.NativeTypes.Count > 0 || metadata.CommandExecution || metadata.NativeFfi;

    public bool IsApproved(string scriptPath, string scriptContent, ScriptMetadata metadata)
    {
        var store = LoadStore();
        var normalizedPath = Path.GetFullPath(scriptPath);
        var fingerprint = ComputeFingerprint(scriptContent, metadata);
        return store.Approvals.Any(approval =>
            string.Equals(approval.ScriptPath, normalizedPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(approval.Fingerprint, fingerprint, StringComparison.Ordinal));
    }

    public void Approve(string scriptPath, string scriptContent, ScriptMetadata metadata)
    {
        var store = LoadStore();
        var normalizedPath = Path.GetFullPath(scriptPath);
        var fingerprint = ComputeFingerprint(scriptContent, metadata);
        store.Approvals.RemoveAll(approval => string.Equals(approval.ScriptPath, normalizedPath, StringComparison.OrdinalIgnoreCase));
        store.Approvals.Add(new ScriptPermissionApproval
        {
            ScriptPath = normalizedPath,
            Fingerprint = fingerprint,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
        });
        SaveStore(store);
    }

    public static string ComputeFingerprint(string scriptContent, ScriptMetadata metadata)
    {
        var payload = new StringBuilder();
        payload.AppendLine(scriptContent);
        payload.Append("Type=").Append(metadata.Type).AppendLine();
        payload.Append("DynamicImport=").Append(metadata.DynamicImport ? "1" : "0").AppendLine();
        payload.Append("CommandExecution=").Append(metadata.CommandExecution ? "1" : "0").AppendLine();
        if (metadata.NativeFfi)
        {
            payload.Append("NativeFfi=1").AppendLine();
        }

        foreach (var nativeType in metadata.NativeTypes.OrderBy(type => type.Name, StringComparer.Ordinal).ThenBy(type => type.TypeName, StringComparer.Ordinal))
        {
            payload.Append(nativeType.Name).Append('|').Append(nativeType.TypeName).AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private ScriptPermissionStore LoadStore()
    {
        try
        {
            if (!File.Exists(_approvalsPath))
            {
                return new ScriptPermissionStore();
            }

            var protectedBytes = File.ReadAllBytes(_approvalsPath);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize(plainBytes, ScripterCoreJsonContext.Default.ScriptPermissionStore) ?? new ScriptPermissionStore();
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or JsonException)
        {
            return new ScriptPermissionStore();
        }
    }

    private void SaveStore(ScriptPermissionStore store)
    {
        var directory = Path.GetDirectoryName(_approvalsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(store, ScripterCoreJsonContext.Default.ScriptPermissionStore);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_approvalsPath, protectedBytes);
    }
}

internal sealed class ScriptPermissionStore
{
    public List<ScriptPermissionApproval> Approvals { get; set; } = [];
}

internal sealed class ScriptPermissionApproval
{
    public string ScriptPath { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public DateTimeOffset ApprovedAtUtc { get; set; }
}
