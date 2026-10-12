(async () => {
    const api = window.ue;
    if (!api || !api.productinfo || !api.downloadmanager) throw new Error('Epic bindings unavailable');
    // Unreal bindings can return JSON text (for example "3.0") or decoded values.
    // Normalize before comparing versions or inspecting product-list structure.
    const decode = value => typeof value === 'string' ? JSON.parse(value) : value;
    const versions = (await Promise.all([api.productinfo.getapiversion(), api.downloadmanager.getapiversion()])).map(decode);
    if (versions[0] !== 3 || versions[1] !== 1.1) throw new Error('Unsupported Epic bindings');
    const lists = (await Promise.all([api.productinfo.getinstalledapps(), api.productinfo.getqueuedapps()])).map(decode);
    if (!lists.every(list => list && Array.isArray(list.products))) throw new Error('Invalid product list');
    const fields = [
        ['initialized', 'isinitialized'], ['installed', 'isinstalled'],
        ['hasUpdate', 'hasupdate'], ['updating', 'isupdating'],
        ['queued', 'isinupdatequeue'], ['running', 'isrunning']
    ];
    const products = new Map();
    for (const list of lists) {
        for (const item of list.products) {
            if (!item || typeof item.appname !== 'string' || typeof item.namespace !== 'string' ||
                typeof item.catalogid !== 'string' || !item.appname || !item.namespace || !item.catalogid) continue;
            const key = JSON.stringify([item.namespace, item.catalogid, item.appname]);
            let product = products.get(key);
            if (!product) {
                product = { app: item.appname, namespace: item.namespace, catalog: item.catalogid };
                products.set(key, product);
            }
            for (const [field, native] of fields) {
                const value = item[native];
                if (typeof value !== 'boolean') continue;
                if (product[field] === undefined) product[field] = value;
                // No source-priority contract has been validated. A disagreement
                // remains unknown even if a third duplicate repeats one value.
                else if (product[field] !== value) product[field] = null;
            }
        }
    }
    const values = Array.from(products.values(), product => {
        const value = { app: product.app, namespace: product.namespace, catalog: product.catalog };
        for (const [field] of fields) value[field] = typeof product[field] === 'boolean' ? product[field] : null;
        return value;
    });
    // Do not replace native callbacks or export account data. Native progress units
    // and aggregate installer counter scope still require an active-download test.
    return JSON.stringify({ products: values });
})()
