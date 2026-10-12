'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const bridge = fs.readFileSync(require('node:path').join(__dirname, '../src/Launchers/Epic/EpicUiBridge.js'), 'utf8');
const product = {appname:'app', namespace:'ns', catalogid:'catalog', isinitialized:true, isinstalled:true,
    hasupdate:false, isupdating:false, isinupdatequeue:false, isrunning:false,
    progress:90, updatestatus:'Please Wait...', accountToken:'must not escape', installlocation:'private path'};
let count = 0;
async function read(installed = [product], queued = [], version = 3, downloadVersion = 1.1, encodeProducts = false) {
    const calls = [];
    const payload = values => encodeProducts ? JSON.stringify({products:values}) : {products:values};
    const bind = (name, value) => async (...args) => { assert.equal(args.length, 0); calls.push(name); return value; };
    const result = await vm.runInNewContext(bridge, {window:{ue:{
        productinfo:{getapiversion:bind('product.version',version), getinstalledapps:bind('installed',payload(installed)),
            getqueuedapps:bind('queued',payload(queued))},
        downloadmanager:{getapiversion:bind('download.version',downloadVersion)}
    }}});
    assert.deepEqual(calls.sort(), ['download.version','installed','product.version','queued'].sort());
    return JSON.parse(result);
}
async function check(name, run) { await run(); count++; console.log('PASS '+name); }
(async () => {
    await check('idle Please Wait is not an update', async () => {
        const result = await read(); assert.equal(result.products[0].updating, false);
        assert.equal(result.products[0].hasUpdate, false);
    });
    await check('account paths and unvalidated counters are not returned', async () => {
        const text = JSON.stringify(await read());
        for (const value of ['accountToken','must not escape','installlocation','private path','progress','updatestatus'])
            assert.equal(text.includes(value), false);
    });
    await check('installed and queued products keep exact triple identity', async () => {
        const result = await read([], [{...product, isinupdatequeue:true}]);
        assert.equal(result.products[0].queued,true); assert.equal(result.products[0].catalog,'catalog');
    });
    await check('identical triples deduplicate', async () => {
        assert.equal((await read([product],[product])).products.length,1);
    });
    await check('partial duplicate preserves all known flags from the installed record', async () => {
        const partial = {appname:product.appname,namespace:product.namespace,catalogid:product.catalogid};
        const result = (await read([{...product,hasupdate:true,isupdating:true}], [partial])).products[0];
        assert.equal(result.initialized,true); assert.equal(result.installed,true);
        assert.equal(result.hasUpdate,true); assert.equal(result.updating,true); assert.equal(result.running,false);
    });
    await check('partial-first and full-first merging produce the same known values', async () => {
        const partial = {appname:product.appname,namespace:product.namespace,catalogid:product.catalogid};
        assert.deepEqual(await read([partial],[product]), await read([product],[partial]));
    });
    await check('missing initialized and installed flags remain unknown', async () => {
        const result = (await read([{appname:'app',namespace:'ns',catalogid:'catalog'}])).products[0];
        for (const field of ['initialized','installed','hasUpdate','updating','queued','running']) assert.equal(result[field],null);
    });
    await check('non-boolean duplicate fields cannot erase known flags', async () => {
        const result = (await read([product],[{...product,isinitialized:'true',isinstalled:null,hasupdate:0,
            isupdating:undefined,isinupdatequeue:'false',isrunning:undefined}])).products[0];
        assert.equal(result.initialized,true); assert.equal(result.installed,true);
        for (const field of ['hasUpdate','updating','queued','running']) assert.equal(result[field],false);
    });
    await check('explicit disagreement becomes unknown instead of choosing a list', async () => {
        const result = (await read([product],[{...product,isinitialized:false,isinstalled:false,hasupdate:true,
            isupdating:true,isinupdatequeue:true,isrunning:true}])).products[0];
        for (const field of ['initialized','installed','hasUpdate','updating','queued','running']) assert.equal(result[field],null);
    });
    await check('conflict remains unknown after a third duplicate repeats a value', async () => {
        const result = (await read([product],[{...product,isupdating:true},product])).products[0];
        assert.equal(result.updating,null); assert.equal(result.installed,true);
    });
    await check('merged conflicting products keep account values and counters private', async () => {
        const text = JSON.stringify(await read([product],[{...product,isupdating:true,accountToken:'another token'}]));
        for (const value of ['accountToken','another token','must not escape','installlocation','private path','progress'])
            assert.equal(text.includes(value),false);
    });
    await check('colliding app names in different namespaces remain distinguishable', async () => {
        assert.equal((await read([product,{...product,namespace:'other'}])).products.length,2);
    });
    await check('missing booleans remain unknown', async () => {
        assert.equal((await read([{...product,isupdating:undefined}])).products[0].updating,null);
    });
    await check('malformed identity is ignored', async () => {
        assert.equal((await read([{...product,catalogid:''}])).products.length,0);
    });
    await check('unsupported API version rejects reading', async () => { await assert.rejects(read([],[],99)); });
    await check('native rejection propagates', async () => {
        await assert.rejects(vm.runInNewContext(bridge,{window:{ue:{productinfo:{
            getapiversion:async()=>{throw Error('disconnected');}},downloadmanager:{getapiversion:async()=>1.1}}}}));
    });
    await check('no subscriptions, download actions or launches exist in the bridge', async () => {
        assert.equal(/register|onwebloaded|pauseactiveinstallation|resumeactiveinstallation|cancelactiveinstallation|\.launch\(/i.test(bridge),false);
    });
    await check('actual Epic JSON numeric versions 3.0 and 1.1 connect', async () => {
        const result = await read([product], [], '3.0', '1.1');
        assert.equal(result.products[0].app, 'app');
    });
    await check('JSON encoded product lists decode before validation', async () => {
        const result = await read([product], [], '3.0', '1.1', true);
        assert.equal(result.products[0].updating, false);
    });
    await check('unsupported JSON numeric API version still rejects', async () => {
        await assert.rejects(read([], [], '99.0', '1.1'));
    });
    await check('malformed JSON native response fails closed', async () => {
        await assert.rejects(read([], [], 'not JSON', '1.1'));
    });
    console.log(count+' Epic bridge checks passed');
})().catch(error => { console.error(error); process.exitCode=1; });
