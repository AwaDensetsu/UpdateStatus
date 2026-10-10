'use strict';
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync('steam-reader/SteamUiBridge.js', 'utf8');
let passed = 0;
async function run(options = {}) {
    const calls = [], timers = [], handles = [];
    const progress = Array.from({ length: 7 }, () => ({ bytes_in_progress: 0, bytes_total: 0 }));
    progress[2] = { bytes_in_progress: 3100, bytes_total: 5000 };
    progress[3] = { bytes_in_progress: 9300, bytes_total: 10000 };
    const item = { appid: 550, completed: false, paused: false, active: true, queue_index: 0, update_result: 0,
        launch_on_completion: false, update_type_info: [{ has_update: true, completed_update: false,
            progress, overall_percent_complete: 93, overall_estimated_time_remaining_sec: 42 }] };
    Object.assign(item, options.item);
    const overview = Object.assign({ remote_client_id: options.legacy ? undefined : '0', paused: false, update_appid: 550,
        update_state: 'Updating', update_state_flags: 97, progress, overall_percent_complete: 93,
        overall_estimated_time_remaining_sec: 42, update_network_bytes_per_second: 500000 }, options.overview);
    const allItems = options.noItem ? [] : [item];
    const downloads = {
        RegisterForDownloadOverview(callback) {
            const handle = { unregister() { calls.push('unregister-overview'); } }; handles.push(handle);
            if (!options.noOverview) callback(overview);
            return handle;
        },
        RegisterForDownloadItems(changed) {
            const handle = { Unregister() { calls.push('unregister-items'); } }; handles.push(handle);
            if (!options.noItems) changed(true, options.flat || options.legacy ? allItems : [
                { remote_client_id: '7', item_data: [{ ...item, appid: 551 }] },
                { remote_client_id: '0', item_data: allItems }
            ]);
            return handle;
        },
        EnableAllDownloads(...args) { assert.equal(args.length, options.legacy ? 1 : 2); if (!options.legacy) assert.equal(args[1], '0'); calls.push(['enable', ...args]); },
        ResumeAppUpdate(...args) { assert.equal(args.length, options.legacy ? 1 : 2); if (!options.legacy) assert.equal(args[1], '0'); calls.push(['resume', ...args]); },
        RemoveFromDownloadList(...args) { assert.equal(args.length, options.legacy ? 1 : 2); if (!options.legacy) assert.equal(args[1], '0'); calls.push(['remove', ...args]); }
    };
    if (options.missingControl) delete downloads[options.missingControl];
    const client = { Downloads: downloads, Apps: { CancelLaunch(id) { calls.push(['cancel', id]); } } };
    if (options.noCancel) delete client.Apps.CancelLaunch;
    const context = { SteamClient: client,
        appStore: { GetAppOverviewByAppID() { return { local_per_client_data: { installed: true } }; } },
        setTimeout(callback) { timers.push(callback); return 1; }, clearTimeout() {} };
    context.window = context;
    const promise = vm.runInNewContext(source, context)((options.ids || [550]), options.action || null, options.selectedApp ?? 550);
    if (options.noItems || options.noOverview || options.remoteOverviewOnly) timers.forEach(callback => callback());
    return { data: JSON.parse(await promise), calls, mutations: calls.filter(Array.isArray) };
}
async function check(name, test) { await test(); ++passed; console.log('PASS ' + name); }
(async () => {
    await check('local snapshot ignores remote items and unregisters both synchronous callbacks', async () => {
        const result = await run(); assert.equal(result.data.Apps[0].AppId, 550);
        assert.equal(result.data.Apps[0].ItemPresent, true); assert.equal(result.data.QueueCount, 1);
        assert.deepEqual(result.calls, ['unregister-overview', 'unregister-items']);
    });
    await check('read-only snapshot never changes download or launch state', async () => {
        assert.deepEqual((await run()).mutations, []);
    });
    await check('download bytes and work percentage remain independent', async () => {
        const { data } = await run(); assert.equal(data.Download.Done / data.Download.Total, 0.62);
        assert.equal(data.WorkPercent, 93); assert.equal(data.Apps[0].WorkPercent, 93);
    });
    await check('older flat item callback remains readable', async () => {
        const { data } = await run({ flat: true }); assert.equal(data.Apps[0].ItemPresent, true);
    });
    await check('missing callback cleans up and refuses all mutations', async () => {
        const result = await run({ noOverview: true, action: 'pause' }); assert.equal(result.data.Available, false);
        assert.deepEqual(result.mutations, []); assert.equal(result.calls.length, 2);
    });
    await check('remote overview cannot authorize a local mutation', async () => {
        const { data, mutations } = await run({ overview: { remote_client_id: '7' }, remoteOverviewOnly: true, action: 'pause' });
        assert.equal(data.Available, false); assert.deepEqual(mutations, []);
    });
    await check('Pause freezes global downloads without removing queue membership', async () => {
        const { data, mutations } = await run({ action: 'pause' }); assert.equal(data.Command, 'accepted');
        assert.deepEqual(mutations, [['enable', false, '0']]);
    });
    await check('close cancels pending launch and removes only selected app', async () => {
        const { mutations } = await run({ action: 'close', item: { launch_on_completion: true } });
        assert.deepEqual(mutations, [['cancel', '550'], ['enable', false, '0'], ['remove', 550, '0']]);
    });
    await check('Resume prioritizes selected update before enabling downloads', async () => {
        const { mutations } = await run({ action: 'resume' });
        assert.deepEqual(mutations, [['resume', 550, '0'], ['enable', true, '0']]);
    });
    await check('Select cannot resume a missing or completed item', async () => {
        for (const extra of [{ noItem: true }, { item: { completed: true } }]) {
            const result = await run({ action: 'select', ...extra }); assert.equal(result.data.Command, 'rejected');
            assert.deepEqual(result.mutations, []);
        }
    });
    await check('foreign selection and unknown commands cannot change downloads', async () => {
        for (const options of [{ action: 'pause', selectedApp: 551 }, { action: 'other' }]) {
            const result = await run(options); assert.equal(result.data.Command, 'rejected'); assert.deepEqual(result.mutations, []);
        }
    });
    await check('missing control and missing launch cancellation reject before mutation', async () => {
        for (const options of [{ missingControl: 'RemoveFromDownloadList' }, { noCancel: true, item: { launch_on_completion: true } }]) {
            const result = await run({ action: 'close', ...options }); assert.equal(result.data.Command, 'rejected'); assert.deepEqual(result.mutations, []);
        }
    });
    await check('older named overview counters are supported without invented work percentage', async () => {
        const result = await run({ overview: { progress: undefined, overall_percent_complete: undefined,
            update_bytes_downloaded: 10, update_bytes_to_download: 100, update_bytes_staged: 30, update_bytes_to_stage: 100 } });
        assert.deepEqual(result.data.Download, { Done: 10, Total: 100 }); assert.equal(result.data.WorkPercent, null);
    });
    await check('large 1024-game catalog stays within the bounded UI message limit', async () => {
        const result = await run({ noItem: true, ids: Array.from({ length: 1024 }, (_, i) => i + 1) });
        assert.equal(result.data.Apps.length, 1024); assert.ok(JSON.stringify(result.data).length < 1048576);
    });
    await check('legacy flat callback uses the one-argument control contract', async () => {
        for (const action of ['pause', 'resume', 'close']) {
            const result = await run({ legacy: true, action });
            assert.equal(result.data.Command, 'accepted');
            assert.ok(result.mutations.every(call => call.length === 2));
        }
    });
    await check('wrapped local items supply the client ID when overview omits it', async () => {
        const result = await run({ action: 'pause', overview: { remote_client_id: undefined } });
        assert.equal(result.data.Command, 'accepted');
        assert.deepEqual(result.mutations, [['enable', false, '0']]);
    });
    console.log(passed + ' Steam UI bridge checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });