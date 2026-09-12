using ReforgerRcon.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;

namespace ReforgerRcon.Services;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpdateAvailable,
    Downloading,
    ReadyToRestart,
    Failed
}

public sealed class UpdateService : IDisposable
{
    private const string ThreadIdKey = "thread_id";
    private const string CurrentVersionKey = "current_version";
    private const string TargetVersionKey = "target_version";
    private const string ElapsedMsKey = "elapsed_ms";
    private const string MemoryWorkingSetMbKey = "ram_mb";
    private const string IsPortableKey = "is_portable";
    private const string ExceptionTypeKey = "exception_type";
    private const string ErrorMessageKey = "error_message";
    private const string StackTraceKey = "stack_trace";

    [SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Official GitHub repository endpoint for ARRT releases")]
    public const string GitHubRepoUrl = "https://github.com/mrwan74/ArmaReforgerRCONTool";

    public static UpdateService Instance { get; } = new();

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private UpdateManager? _updateManager;
    private UpdateInfo? _pendingUpdate;
    private bool _isDisposed;

    public UpdateStatus CurrentStatus { get; private set; } = UpdateStatus.Idle;
    public int DownloadPercentage { get; private set; }
    public string StatusDetails { get; private set; } = "Ready";
    public string? TargetVersionString => _pendingUpdate?.TargetFullRelease.Version.ToString();
    public string CurrentVersionString => _updateManager?.CurrentVersion?.ToString() ?? "0.9.0 (Dev)";

    public bool CanUpdate => _updateManager?.CurrentVersion != null;
    public static bool IsPortableMode => VelopackLocator.Current.IsPortable;

    public event Action? StateChanged;

    private UpdateService()
    {
        InitializeEngine();
    }

    private void InitializeEngine()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            ["repo_url"] = GitHubRepoUrl,
            [ThreadIdKey] = Environment.CurrentManagedThreadId,
            [MemoryWorkingSetMbKey] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1),
            ["os_description"] = RuntimeInformation.OSDescription,
            ["process_architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["clr_runtime"] = RuntimeInformation.FrameworkDescription
        };

        AppLogger.Debug("[UpdateService:Init] Commencing Velopack update subsystem initialization...", context);

