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

    [SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Official GitHub repository endpoint for ARRT releases")]
    public const string GitHubRepoUrl = "https://github.com/Marwan3020/ARRT";

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
        var sw = Stopwatch.StartNew();
        var context = new Dictionary<string, object?>
        {
            ["repo_url"] = GitHubRepoUrl,
            [ThreadIdKey] = Environment.CurrentManagedThreadId,
            [MemoryWorkingSetMbKey] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1)
        };

        try
        {
            AppLogger.Debug("[UpdateService:Init] Initializing Velopack portable update manager with bridged logger...", context);

            var source = new GithubSource(GitHubRepoUrl, accessToken: null, prerelease: true);
            var locator = VelopackLocator.CreateDefaultForPlatform(logger: VelopackLoggerBridge.Instance);
            _updateManager = new UpdateManager(source, options: null, locator: locator);

            var currentVer = _updateManager.CurrentVersion;
            context[IsPortableKey] = locator.IsPortable;
            context["can_update"] = currentVer != null;
            context[CurrentVersionKey] = currentVer?.ToString();
            context["app_id"] = _updateManager.AppId;
            context["root_app_dir"] = locator.RootAppDir;
            context["packages_dir"] = locator.PackagesDir;

            if (currentVer != null)
            {
                var modeName = locator.IsPortable ? "Portable" : "Installed";
                StatusDetails = $"ARRT v{currentVer} ({modeName})";
                AppLogger.Info($"[UpdateService:Init] Velopack engine active for v{currentVer} in {modeName} mode ({sw.ElapsedMilliseconds}ms).", context);
            }
            else
            {
                StatusDetails = "Development Build (Visual Studio F5 - Auto-updates inactive)";
                AppLogger.Debug("[UpdateService:Init] Running unbundled out of bin/Debug. Update checking disabled.", context);
            }
        }
        catch (Exception ex)
        {
            context["exception_type"] = ex.GetType().FullName;
            context["error"] = ex.Message;
            AppLogger.Error("[UpdateService:Init] Failed to initialize Velopack UpdateManager.", ex, context);
            StatusDetails = "Update engine initialization failed";
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

        if (_updateManager?.CurrentVersion == null)
        {
            AppLogger.Warn("[UpdateService:Check] Update check bypassed: App is running from raw bin/Debug without Velopack package context.", null, context);
            if (isManual)
            {
                ToastNotificationService.Instance.ShowWarning(
                    "Updates Inactive",
                    "Auto-updates are only active when running from a packaged release zip."
                );
            }
            return false;
        }

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(UpdateStatus.Checking, "Querying GitHub pre-releases...");
            AppLogger.Info("[UpdateService:Check] Querying GitHub API for releases.win.json manifest...", context);

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
                AppLogger.Info($"[UpdateService:Check] New pre-release discovered: v{targetVer} in {elapsedMs:F2}ms (Package='{_pendingUpdate.TargetFullRelease.FileName}', Size={_pendingUpdate.TargetFullRelease.Size / (1024.0 * 1024.0):F2} MB).", context);

                AppLogger.TrackEvent("update_discovered", new Dictionary<string, object>
                {
                    [CurrentVersionKey] = CurrentVersionString,
                    [TargetVersionKey] = targetVer,
                    [IsPortableKey] = IsPortableMode,
                    ["is_downgrade"] = _pendingUpdate.IsDowngrade,
                    ["duration_ms"] = elapsedMs
                });

                ToastNotificationService.Instance.ShowToast(
                    "Update Available",
                    $"ARRT v{targetVer} is available. Click Download in Settings to update.",
                    "UPDATE_NOTIFY"
                );
                return true;
            }

            SetState(UpdateStatus.Idle, $"ARRT v{CurrentVersionString} is up to date");
            AppLogger.Info($"[UpdateService:Check] No updates found. Running latest release ({elapsedMs:F2}ms).", context);

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
            AppLogger.Warn($"[UpdateService:Check] App is running outside package structure: {notInstalledEx.Message}", notInstalledEx, context);
            SetState(UpdateStatus.Idle, "Updates unavailable (unbundled build)");
            return false;
        }
        catch (HttpRequestException httpEx)
        {
            context["http_code"] = httpEx.StatusCode?.ToString();
            AppLogger.Error($"[UpdateService:Check] HTTP network error querying GitHub: {httpEx.Message}", httpEx, context);
            SetState(UpdateStatus.Failed, "Network failure reaching GitHub");

            ToastNotificationService.Instance.ShowError(
                "Update Check Failed",
                $"GitHub unreachable ({httpEx.StatusCode}): Verify internet connection."
            );
            return false;
        }
        catch (SocketException sockEx)
        {
            context["socket_code"] = sockEx.SocketErrorCode.ToString();
            AppLogger.Error($"[UpdateService:Check] DNS/Socket error reaching GitHub: {sockEx.Message}", sockEx, context);
            SetState(UpdateStatus.Failed, "DNS/Socket error reaching GitHub");

            ToastNotificationService.Instance.ShowError(
                "Network Error",
                $"Could not establish connection to GitHub: {sockEx.SocketErrorCode}"
            );
            return false;
        }
        catch (Exception ex)
        {
            context["exception_type"] = ex.GetType().FullName;
            AppLogger.Fatal("[UpdateService:Check] Fatal failure during update check.", ex, context);
            SetState(UpdateStatus.Failed, $"Check failed: {ex.Message}");

            ToastNotificationService.Instance.ShowError(
                "Update Engine Error",
                $"Failed checking for updates: {ex.Message}"
            );
            return false;
        }
        finally
        {
            _syncLock.Release();
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
            AppLogger.Warn("[UpdateService:Download] Download aborted: No update pending.", null, context);
            return false;
        }

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DownloadPercentage = 0;
            SetState(UpdateStatus.Downloading, "Downloading update package (0%)...");
            AppLogger.Info($"[UpdateService:Download] Commencing download of v{TargetVersionString} in portable mode...", context);

            int lastLoggedPercentage = -1;

            await _updateManager.DownloadUpdatesAsync(_pendingUpdate, progress =>
            {
                DownloadPercentage = progress;
                SetState(UpdateStatus.Downloading, $"Downloading update ({progress}%)...");

                if (progress % 10 == 0 && progress != lastLoggedPercentage)
                {
                    lastLoggedPercentage = progress;
                    AppLogger.Trace($"[UpdateService:Download] Progress milestone: {progress}% for v{TargetVersionString}");
                }
            }, cancellationToken).ConfigureAwait(false);

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;

            SetState(UpdateStatus.ReadyToRestart, $"Update v{TargetVersionString} ready to install");
            AppLogger.Info($"[UpdateService:Download] Download complete in {elapsedMs:F2}ms.", context);

            AppLogger.TrackEvent("update_downloaded", new Dictionary<string, object>
            {
                [TargetVersionKey] = TargetVersionString ?? "unknown",
                [IsPortableKey] = IsPortableMode,
                ["duration_ms"] = elapsedMs
            });

            ToastNotificationService.Instance.ShowSuccess(
                "Update Ready to Apply",
                $"ARRT v{TargetVersionString} downloaded. Click Restart to apply.",
                "UPDATE_READY",
                undoAction: () =>
                {
                    RestartAndApply();
                    return Task.CompletedTask;
                }
            );

            return true;
        }
        catch (ChecksumFailedException checkEx)
        {
            AppLogger.Error($"[UpdateService:Download] Checksum verification failed: {checkEx.Message}", checkEx, context);
            SetState(UpdateStatus.Failed, "Package checksum mismatch");
            ToastNotificationService.Instance.ShowError("Security Alert", "Downloaded update failed integrity verification.");
            return false;
        }
        catch (AcquireLockFailedException lockEx)
        {
            AppLogger.Warn($"[UpdateService:Download] Concurrent update locked: {lockEx.Message}", lockEx, context);
            SetState(UpdateStatus.Failed, "Another update is in progress");
            ToastNotificationService.Instance.ShowWarning("Update Locked", "Another update operation is currently active.");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Fatal($"[UpdateService:Download] Fatal exception downloading update: {ex.Message}", ex, context);
            SetState(UpdateStatus.Failed, $"Download failed: {ex.Message}");
            ToastNotificationService.Instance.ShowError("Download Failed", $"Failed downloading update: {ex.Message}");
            return false;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public void RestartAndApply()
    {
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
            AppLogger.Warn("[UpdateService:Apply] Restart aborted: No verified update package staged.", null, context);
            ToastNotificationService.Instance.ShowWarning("Apply Notice", "No verified update is ready to install.");
            return;
        }

        try
        {
            AppLogger.Info($"[UpdateService:Apply] Applying portable update to v{TargetVersionString}...", context);

            AppLogger.TrackEvent("update_applied_restarting", new Dictionary<string, object>
            {
                [TargetVersionKey] = TargetVersionString ?? "unknown",
                [IsPortableKey] = IsPortableMode
            });

            AppLogger.Flush();

            // Swaps out the current\ folder inside the portable folder and relaunches
            _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
        }
        catch (Exception ex)
        {
            AppLogger.Fatal($"[UpdateService:Apply] Failed invoking ApplyUpdatesAndRestart: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Restart Failed", $"Unable to apply update: {ex.Message}");
        }
    }

    private void SetState(UpdateStatus status, string details)
    {
        CurrentStatus = status;
        StatusDetails = details;
        AppLogger.Trace($"[UpdateService:State] State: {status} ('{details}')");
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _syncLock.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[UpdateService:Dispose] Notice disposing sync lock: {ex.Message}");
        }
    }
}