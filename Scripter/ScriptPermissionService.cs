using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scripter;

internal sealed class ScriptPermissionService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PowerToys.CmdPal.Scripter.Permission.v1");

    private readonly string _approvalsPath;

    public ScriptPermissionService(string rootDirectory)
    {
        _approvalsPath = Path.Combine(rootDirectory, "script-permissions.bin");
    }

    public static bool RequiresPermission(ScriptMetadata metadata)
    {
        return metadata.DynamicImport || metadata.NativeTypes.Count > 0 || metadata.CommandExecution;
    }

    public bool IsApproved(string scriptPath, string scriptContent, ScriptMetadata metadata)
    {
        var store = LoadStore();
        var normalizedPath = Path.GetFullPath(scriptPath);
        var fingerprint = ComputeFingerprint(scriptContent, metadata);
        return store.Approvals.Any(a =>
            string.Equals(a.ScriptPath, normalizedPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Fingerprint, fingerprint, StringComparison.Ordinal));
    }

    public void Approve(string scriptPath, string scriptContent, ScriptMetadata metadata)
    {
        var store = LoadStore();
        var normalizedPath = Path.GetFullPath(scriptPath);
        var fingerprint = ComputeFingerprint(scriptContent, metadata);

        store.Approvals.RemoveAll(a => string.Equals(a.ScriptPath, normalizedPath, StringComparison.OrdinalIgnoreCase));
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
        var fingerprintPayload = new StringBuilder();
        fingerprintPayload.AppendLine(scriptContent);
        fingerprintPayload.Append("Type=").Append(metadata.Type).AppendLine();
        fingerprintPayload.Append("DynamicImport=").Append(metadata.DynamicImport ? "1" : "0").AppendLine();
        fingerprintPayload.Append("CommandExecution=").Append(metadata.CommandExecution ? "1" : "0").AppendLine();
        foreach (var nativeType in metadata.NativeTypes.OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.TypeName, StringComparer.Ordinal))
        {
            fingerprintPayload.Append(nativeType.Name).Append('|').Append(nativeType.TypeName).AppendLine();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintPayload.ToString()));
        return Convert.ToHexString(hash);
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
            var store = JsonSerializer.Deserialize(plainBytes, ScripterJsonContext.Default.ScriptPermissionStore);
            return store ?? new ScriptPermissionStore();
        }
        catch
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

        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(store, ScripterJsonContext.Default.ScriptPermissionStore);
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
