using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ReforgerRcon.Services;
using Sentry;
using SerilogTimings;

namespace ReforgerRcon.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    protected async Task<bool> ExecuteSafeAsync(
        Func<Task> action,
        string? userFriendlyErrorMessage = null,
        [CallerMemberName] string actionName = "",
        [CallerFilePath] string callerPath = "",
        [CallerLineNumber] int callerLine = 0)
    {
        var callerType = GetType().Name;
        var transaction = SentrySdk.StartTransaction(actionName, $"ui.action.{callerType}");
        using var op = Operation.Begin("Execute {ActionName} on {CallerType}", actionName, callerType);
        var sw = Stopwatch.StartNew();

        SentrySdk.Metrics.EmitCounter("ui_action_invoked", 1,
        [
            new KeyValuePair<string, object>("action", actionName),
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
                new KeyValuePair<string, object>("action", actionName),
                new KeyValuePair<string, object>("outcome", "success")
            ]);

            return true;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            op.Cancel();
            transaction.Finish(SpanStatus.Cancelled);

            AppLogger.Debug(string.Create(CultureInfo.InvariantCulture, $"[Action:Canceled] {callerType}.{actionName}() was cancelled."), member: actionName, path: callerPath, line: callerLine);
            return false;
        }
        catch (SocketException sockEx)
        {
            sw.Stop();
            var demystified = sockEx.Demystify();
            transaction.Finish(SpanStatus.Unavailable);

            SentrySdk.Metrics.EmitCounter("ui_action_errors", 1,
            [
                new KeyValuePair<string, object>("action", actionName),
                new KeyValuePair<string, object>("error_type", "socket")
            ]);

            var msg = userFriendlyErrorMessage ?? $"Network socket failure during '{actionName}': {sockEx.SocketErrorCode}";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:SocketError] {callerType}.{actionName}(): {sockEx.SocketErrorCode}"), demystified, member: actionName, path: callerPath, line: callerLine);
            ToastNotificationService.Instance.ShowToast("Connection Failure", msg, actionName);
            return false;
        }
        catch (TimeoutException timeEx)
        {
            sw.Stop();
            var demystified = timeEx.Demystify();
            transaction.Finish(SpanStatus.DeadlineExceeded);

            SentrySdk.Metrics.EmitCounter("ui_action_errors", 1,
            [
                new KeyValuePair<string, object>("action", actionName),
                new KeyValuePair<string, object>("error_type", "timeout")
            ]);

            var msg = userFriendlyErrorMessage ?? $"Operation '{actionName}' timed out waiting for server.";
            AppLogger.Warn(string.Create(CultureInfo.InvariantCulture, $"[Action:Timeout] {callerType}.{actionName}() timed out."), demystified, member: actionName, path: callerPath, line: callerLine);
            ToastNotificationService.Instance.ShowToast("Operation Timeout", msg, actionName);
            return false;
        }
        catch (JsonException jsonEx)
        {
            sw.Stop();
            var demystified = jsonEx.Demystify();
            transaction.Finish(SpanStatus.InvalidArgument);

            var msg = userFriendlyErrorMessage ?? $"Data parsing failure during '{actionName}'.";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:JsonError] {callerType}.{actionName}()"), demystified, member: actionName, path: callerPath, line: callerLine);
            ToastNotificationService.Instance.ShowToast("Data Format Error", msg, actionName);
            return false;
        }
        catch (IOException ioEx)
        {
            sw.Stop();
            var demystified = ioEx.Demystify();
            transaction.Finish(SpanStatus.InternalError);

            var msg = userFriendlyErrorMessage ?? $"File access error during '{actionName}': {ioEx.Message}";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:IOError] {callerType}.{actionName}()"), demystified, member: actionName, path: callerPath, line: callerLine);
            ToastNotificationService.Instance.ShowToast("Disk Error", msg, actionName);
            return false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var demystified = ex.Demystify();
            transaction.Finish(SpanStatus.UnknownError);

            SentrySdk.Metrics.EmitCounter("ui_action_errors", 1,
            [
                new KeyValuePair<string, object>("action", actionName),
                new KeyValuePair<string, object>("error_type", demystified.GetType().Name)
            ]);

            var msg = userFriendlyErrorMessage ?? $"Operation '{actionName}' encountered an unexpected error: {ex.Message}";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:Unexpected] {callerType}.{actionName}()"), demystified, member: actionName, path: callerPath, line: callerLine);
            ToastNotificationService.Instance.ShowToast("System Alert", msg, actionName);
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
            AppLogger.Debug(string.Create(CultureInfo.InvariantCulture, $"[Action:Canceled] {callerType}.{actionName}() was cancelled."), member: actionName, path: callerPath, line: callerLine);
            return false;
        }
        catch (ArgumentException argEx)
        {
            var demystified = argEx.Demystify();
            transaction.Finish(SpanStatus.InvalidArgument);

            var msg = userFriendlyErrorMessage ?? $"Invalid parameter supplied to '{actionName}': {argEx.Message}";
            AppLogger.Warn(string.Create(CultureInfo.InvariantCulture, $"[Action:ArgumentError] {callerType}.{actionName}()"), demystified, member: actionName, path: callerPath, line: callerLine);
            ToastNotificationService.Instance.ShowToast("Invalid Parameter", msg, actionName);
            return false;
        }
        catch (Exception ex)
        {
            var demystified = ex.Demystify();
            transaction.Finish(SpanStatus.UnknownError);

            var msg = userFriendlyErrorMessage ?? $"Action '{actionName}' failed: {ex.Message}";
            AppLogger.Error(string.Create(CultureInfo.InvariantCulture, $"[Action:Failed] {callerType}.{actionName}()"), demystified, member: actionName, path: callerPath, line: callerLine);
            ToastNotificationService.Instance.ShowToast("Action Error", msg, actionName);
            return false;
        }
    }
}