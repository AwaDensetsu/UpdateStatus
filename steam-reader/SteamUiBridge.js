(function (ids, action, selectedApp) {
    const downloads = window.SteamClient && SteamClient.Downloads;
    if (!downloads || typeof downloads.RegisterForDownloadOverview !== 'function' ||
        typeof downloads.RegisterForDownloadItems !== 'function') {
        return Promise.resolve(JSON.stringify({ Available: false }));
    }

    return new Promise(resolve => {
        const subscriptions = [];
        let overview, items, localClientId = null, registered = false, finished = false;
        const timeout = setTimeout(finish, 1200);
        // Steam UI progress uses semantic stage indices, not native memory offsets.
        const downloadStage = 2, stagingStage = 3;
        const integer = value => Number.isSafeInteger(value) ? value : null;
        const boolean = value => typeof value === 'boolean' ? value : null;
        const counter = (progress, index, done, total) => {
            const row = Array.isArray(progress) && progress[index];
            return { Done: integer(row ? row.bytes_in_progress : done), Total: integer(row ? row.bytes_total : total) };
        };

        function finish() {
            if (finished) return;
            finished = true;
            clearTimeout(timeout);
            for (const subscription of subscriptions) {
                try {
                    if (subscription && typeof subscription.unregister === 'function') subscription.unregister();
                    else if (subscription && typeof subscription.Unregister === 'function') subscription.Unregister();
                } catch (_) { }
            }
            if (!registered || !overview || !items) {
                resolve(JSON.stringify({ Available: false }));
                return;
            }
            const controls = ['EnableAllDownloads', 'ResumeAppUpdate', 'RemoveFromDownloadList'].every(name => typeof downloads[name] === 'function');
            // Current remote-aware UI commands require the client ID after the value/AppID.
            // Older flat callbacks omit that field and keep the one-argument contract.
            const clientArguments = localClientId === null ? [] : [localClientId];
            let command = null;
            try {
                if (action) {
                    const item = items.find(row => row.appid === selectedApp);
                    const cancel = SteamClient.Apps && SteamClient.Apps.CancelLaunch;
                    if (!controls || typeof overview.paused !== 'boolean' || !ids.includes(selectedApp) || selectedApp <= 0) throw new Error('Controls unavailable');
                    if (action === 'pause' || action === 'close') {
                        if (item && item.launch_on_completion) {
                            if (typeof cancel !== 'function') throw new Error('Launch cancellation unavailable');
                            SteamClient.Apps.CancelLaunch(String(selectedApp));
                        }
                        downloads.EnableAllDownloads(false, ...clientArguments);
                        if (action === 'close' && item && (item.active || item.queue_index >= 0)) downloads.RemoveFromDownloadList(selectedApp, ...clientArguments);
                    } else if (action === 'select' || action === 'resume') {
                        if (!item || item.completed !== false || item.update_result > 4) throw new Error('Selected update unavailable');
                        downloads.ResumeAppUpdate(selectedApp, ...clientArguments);
                        downloads.EnableAllDownloads(true, ...clientArguments);
                    } else throw new Error('Unknown command');
                    command = 'accepted';
                }
            } catch (_) { command = 'rejected'; }
            const apps = ids.map(id => {
                const item = items.find(row => row.appid === id);
                const app = window.appStore && typeof appStore.GetAppOverviewByAppID === 'function' && appStore.GetAppOverviewByAppID(id);
                const local = app && (app.local_per_client_data || app.per_client_data && app.per_client_data['0']);
                const content = item && Array.isArray(item.update_type_info) && item.update_type_info[0];
                return {
                    AppId: id, Installed: boolean(local && local.installed), ItemPresent: !!item,
                    Completed: boolean(item && item.completed), Paused: boolean(item && item.paused),
                    Active: boolean(item && item.active), QueueIndex: integer(item && item.queue_index),
                    Result: integer(item && item.update_result), SourceBuild: integer(item && item.buildid),
                    TargetBuild: integer(item && item.target_buildid),
                    ContentRequired: boolean(content && content.has_update && !content.completed_update),
                    Download: counter(content && content.progress, downloadStage, item && item.downloaded_bytes, item && item.total_bytes),
                    Staging: counter(content && content.progress, stagingStage, null, null),
                    WorkPercent: integer(content && content.overall_percent_complete),
                    SecondsRemaining: integer(content && content.overall_estimated_time_remaining_sec)
                };
            });
            resolve(JSON.stringify({
                Available: true, Controls: controls, Command: command,
                DownloadsEnabled: typeof overview.paused === 'boolean' ? !overview.paused : null,
                ActiveAppId: integer(overview.update_appid),
                QueueCount: items.filter(item => !item.completed && item.queue_index >= 0).length,
                UpdateState: integer(overview.update_state_flags),
                State: typeof overview.update_state === 'string' ? overview.update_state : null,
                NonGameContent: !!(overview.update_is_upload || overview.update_is_shader || overview.update_is_workshop),
                Download: counter(overview.progress, downloadStage, overview.update_bytes_downloaded, overview.update_bytes_to_download),
                Staging: counter(overview.progress, stagingStage, overview.update_bytes_staged, overview.update_bytes_to_stage),
                WorkPercent: integer(overview.overall_percent_complete),
                BytesPerSecond: integer(overview.update_network_bytes_per_second),
                SecondsRemaining: integer(overview.overall_estimated_time_remaining_sec), Apps: apps
            }));
        }
        function ready() { if (registered && overview && items) finish(); }
        try {
            subscriptions.push(downloads.RegisterForDownloadOverview(value => {
                if (value && (value.remote_client_id === undefined || String(value.remote_client_id) === '0')) {
                    overview = value;
                    if (value.remote_client_id !== undefined) localClientId = String(value.remote_client_id);
                    ready();
                }
            }));
            subscriptions.push(downloads.RegisterForDownloadItems((changed, rows) => {
                if (!Array.isArray(rows)) return;
                if (rows.some(row => Array.isArray(row.item_data))) {
                    const local = rows.find(row => String(row.remote_client_id) === '0');
                    if (local) { items = local.item_data; localClientId = String(local.remote_client_id); }
                } else items = rows;
                ready();
            }));
            registered = true;
            ready();
        } catch (_) { finish(); }
    });
})
