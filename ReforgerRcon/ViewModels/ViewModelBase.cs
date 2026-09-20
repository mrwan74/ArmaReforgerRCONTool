using Aptabase.Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using ReforgerRcon.Services;
using Sentry;
using Serilog.Context;
using SerilogTimings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace ReforgerRcon.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    private const string UiActionErrorsMetric = "ui_action_errors";
    private const string ErrorTypeTag = "error_type";
    private const string ActionTag = "action";
    private const string ThreadIdKey = "thread_id";
    private const string TaskIdKey = "task_id";
    private const string WorkingSetMbKey = "ram_mb";
    private const string ElapsedMsKey = "elapsed_ms";

    protected async Task<bool> ExecuteSafeAsync(
        Func<Task> action,
        string? userFriendlyErrorMessage = null,
        bool trackCloudTelemetry = true,
        [CallerMemberName] string actionName = "",
        [CallerFilePath] string callerPath = "",
        [CallerLineNumber] int callerLine = 0)
    {
        ArgumentNullException.ThrowIfNull(action);

        var callerType = GetType().Name;
        var file = Path.GetFileName(callerPath);
        var callerContext = $"{file}:{callerLine} -> {actionName}()";
        var threadId = Environment.CurrentManagedThreadId;
        var taskId = Task.CurrentId?.ToString(CultureInfo.InvariantCulture) ?? "-";

        var diagnosticContext = new Dictionary<string, object?>
        {
            [ActionTag] = actionName,
            ["caller_type"] = callerType,
            [ThreadIdKey] = threadId,
            [TaskIdKey] = taskId,
            [WorkingSetMbKey] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1)
        };

        AppLogger.Trace($"[Action:Begin] Executing asynchronous action '{actionName}' on '{callerType}'...", diagnosticContext, actionName, callerPath, callerLine);

        ISpan? transaction = null;
        if (trackCloudTelemetry)
        {
            transaction = SentrySdk.StartTransaction(actionName, $"ui.action.{callerType}");
            SentrySdk.Metrics.EmitCounter("ui_action_invoked", 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>("caller", callerType)
            ]);
        }

        using var logContext = LogContext.PushProperty("CallerContext", callerContext);
        using var op = Operation.Begin("Execute {ActionName} on {CallerType}", actionName, callerType);
        var sw = Stopwatch.StartNew();

        try
        {
            await action().ConfigureAwait(false);
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;

            op.Complete();

            if (trackCloudTelemetry)
            {
                transaction?.Finish(SpanStatus.Ok);
                SentrySdk.Metrics.EmitDistribution("ui_action_duration_ms", sw.ElapsedMilliseconds, MeasurementUnit.Duration.Millisecond,
                [
                    new KeyValuePair<string, object>(ActionTag, actionName),
                    new KeyValuePair<string, object>("outcome", "success")
                ]);

                AppLogger.TrackEvent("ui_action_completed", new Dictionary<string, object>
                {
                    ["action"] = actionName,
                    ["view_model"] = callerType,
                    ["duration_ms"] = sw.ElapsedMilliseconds
                });
            }

            AppLogger.Debug($"[Action:Success] '{callerType}.{actionName}()' completed in {sw.ElapsedMilliseconds}ms.", diagnosticContext, actionName, callerPath, callerLine);
            return true;
        }
        catch (OperationCanceledException opEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            op.Cancel();
            transaction?.Finish(SpanStatus.Cancelled);

            AppLogger.Debug($"[Action:Canceled] '{callerType}.{actionName}()' cancelled after {sw.ElapsedMilliseconds}ms: {opEx.Message}", diagnosticContext, actionName, callerPath, callerLine);
            return false;
        }
        catch (SocketException sockEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["socket_error_code"] = sockEx.SocketErrorCode.ToString();
            diagnosticContext["native_error_code"] = sockEx.NativeErrorCode;

            var demystified = sockEx.Demystify();
            transaction?.Finish(SpanStatus.Unavailable);

            TrackAptabaseError(demystified, actionName, callerType);

            SentrySdk.Metrics.EmitCounter(UiActionErrorsMetric, 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>(ErrorTypeTag, "socket"),
                new KeyValuePair<string, object>("socket_code", sockEx.SocketErrorCode.ToString())
            ]);

            var msg = userFriendlyErrorMessage ?? $"Network socket error ({sockEx.SocketErrorCode}). Verify that the remote server IP and port are reachable.";
            AppLogger.Error($"[Action:SocketError] Fault in '{callerType}.{actionName}()' (ErrorCode={sockEx.SocketErrorCode}, Native={sockEx.NativeErrorCode}): {sockEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("Network Connection Error", msg, actionName);
            return false;
        }
        catch (TimeoutException timeEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            var demystified = timeEx.Demystify();
            transaction?.Finish(SpanStatus.DeadlineExceeded);

            TrackAptabaseError(demystified, actionName, callerType);

            SentrySdk.Metrics.EmitCounter(UiActionErrorsMetric, 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>(ErrorTypeTag, "timeout")
            ]);

            var msg = userFriendlyErrorMessage ?? $"The request '{actionName}' timed out waiting for the server to reply.";
            AppLogger.Warn($"[Action:Timeout] '{callerType}.{actionName}()' timed out after {sw.ElapsedMilliseconds}ms: {timeEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.WarningAlert);
            ToastNotificationService.Instance.ShowWarning("Request Timed Out", msg, actionName);
            return false;
        }
        catch (SqliteException sqlEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["sqlite_error_code"] = sqlEx.SqliteErrorCode;
            diagnosticContext["sqlite_extended_code"] = sqlEx.SqliteExtendedErrorCode;

            var demystified = sqlEx.Demystify();
            transaction?.Finish(SpanStatus.InternalError);

            TrackAptabaseError(demystified, actionName, callerType);

            SentrySdk.Metrics.EmitCounter(UiActionErrorsMetric, 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>(ErrorTypeTag, "sqlite"),
                new KeyValuePair<string, object>("sqlite_code", sqlEx.SqliteErrorCode.ToString(CultureInfo.InvariantCulture))
            ]);

            var msg = userFriendlyErrorMessage ?? $"Local SQLite database storage error (Code: {sqlEx.SqliteErrorCode}).";
            AppLogger.Error($"[Action:SqliteError] Database failure in '{callerType}.{actionName}()' (Code={sqlEx.SqliteErrorCode}, Ext={sqlEx.SqliteExtendedErrorCode}): {sqlEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("Database Storage Error", msg, actionName);
            return false;
        }
        catch (HttpRequestException httpEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["http_status_code"] = httpEx.StatusCode?.ToString() ?? "None";
            diagnosticContext["http_request_error"] = httpEx.HttpRequestError.ToString();

            var demystified = httpEx.Demystify();
            transaction?.Finish(SpanStatus.Unavailable);

            TrackAptabaseError(demystified, actionName, callerType);

            SentrySdk.Metrics.EmitCounter(UiActionErrorsMetric, 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>(ErrorTypeTag, "http"),
                new KeyValuePair<string, object>("status_code", httpEx.StatusCode?.ToString() ?? "None")
            ]);

            var msg = userFriendlyErrorMessage ?? $"Web service communication error ({httpEx.StatusCode?.ToString() ?? "No Response"}). Check internet connection.";
            AppLogger.Error($"[Action:HttpError] HTTP failure in '{callerType}.{actionName}()' (Status={httpEx.StatusCode}): {httpEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.WarningAlert);
            ToastNotificationService.Instance.ShowError("Web Service Error", msg, actionName);
            return false;
        }
        catch (JsonException jsonEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["json_line"] = jsonEx.LineNumber;
            diagnosticContext["json_path"] = jsonEx.Path;

            var demystified = jsonEx.Demystify();
            transaction?.Finish(SpanStatus.InvalidArgument);

            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? "Failed to parse data configuration format.";
            AppLogger.Error($"[Action:JsonError] JSON deserialization failure in '{callerType}.{actionName}()' at line {jsonEx.LineNumber} (Path='{jsonEx.Path}'): {jsonEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.WarningAlert);
            ToastNotificationService.Instance.ShowError("Data Format Error", msg, actionName);
            return false;
        }
        catch (FileNotFoundException fnfEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["missing_file"] = fnfEx.FileName;

            var demystified = fnfEx.Demystify();
            transaction?.Finish(SpanStatus.NotFound);

            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? $"Required file not found: {fnfEx.FileName}";
            AppLogger.Error($"[Action:FileNotFound] File missing in '{callerType}.{actionName}()': {fnfEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            ToastNotificationService.Instance.ShowError("File Not Found", msg, actionName);
            return false;
        }
        catch (DirectoryNotFoundException dnfEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;

            var demystified = dnfEx.Demystify();
            transaction?.Finish(SpanStatus.NotFound);

            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? "Required directory path was not found.";
            AppLogger.Error($"[Action:DirectoryNotFound] Directory missing in '{callerType}.{actionName}()': {dnfEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            ToastNotificationService.Instance.ShowError("Directory Not Found", msg, actionName);
            return false;
        }
        catch (UnauthorizedAccessException authEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;

            var demystified = authEx.Demystify();
            transaction?.Finish(SpanStatus.PermissionDenied);

            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? "File or directory access was denied by operating system permissions.";
            AppLogger.Error($"[Action:AccessDenied] Permission denied in '{callerType}.{actionName}()': {authEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("Access Denied", msg, actionName);
            return false;
        }
        catch (IOException ioEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["hresult"] = $"0x{ioEx.HResult:X8}";

            var demystified = ioEx.Demystify();
            transaction?.Finish(SpanStatus.InternalError);

            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? "Disk read/write failure occurred while accessing local files.";
            AppLogger.Error($"[Action:IOError] File system failure in '{callerType}.{actionName}()' (HResult=0x{ioEx.HResult:X8}): {ioEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("Disk I/O Error", msg, actionName);
            return false;
        }
        catch (ArgumentException argEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["param_name"] = argEx.ParamName;

            var demystified = argEx.Demystify();
            transaction?.Finish(SpanStatus.InvalidArgument);

            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? $"Invalid parameter specified: {argEx.Message}";
            AppLogger.Warn($"[Action:ArgumentError] Invalid argument in '{callerType}.{actionName}()' (Param={argEx.ParamName}): {argEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            ToastNotificationService.Instance.ShowWarning("Invalid Parameter", msg, actionName);
            return false;
        }
        catch (InvalidOperationException invOpEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;

            var demystified = invOpEx.Demystify();
            transaction?.Finish(SpanStatus.FailedPrecondition);

            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? $"Action '{actionName}' cannot be performed in current state: {invOpEx.Message}";
            AppLogger.Warn($"[Action:InvalidOperation] Invalid state in '{callerType}.{actionName}()': {invOpEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            ToastNotificationService.Instance.ShowWarning("Invalid State", msg, actionName);
            return false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["exception_type"] = ex.GetType().FullName;

            var demystified = ex.Demystify();
            transaction?.Finish(SpanStatus.UnknownError);

            TrackAptabaseError(demystified, actionName, callerType);

            SentrySdk.Metrics.EmitCounter(UiActionErrorsMetric, 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>(ErrorTypeTag, demystified.GetType().Name)
            ]);

            var msg = userFriendlyErrorMessage ?? $"An unexpected error occurred during '{actionName}': {ex.Message}";
            AppLogger.Fatal($"[Action:UnhandledException] Unhandled exception in '{callerType}.{actionName}()' after {sw.ElapsedMilliseconds}ms: {ex.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("System Alert", msg, actionName);
            return false;
        }
    }

    protected bool ExecuteSafe(
        Action action,
        string? userFriendlyErrorMessage = null,
        [CallerMemberName] string actionName = "",
        [CallerFilePath] string callerPath = "",
        [CallerLineNumber] int callerLine = 0)
    {
        ArgumentNullException.ThrowIfNull(action);

        var callerType = GetType().Name;
        var file = Path.GetFileName(callerPath);
        var callerContext = $"{file}:{callerLine} -> {actionName}()";
        var threadId = Environment.CurrentManagedThreadId;

        var diagnosticContext = new Dictionary<string, object?>
        {
            [ActionTag] = actionName,
            ["caller_type"] = callerType,
            [ThreadIdKey] = threadId,
            [WorkingSetMbKey] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1)
        };

        AppLogger.Trace($"[ActionSync:Begin] Executing synchronous action '{actionName}' on '{callerType}'...", diagnosticContext, actionName, callerPath, callerLine);

        var transaction = SentrySdk.StartTransaction(actionName, $"ui.sync.{callerType}");
        using var logContext = LogContext.PushProperty("CallerContext", callerContext);
        using var op = Operation.Begin("Execute {ActionName} on {CallerType}", actionName, callerType);
        var sw = Stopwatch.StartNew();

        try
        {
            action();
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;

            op.Complete();
            transaction.Finish(SpanStatus.Ok);

            AppLogger.Debug($"[ActionSync:Success] '{callerType}.{actionName}()' completed in {sw.ElapsedMilliseconds}ms.", diagnosticContext, actionName, callerPath, callerLine);
            return true;
        }
        catch (OperationCanceledException opEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            op.Cancel();
            transaction.Finish(SpanStatus.Cancelled);

            AppLogger.Debug($"[ActionSync:Canceled] '{callerType}.{actionName}()' cancelled after {sw.ElapsedMilliseconds}ms: {opEx.Message}", diagnosticContext, actionName, callerPath, callerLine);
            return false;
        }
        catch (ArgumentException argEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["param_name"] = argEx.ParamName;

            var demystified = argEx.Demystify();
            transaction.Finish(SpanStatus.InvalidArgument);
            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? $"Invalid parameter specified: {argEx.Message}";
            AppLogger.Warn($"[ActionSync:ArgumentError] Invalid argument in '{callerType}.{actionName}()' (Param={argEx.ParamName}): {argEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            ToastNotificationService.Instance.ShowWarning("Invalid Parameter", msg, actionName);
            return false;
        }
        catch (InvalidOperationException invOpEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;

            var demystified = invOpEx.Demystify();
            transaction.Finish(SpanStatus.FailedPrecondition);
            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? $"Action '{actionName}' cannot be executed in current state: {invOpEx.Message}";
            AppLogger.Warn($"[ActionSync:InvalidOperation] Invalid state in '{callerType}.{actionName}()': {invOpEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            ToastNotificationService.Instance.ShowWarning("Invalid State", msg, actionName);
            return false;
        }
        catch (UnauthorizedAccessException authEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;

            var demystified = authEx.Demystify();
            transaction.Finish(SpanStatus.PermissionDenied);
            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? "Operating system permission denied.";
            AppLogger.Error($"[ActionSync:AccessDenied] Permission denied in '{callerType}.{actionName}()': {authEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            ToastNotificationService.Instance.ShowError("Access Denied", msg, actionName);
            return false;
        }
        catch (IOException ioEx)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["hresult"] = $"0x{ioEx.HResult:X8}";

            var demystified = ioEx.Demystify();
            transaction.Finish(SpanStatus.InternalError);
            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? "Disk I/O error occurred.";
            AppLogger.Error($"[ActionSync:IOError] File system error in '{callerType}.{actionName}()' (HResult=0x{ioEx.HResult:X8}): {ioEx.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            ToastNotificationService.Instance.ShowError("Disk Error", msg, actionName);
            return false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            diagnosticContext[ElapsedMsKey] = sw.ElapsedMilliseconds;
            diagnosticContext["exception_type"] = ex.GetType().FullName;

            var demystified = ex.Demystify();
            transaction.Finish(SpanStatus.UnknownError);
            TrackAptabaseError(demystified, actionName, callerType);

            var msg = userFriendlyErrorMessage ?? $"Action '{actionName}' failed: {ex.Message}";
            AppLogger.Error($"[ActionSync:Error] Synchronous action failure in '{callerType}.{actionName}()' after {sw.ElapsedMilliseconds}ms: {ex.Message}", demystified, diagnosticContext, actionName, callerPath, callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("Action Error", msg, actionName);
            return false;
        }
    }

    private static void TrackAptabaseError(Exception ex, string actionName, string callerType)
    {
        if (!Models.AppSettings.IsCrashReportingEnabled() || !AptabaseExtensions.IsInitialized) return;

        try
        {
            _ = AptabaseExtensions.Instance.TrackError(ex, fatal: false);
            AppLogger.TrackEvent("ui_action_error", new Dictionary<string, object>
            {
                ["action"] = actionName,
                ["caller"] = callerType,
                ["error_type"] = ex.GetType().Name
            });
        }
        catch (Exception aptaEx)
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModelBase:Aptabase] Error tracking notice: {aptaEx.Message}");
        }
    }
}