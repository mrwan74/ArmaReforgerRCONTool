using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using ReforgerRcon.Services;
using Sentry;
using SerilogTimings;

namespace ReforgerRcon.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    private const string UiActionErrorsMetric = "ui_action_errors";
    private const string ErrorTypeTag = "error_type";
    private const string ActionTag = "action";

    protected async Task<bool> ExecuteSafeAsync(
        Func<Task> action,
        string? userFriendlyErrorMessage = null,
        [CallerMemberName] string actionName = "",
        [CallerFilePath] string callerPath = "",
        [CallerLineNumber] int callerLine = 0)
    {
        ArgumentNullException.ThrowIfNull(action);

        var callerType = GetType().Name;
        var transaction = SentrySdk.StartTransaction(actionName, $"ui.action.{callerType}");
        using var op = Operation.Begin("Execute {ActionName} on {CallerType}", actionName, callerType);
        var sw = Stopwatch.StartNew();

        SentrySdk.Metrics.EmitCounter("ui_action_invoked", 1,
        [
            new KeyValuePair<string, object>(ActionTag, actionName),
            new KeyValuePair<string, object>("caller", callerType)
        ]);

        try
        {
            await action();
            sw.Stop();
            op.Complete();
            transaction.Finish(SpanStatus.Ok);

            SentrySdk.Metrics.EmitDistribution("ui_action_duration_ms", sw.ElapsedMilliseconds, MeasurementUnit.Duration.Millisecond,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>("outcome", "success")
            ]);

            return true;
        }
        catch (OperationCanceledException opEx)
        {
            sw.Stop();
            op.Cancel();
            transaction.Finish(SpanStatus.Cancelled);

            AppLogger.Debug(string.Create(CultureInfo.InvariantCulture, $"[Action:Canceled] {callerType}.{actionName}() cancelled: {opEx.Message}"), member: actionName, path: callerPath, line: callerLine);
            return false;
        }
        catch (SocketException sockEx)
        {
            sw.Stop();
            var demystified = sockEx.Demystify();
            transaction.Finish(SpanStatus.Unavailable);

            SentrySdk.Metrics.EmitCounter(UiActionErrorsMetric, 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>(ErrorTypeTag, "socket"),
                new KeyValuePair<string, object>("socket_code", sockEx.SocketErrorCode.ToString())
            ]);

            var msg = userFriendlyErrorMessage ?? $"Network communication failure (Error Code: {sockEx.SocketErrorCode}). Verify that the remote server IP and port are reachable and open in firewall.";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:SocketError] {callerType}.{actionName}(): SocketErrorCode={sockEx.SocketErrorCode}, NativeErrorCode={sockEx.NativeErrorCode}"), demystified, member: actionName, path: callerPath, line: callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("Network Connection Error", msg, actionName);
            return false;
        }
        catch (TimeoutException timeEx)
        {
            sw.Stop();
            var demystified = timeEx.Demystify();
            transaction.Finish(SpanStatus.DeadlineExceeded);

            SentrySdk.Metrics.EmitCounter(UiActionErrorsMetric, 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>(ErrorTypeTag, "timeout")
            ]);

            var msg = userFriendlyErrorMessage ?? $"The request '{actionName}' timed out waiting for the server to reply.";
            AppLogger.Warn(string.Create(CultureInfo.InvariantCulture, $"[Action:Timeout] {callerType}.{actionName}() timed out after {sw.ElapsedMilliseconds} ms."), demystified, member: actionName, path: callerPath, line: callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.WarningAlert);
            ToastNotificationService.Instance.ShowWarning("Request Timed Out", msg, actionName);
            return false;
        }
        catch (SqliteException sqlEx)
        {
            sw.Stop();
            var demystified = sqlEx.Demystify();
            transaction.Finish(SpanStatus.InternalError);

            SentrySdk.Metrics.EmitCounter(UiActionErrorsMetric, 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>(ErrorTypeTag, "sqlite"),
                new KeyValuePair<string, object>("sqlite_code", sqlEx.SqliteErrorCode.ToString())
            ]);

            var msg = userFriendlyErrorMessage ?? $"Local SQLite database storage error (Code: {sqlEx.SqliteErrorCode}).";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:SqliteError] {callerType}.{actionName}(): SqliteErrorCode={sqlEx.SqliteErrorCode}, ExtendedCode={sqlEx.SqliteExtendedErrorCode}"), demystified, member: actionName, path: callerPath, line: callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("Database Storage Error", msg, actionName);
            return false;
        }
        catch (HttpRequestException httpEx)
        {
            sw.Stop();
            var demystified = httpEx.Demystify();
            transaction.Finish(SpanStatus.Unavailable);

            SentrySdk.Metrics.EmitCounter(UiActionErrorsMetric, 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>(ErrorTypeTag, "http"),
                new KeyValuePair<string, object>("status_code", httpEx.StatusCode?.ToString() ?? "None")
            ]);

            var msg = userFriendlyErrorMessage ?? $"Web service communication error ({httpEx.StatusCode?.ToString() ?? "No Response"}). Check internet connection.";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:HttpError] {callerType}.{actionName}(): StatusCode={httpEx.StatusCode}, HttpRequestError={httpEx.HttpRequestError}"), demystified, member: actionName, path: callerPath, line: callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.WarningAlert);
            ToastNotificationService.Instance.ShowError("Web Service Error", msg, actionName);
            return false;
        }
        catch (JsonException jsonEx)
        {
            sw.Stop();
            var demystified = jsonEx.Demystify();
            transaction.Finish(SpanStatus.InvalidArgument);

            var msg = userFriendlyErrorMessage ?? "Failed to parse data configuration format.";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:JsonError] {callerType}.{actionName}(): LineNumber={jsonEx.LineNumber}, BytePosition={jsonEx.BytePositionInLine}, Path={jsonEx.Path}"), demystified, member: actionName, path: callerPath, line: callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.WarningAlert);
            ToastNotificationService.Instance.ShowError("Data Format Error", msg, actionName);
            return false;
        }
        catch (UnauthorizedAccessException authEx)
        {
            sw.Stop();
            var demystified = authEx.Demystify();
            transaction.Finish(SpanStatus.PermissionDenied);

            var msg = userFriendlyErrorMessage ?? "File or directory access was denied by operating system permissions.";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:AccessDenied] {callerType}.{actionName}(): {authEx.Message}"), demystified, member: actionName, path: callerPath, line: callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("Access Denied", msg, actionName);
            return false;
        }
        catch (IOException ioEx)
        {
            sw.Stop();
            var demystified = ioEx.Demystify();
            transaction.Finish(SpanStatus.InternalError);

            var msg = userFriendlyErrorMessage ?? "Disk read/write failure occurred.";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:IOError] {callerType}.{actionName}(): HResult=0x{ioEx.HResult:X8}, Message={ioEx.Message}"), demystified, member: actionName, path: callerPath, line: callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("Disk I/O Error", msg, actionName);
            return false;
        }
        catch (ArgumentException argEx)
        {
            sw.Stop();
            var demystified = argEx.Demystify();
            transaction.Finish(SpanStatus.InvalidArgument);

            var msg = userFriendlyErrorMessage ?? $"Invalid parameter specified: {argEx.Message}";
            AppLogger.Warn(string.Create(CultureInfo.InvariantCulture, $"[Action:ArgumentError] {callerType}.{actionName}(): ParamName={argEx.ParamName}, Message={argEx.Message}"), demystified, member: actionName, path: callerPath, line: callerLine);

            ToastNotificationService.Instance.ShowWarning("Invalid Parameter", msg, actionName);
            return false;
        }
        catch (InvalidOperationException invOpEx)
        {
            sw.Stop();
            var demystified = invOpEx.Demystify();
            transaction.Finish(SpanStatus.FailedPrecondition);

            var msg = userFriendlyErrorMessage ?? $"Action '{actionName}' cannot be performed in current state: {invOpEx.Message}";
            AppLogger.Warn(string.Create(CultureInfo.InvariantCulture, $"[Action:InvalidOperation] {callerType}.{actionName}(): {invOpEx.Message}"), demystified, member: actionName, path: callerPath, line: callerLine);

            ToastNotificationService.Instance.ShowWarning("Invalid State", msg, actionName);
            return false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var demystified = ex.Demystify();
            transaction.Finish(SpanStatus.UnknownError);

            SentrySdk.Metrics.EmitCounter(UiActionErrorsMetric, 1,
            [
                new KeyValuePair<string, object>(ActionTag, actionName),
                new KeyValuePair<string, object>(ErrorTypeTag, demystified.GetType().Name)
            ]);

            var msg = userFriendlyErrorMessage ?? $"An unexpected error occurred during '{actionName}': {ex.Message}";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:UnhandledException] {callerType}.{actionName}() encountered an unhandled fault: {ex.Message}"), demystified, member: actionName, path: callerPath, line: callerLine);

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
        var transaction = SentrySdk.StartTransaction(actionName, $"ui.sync.{callerType}");
        using var op = Operation.Begin("Execute {ActionName} on {CallerType}", actionName, callerType);

        try
        {
            action();
            op.Complete();
            transaction.Finish(SpanStatus.Ok);
            return true;
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
            transaction.Finish(SpanStatus.Cancelled);
            AppLogger.Debug(string.Create(CultureInfo.InvariantCulture, $"[Action:Canceled] {callerType}.{actionName}() cancelled."), member: actionName, path: callerPath, line: callerLine);
            return false;
        }
        catch (ArgumentException argEx)
        {
            var demystified = argEx.Demystify();
            transaction.Finish(SpanStatus.InvalidArgument);

            var msg = userFriendlyErrorMessage ?? $"Invalid parameter specified: {argEx.Message}";
            AppLogger.Warn(string.Create(CultureInfo.InvariantCulture, $"[Action:ArgumentError] {callerType}.{actionName}(): ParamName={argEx.ParamName}"), demystified, member: actionName, path: callerPath, line: callerLine);

            ToastNotificationService.Instance.ShowWarning("Invalid Parameter", msg, actionName);
            return false;
        }
        catch (InvalidOperationException invOpEx)
        {
            var demystified = invOpEx.Demystify();
            transaction.Finish(SpanStatus.FailedPrecondition);

            var msg = userFriendlyErrorMessage ?? $"Action '{actionName}' cannot be executed in current state: {invOpEx.Message}";
            AppLogger.Warn(string.Create(CultureInfo.InvariantCulture, $"[Action:InvalidOperation] {callerType}.{actionName}(): {invOpEx.Message}"), demystified, member: actionName, path: callerPath, line: callerLine);

            ToastNotificationService.Instance.ShowWarning("Invalid State", msg, actionName);
            return false;
        }
        catch (Exception ex)
        {
            var demystified = ex.Demystify();
            transaction.Finish(SpanStatus.UnknownError);

            var msg = userFriendlyErrorMessage ?? $"Action '{actionName}' failed: {ex.Message}";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:UnhandledException] {callerType}.{actionName}() failed: {ex.Message}"), demystified, member: actionName, path: callerPath, line: callerLine);

            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
            ToastNotificationService.Instance.ShowError("Action Error", msg, actionName);
            return false;
        }
    }
}