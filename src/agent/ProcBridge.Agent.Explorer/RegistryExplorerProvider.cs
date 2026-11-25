using Microsoft.Win32;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Agent.Explorer;

/// <summary>Registry browsing and editing below the registry roots of the policy (<c>Reg.*</c>).</summary>
public sealed class RegistryExplorerProvider : ExplorerProviderBase
{
    private readonly RegistryScopeGuard _guard;

    internal RegistryExplorerProvider(ExplorerPolicy policy, string requesterId, IExplorerAuditLog audit)
        : base(policy, requesterId, audit)
    {
        _guard = new RegistryScopeGuard(policy.RegistryRoots);
    }

    public RegistryListResponse List(RegistryListRequest request) => Read(ExplorerPermissions.RegistryRead, () =>
    {
        var path = _guard.Resolve(request.Path);
        using var key = Open(path, writable: false);
        var values = key.GetValueNames()
            .Select(name => ToValue(key, name))
            .ToList();
        return new RegistryListResponse { Path = path.Normalized, SubKeys = key.GetSubKeyNames(), Values = values };
    });

    public ExplorerAckResponse SetValue(RegistrySetValueRequest request) => Write(ExplorerPermissions.RegistryWrite, "Reg.SetValue", $"{request.Path}\\{request.Value?.Name}", () =>
    {
        // Required members can still arrive as nil from the wire.
        var value = request.Value ?? throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Value is missing.");
        var path = _guard.Resolve(request.Path);
        using var key = Open(path, writable: true);
        var (data, kind) = ToData(value);
        key.SetValue(value.Name, data, kind);
    });

    public ExplorerAckResponse DeleteValue(RegistryDeleteValueRequest request) => Write(ExplorerPermissions.RegistryWrite, "Reg.DeleteValue", $"{request.Path}\\{request.Name}", () =>
    {
        var path = _guard.Resolve(request.Path);
        using var key = Open(path, writable: true);
        if (!key.GetValueNames().Contains(request.Name, StringComparer.OrdinalIgnoreCase))
            throw new ExplorerDeniedException(ExplorerStatus.NotFound, "Value not found.");
        key.DeleteValue(request.Name);
    });

    public ExplorerAckResponse CreateKey(RegistryCreateKeyRequest request) => Write(ExplorerPermissions.RegistryWrite, "Reg.CreateKey", request.Path, () =>
    {
        var path = _guard.Resolve(request.Path, forChange: true);
        using var root = path.OpenBase();
        using var key = root.CreateSubKey(path.SubKey, writable: false);
    });

    public ExplorerAckResponse DeleteKey(RegistryDeleteKeyRequest request) => Write(ExplorerPermissions.RegistryWrite, "Reg.DeleteKey", request.Path, () =>
    {
        var path = _guard.Resolve(request.Path, forChange: true);
        using var root = path.OpenBase();
        using (var existing = root.OpenSubKey(path.SubKey, writable: false))
        {
            if (existing is null)
                throw new ExplorerDeniedException(ExplorerStatus.NotFound, "Key not found.");
        }

        if (request.Recursive)
            root.DeleteSubKeyTree(path.SubKey);
        else
            root.DeleteSubKey(path.SubKey); // throws InvalidOperationException when it has sub keys
    });

    private static RegistryKey Open(RegistryPath path, bool writable)
    {
        using var root = path.OpenBase();
        if (path.SubKey.Length == 0)
            return path.OpenBase();
        return root.OpenSubKey(path.SubKey, writable)
            ?? throw new ExplorerDeniedException(ExplorerStatus.NotFound, "Key not found.");
    }

    private static RegistryValue ToValue(RegistryKey key, string name)
    {
        var kind = key.GetValueKind(name);
        var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return kind switch
        {
            RegistryValueKind.String => new RegistryValue { Name = name, Type = RegistryValueType.String, Text = data as string },
            RegistryValueKind.ExpandString => new RegistryValue { Name = name, Type = RegistryValueType.ExpandString, Text = data as string },
            RegistryValueKind.DWord => new RegistryValue { Name = name, Type = RegistryValueType.DWord, Number = (uint)(int)data! },
            RegistryValueKind.QWord => new RegistryValue { Name = name, Type = RegistryValueType.QWord, Number = (long)data! },
            RegistryValueKind.MultiString => new RegistryValue { Name = name, Type = RegistryValueType.MultiString, Strings = (string[]?)data },
            RegistryValueKind.Binary => new RegistryValue { Name = name, Type = RegistryValueType.Binary, Bytes = data as byte[] },
            RegistryValueKind.None => new RegistryValue { Name = name, Type = RegistryValueType.None, Bytes = data as byte[] },
            _ => new RegistryValue { Name = name, Type = RegistryValueType.Unknown, Bytes = data as byte[] },
        };
    }

    private static (object Data, RegistryValueKind Kind) ToData(RegistryValue value) => value.Type switch
    {
        RegistryValueType.String => (Require(value.Text), RegistryValueKind.String),
        RegistryValueType.ExpandString => (Require(value.Text), RegistryValueKind.ExpandString),
        RegistryValueType.DWord => (unchecked((int)checked((uint)Require(value.Number))), RegistryValueKind.DWord),
        RegistryValueType.QWord => (Require(value.Number), RegistryValueKind.QWord),
        RegistryValueType.MultiString => (Require(value.Strings).ToArray(), RegistryValueKind.MultiString),
        RegistryValueType.Binary => (Require(value.Bytes), RegistryValueKind.Binary),
        _ => throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, $"Value type {value.Type} cannot be written."),
    };

    private static T Require<T>(T? data) where T : class
        => data ?? throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Value data is missing for its type.");

    private static long Require(long? data)
        => data ?? throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Value data is missing for its type.");
}