        try
        {
            AppLogger.Trace("[UpdateService:Init] Instantiating FilteredGithubSource and platform-specific locator...", context);
            var source = new FilteredGithubSource(GitHubRepoUrl, accessToken: null, prerelease: true);
            var locator = VelopackLocator.CreateDefaultForPlatform(logger: VelopackLoggerBridge.Instance);

            AppLogger.Trace($"[UpdateService:Init] WindowsVelopackLocator resolved: AppContentDir='{locator.AppContentDir}', RootAppDir='{locator.RootAppDir}', PackagesDir='{locator.PackagesDir}', IsPortable={locator.IsPortable}", context);

            _updateManager = new UpdateManager(source, options: null, locator: locator);

            var currentVer = _updateManager.CurrentVersion;
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

            context[IsPortableKey] = locator.IsPortable;
            context["can_update"] = currentVer != null;
            context[CurrentVersionKey] = currentVer?.ToString();
            context["app_id"] = _updateManager.AppId;
            context["root_app_dir"] = locator.RootAppDir;
            context["packages_dir"] = locator.PackagesDir;
            context["app_content_dir"] = locator.AppContentDir;
            context["app_temp_dir"] = locator.AppTempDir;
            context[ElapsedMsKey] = elapsedMs;

            if (currentVer != null)
            {
                var modeName = locator.IsPortable ? "Portable" : "Installed";
                StatusDetails = $"ARRT v{currentVer} ({modeName})";
                AppLogger.Info($"[UpdateService:Init] Velopack engine ready for v{currentVer} in {modeName} mode in {elapsedMs:F2}ms.", context);
            }
            else
            {
                StatusDetails = "Development Build (Auto-updates inactive)";
                AppLogger.Warn("[UpdateService:Init] Running unbundled out of bin/Debug. Update checking disabled.", null, context);
            }
        }
        catch (UnauthorizedAccessException authEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            context[ExceptionTypeKey] = authEx.GetType().FullName;
            context[ErrorMessageKey] = authEx.Message;
            context[StackTraceKey] = authEx.StackTrace;

            AppLogger.Fatal("[UpdateService:Init] Operating system permission denied initializing Velopack update directories.", authEx, context);
            StatusDetails = "Update engine initialization failed (Access Denied)";
            ToastNotificationService.Instance.ShowError("Update Engine Failure", "Permission denied accessing update storage: " + authEx.Message);
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            context[ExceptionTypeKey] = ex.GetType().FullName;
            context[ErrorMessageKey] = ex.Message;
            context[StackTraceKey] = ex.StackTrace;

            AppLogger.Fatal("[UpdateService:Init] Unexpected failure initializing Velopack UpdateManager.", ex, context);
            StatusDetails = "Update engine initialization failed";
            ToastNotificationService.Instance.ShowError("Update Engine Failure", "Failed initializing auto-update subsystem: " + ex.Message);
        }
    }

    public async Task<bool> CheckForUpdatesAsync(bool isManual = true, CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            ["is_manual"] = isManual,
            ["repo_url"] = GitHubRepoUrl,
            [CurrentVersionKey] = CurrentVersionString,
            [IsPortableKey] = IsPortableMode,
            [ThreadIdKey] = Environment.CurrentManagedThreadId,
            [MemoryWorkingSetMbKey] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1)
        };

        AppLogger.Debug($"[UpdateService:Check] Checking for updates (Manual={isManual}, CurrentVersion='{CurrentVersionString}')...", context);

        if (_updateManager?.CurrentVersion == null)
        {
            AppLogger.Warn("[UpdateService:Check] Check aborted: App is running in unbundled/debug mode without a Velopack manifest.", null, context);
            if (isManual)
            {
                ToastNotificationService.Instance.ShowWarning(
                    "Updates Inactive",
                    "Auto-updates are only active when running from a packaged release."
                );
            }
            return false;
        }

        try
        {
            AppLogger.Trace("[UpdateService:Check] Acquiring exclusive update synchronization lock...", context);
            await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            AppLogger.Trace("[UpdateService:Check] Synchronization lock acquired.", context);
        }
        catch (OperationCanceledException opEx)
        {
            AppLogger.Trace($"[UpdateService:Check] Lock acquisition cancelled by caller: {opEx.Message}", context);
            return false;
        }

        try
        {
            SetState(UpdateStatus.Checking, "Querying GitHub pre-releases...");
            AppLogger.Info($"[UpdateService:Check] Dispatching HTTP GET to GitHub API for releases.win.json via '{GitHubRepoUrl}'...", context);

            _pendingUpdate = await _updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;

            if (_pendingUpdate != null)
            {
                var targetVer = _pendingUpdate.TargetFullRelease.Version.ToString();
                context[TargetVersionKey] = targetVer;
                context["is_downgrade"] = _pendingUpdate.IsDowngrade;
                context["target_package_filename"] = _pendingUpdate.TargetFullRelease.FileName;
                context["target_package_size_bytes"] = _pendingUpdate.TargetFullRelease.Size;

                SetState(UpdateStatus.UpdateAvailable, $"New update available: v{targetVer}");
                AppLogger.Info($"[UpdateService:Check] Remote update located: v{targetVer} in {elapsedMs:F2}ms (Package='{_pendingUpdate.TargetFullRelease.FileName}', Size={_pendingUpdate.TargetFullRelease.Size / (1024.0 * 1024.0):F2} MB).", context);

                AppLogger.TrackEvent("update_discovered", new Dictionary<string, object>
                {
                    [CurrentVersionKey] = CurrentVersionString,
                    [TargetVersionKey] = targetVer,
                    [IsPortableKey] = IsPortableMode,
                    ["is_downgrade"] = _pendingUpdate.IsDowngrade,
                    ["duration_ms"] = elapsedMs
                });

                return true;
            }

            SetState(UpdateStatus.Idle, $"ARRT v{CurrentVersionString} is up to date");
            AppLogger.Info($"[UpdateService:Check] Release feed is current. No newer version available ({elapsedMs:F2}ms).", context);

            if (isManual)
            {
                ToastNotificationService.Instance.ShowToast(
                    "Latest Version",
                    $"You are running the latest version (v{CurrentVersionString})."
                );
            }

            return false;
        }
        catch (NotInstalledException notInstalledEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            AppLogger.Warn($"[UpdateService:Check] Application is running outside packaged directory structure: {notInstalledEx.Message}", notInstalledEx, context);
            SetState(UpdateStatus.Idle, "Updates unavailable (unbundled build)");
            return false;
        }
        catch (HttpRequestException httpEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            context["http_code"] = httpEx.StatusCode?.ToString() ?? "None";
            context["http_request_error"] = httpEx.HttpRequestError.ToString();
            context[ErrorMessageKey] = httpEx.Message;

            AppLogger.Error($"[UpdateService:Check] HTTP network error querying GitHub API after {elapsedMs:F2}ms (Status: {httpEx.StatusCode}): {httpEx.Message}", httpEx, context);
            SetState(UpdateStatus.Failed, "Network failure reaching GitHub");

            if (isManual)
            {
                ToastNotificationService.Instance.ShowError(
                    "Update Check Failed",
                    $"GitHub unreachable ({httpEx.StatusCode?.ToString() ?? "No Response"}): Verify network connectivity and repository visibility."
                );
            }
            return false;
        }
        catch (SocketException sockEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            context["socket_code"] = sockEx.SocketErrorCode.ToString();
            context["native_error_code"] = sockEx.NativeErrorCode;
            context[ErrorMessageKey] = sockEx.Message;

            AppLogger.Error($"[UpdateService:Check] DNS or Socket exception connecting to GitHub after {elapsedMs:F2}ms: {sockEx.Message}", sockEx, context);
            SetState(UpdateStatus.Failed, "DNS/Socket error reaching GitHub");

            if (isManual)
            {
                ToastNotificationService.Instance.ShowError(
                    "Network Error",
                    $"Could not establish connection to GitHub: {sockEx.SocketErrorCode}"
                );
            }
            return false;
        }
        catch (OperationCanceledException opEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            AppLogger.Info($"[UpdateService:Check] Update check was cancelled by operator or timeout after {elapsedMs:F2}ms: {opEx.Message}", context);
            SetState(UpdateStatus.Idle, "Check cancelled");
            return false;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            context[ExceptionTypeKey] = ex.GetType().FullName;
            context[ErrorMessageKey] = ex.Message;
            context[StackTraceKey] = ex.StackTrace;

            AppLogger.Fatal("[UpdateService:Check] Fatal unhandled exception during update check.", ex, context);
            SetState(UpdateStatus.Failed, $"Check failed: {ex.Message}");

            if (isManual)
            {
                ToastNotificationService.Instance.ShowError(
                    "Update Engine Error",
                    $"Failed checking for updates: {ex.Message}"
                );
            }
            return false;
        }
        finally
        {
            _syncLock.Release();
            AppLogger.Trace("[UpdateService:Check] Released update synchronization lock.", context);
        }
    }

    public async Task<bool> DownloadUpdateAsync(CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            [TargetVersionKey] = TargetVersionString,
            [IsPortableKey] = IsPortableMode,
            [ThreadIdKey] = Environment.CurrentManagedThreadId,
            [MemoryWorkingSetMbKey] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1)
        };

        if (_updateManager == null || _pendingUpdate == null)
        {
            AppLogger.Warn("[UpdateService:Download] Download aborted: UpdateManager or TargetRelease is null.", null, context);
            return false;
        }

        try
        {
            AppLogger.Trace("[UpdateService:Download] Acquiring update synchronization lock for download...", context);
            await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException opEx)
        {
            AppLogger.Trace($"[UpdateService:Download] Lock wait cancelled: {opEx.Message}", context);
            return false;
        }

        try
        {
            DownloadPercentage = 0;
            SetState(UpdateStatus.Downloading, "Downloading update package (0%)...");
            AppLogger.Info($"[UpdateService:Download] Commencing download workflow for v{TargetVersionString} (Package: '{_pendingUpdate.TargetFullRelease.FileName}', Size: {_pendingUpdate.TargetFullRelease.Size / (1024.0 * 1024.0):F2} MB)...", context);

            int lastLoggedPercentage = -1;

            await _updateManager.DownloadUpdatesAsync(_pendingUpdate, progress =>
            {
                DownloadPercentage = progress;
                SetState(UpdateStatus.Downloading, $"Downloading update ({progress}%)...");

                if (progress % 10 == 0 && progress != lastLoggedPercentage)
                {
                    lastLoggedPercentage = progress;
                    AppLogger.Trace($"[UpdateService:Download] Progress milestone: {progress}% for target v{TargetVersionString} (Thread={Environment.CurrentManagedThreadId})");
                }
            }, cancellationToken).ConfigureAwait(false);

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;

            SetState(UpdateStatus.ReadyToRestart, $"Update v{TargetVersionString} ready to install");
            AppLogger.Info($"[UpdateService:Download] Update payload for v{TargetVersionString} successfully downloaded and verified in {elapsedMs:F2}ms.", context);

            AppLogger.TrackEvent("update_downloaded", new Dictionary<string, object>
            {
                [TargetVersionKey] = TargetVersionString ?? "unknown",
                [IsPortableKey] = IsPortableMode,
                ["duration_ms"] = elapsedMs
            });

            return true;
        }
        catch (ChecksumFailedException checkEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            context[ErrorMessageKey] = checkEx.Message;

            AppLogger.Error($"[UpdateService:Download] Checksum verification failed after {elapsedMs:F2}ms: {checkEx.Message}", checkEx, context);
            SetState(UpdateStatus.Failed, "Package checksum mismatch");
            ToastNotificationService.Instance.ShowError("Security Alert", "Downloaded package failed cryptographic SHA1 verification.");
            return false;
        }
        catch (AcquireLockFailedException lockEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;

            AppLogger.Warn($"[UpdateService:Download] Concurrent lock contention (.velopack_lock exists): {lockEx.Message}", lockEx, context);
            SetState(UpdateStatus.Failed, "Another update is in progress");
            ToastNotificationService.Instance.ShowWarning("Update Locked", "Another update or download instance is currently active.");
            return false;
        }
        catch (OperationCanceledException opEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            AppLogger.Info($"[UpdateService:Download] Update download was cancelled after {elapsedMs:F2}ms: {opEx.Message}", context);
            SetState(UpdateStatus.Failed, "Download cancelled");
            return false;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            context[ExceptionTypeKey] = ex.GetType().FullName;
            context[ErrorMessageKey] = ex.Message;
            context[StackTraceKey] = ex.StackTrace;

            AppLogger.Fatal("[UpdateService:Download] Critical fault during update package download.", ex, context);
            SetState(UpdateStatus.Failed, $"Download failed: {ex.Message}");
            ToastNotificationService.Instance.ShowError("Download Failed", $"Failed downloading update: {ex.Message}");
            return false;
        }
        finally
        {
            _syncLock.Release();
            AppLogger.Trace("[UpdateService:Download] Released download synchronization lock.", context);
        }
    }

    public void RestartAndApply()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            [TargetVersionKey] = TargetVersionString,
            [CurrentVersionKey] = CurrentVersionString,
            [IsPortableKey] = IsPortableMode,
            [ThreadIdKey] = Environment.CurrentManagedThreadId,
            [MemoryWorkingSetMbKey] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1)
        };

        if (_updateManager == null || _pendingUpdate == null)
        {
            AppLogger.Warn("[UpdateService:Apply] Restart aborted: No verified update package staged in memory.", null, context);
            ToastNotificationService.Instance.ShowWarning("Apply Notice", "No verified update is ready to install.");
            return;
        }

        try
        {
            AppLogger.Info($"[UpdateService:Apply] Applying update to v{TargetVersionString} and executing application restart sequence...", context);

            AppLogger.TrackEvent("update_applied_restarting", new Dictionary<string, object>
            {
                [TargetVersionKey] = TargetVersionString ?? "unknown",
                [IsPortableKey] = IsPortableMode
            });

            AppLogger.Trace("[UpdateService:Apply] Flushing file logger and diagnostic Sentry transports prior to process termination...", context);
            AppLogger.Flush();

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[UpdateService:Apply] Calling Velopack ApplyUpdatesAndRestart (PreShutdownDuration={elapsedMs:F2}ms)...", context);

            _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
        }
        catch (Exception ex)
        {
            context[ExceptionTypeKey] = ex.GetType().FullName;
            context[ErrorMessageKey] = ex.Message;
            context[StackTraceKey] = ex.StackTrace;

            AppLogger.Fatal("[UpdateService:Apply] Critical failure invoking ApplyUpdatesAndRestart.", ex, context);
            ToastNotificationService.Instance.ShowError("Restart Failed", $"Unable to restart and apply update: {ex.Message}");
        }
    }

    private void SetState(UpdateStatus status, string details)
    {
        CurrentStatus = status;
        StatusDetails = details;
        AppLogger.Trace($"[UpdateService:State] State Transition -> {status} ('{details}', Thread={Environment.CurrentManagedThreadId})");

        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[UpdateService:State] Uncaught exception in StateChanged event subscriber: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        var start = Stopwatch.GetTimestamp();
        try
        {
            AppLogger.Trace("[UpdateService:Dispose] Disposing synchronization lock and unregistering updater resources...");
            _syncLock.Dispose();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[UpdateService:Dispose] UpdateService disposal finalized in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[UpdateService:Dispose] Exception disposing sync lock: {ex.Message}", ex);
        }
    }
}