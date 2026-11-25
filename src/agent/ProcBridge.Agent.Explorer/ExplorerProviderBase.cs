using System.ComponentModel;
using System.Security;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Agent.Explorer;

/// <summary>
/// Shared plumbing of the providers: permission checks, mapping exceptions to
/// <see cref="ExplorerStatus"/>, and auditing write operations.
/// </summary>
public abstract class ExplorerProviderBase
{
    private readonly ExplorerPolicy _policy;
    private readonly string _requesterId;
    private readonly IExplorerAuditLog _audit;

    private protected ExplorerProviderBase(ExplorerPolicy policy, string requesterId, IExplorerAuditLog audit)
    {
        _policy = policy;
        _requesterId = requesterId;
        _audit = audit;
    }

    private protected ExplorerPolicy Policy => _policy;

    private protected TResponse Read<TResponse>(ExplorerPermissions permission, Func<TResponse> action)
        where TResponse : ExplorerResponse, new()
    {
        if (!_policy.Permissions.HasFlag(permission))
            return Fail<TResponse>(ExplorerStatus.PermissionDenied, $"Permission {permission} is not granted.");
        return Run(action);
    }

    private protected ExplorerAckResponse Write(ExplorerPermissions permission, string action, string? target, Action body)
    {
        ExplorerAckResponse response;
        if (!_policy.Permissions.HasFlag(permission))
            response = Fail<ExplorerAckResponse>(ExplorerStatus.PermissionDenied, $"Permission {permission} is not granted.");
        else
            response = Run(() =>
            {
                body();
                return new ExplorerAckResponse();
            });

        _audit.Write(new ExplorerAuditEntry
        {
            TimeUtc = DateTime.UtcNow,
            RequesterId = _requesterId,
            Action = action,
            Target = target ?? string.Empty,
            Status = response.Status,
        });
        return response;
    }

    private static TResponse Run<TResponse>(Func<TResponse> action)
        where TResponse : ExplorerResponse, new()
    {
        try
        {
            return action();
        }
        catch (ExplorerDeniedException ex)
        {
            return Fail<TResponse>(ex.Status, ex.Message);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return Fail<TResponse>(ExplorerStatus.AccessDenied, ex.Message);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Fail<TResponse>(ExplorerStatus.NotFound, ex.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            return Fail<TResponse>(ExplorerStatus.AccessDenied, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return Fail<TResponse>(ExplorerStatus.InvalidArgument, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception
            or System.ServiceProcess.TimeoutException)
        {
            return Fail<TResponse>(ExplorerStatus.Failed, ex.Message);
        }
    }

    private static TResponse Fail<TResponse>(ExplorerStatus status, string message)
        where TResponse : ExplorerResponse, new()
        => new() { Status = status, Error = message };
}
