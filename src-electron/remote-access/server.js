const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');

const SECURITY_HEADERS = {
    'content-security-policy':
        "default-src 'self'; connect-src 'self'; img-src 'self' https: data:; style-src 'self'; script-src 'self'; base-uri 'none'; frame-ancestors 'none'",
    'x-content-type-options': 'nosniff',
    'referrer-policy': 'no-referrer',
    'cache-control': 'no-store'
};

function write(response, statusCode, body, headers = {}) {
    response.writeHead(statusCode, { ...SECURITY_HEADERS, ...headers });
    response.end(body);
}

function contentType(filePath) {
    if (filePath.endsWith('.html')) return 'text/html; charset=utf-8';
    if (filePath.endsWith('.js')) return 'text/javascript; charset=utf-8';
    if (filePath.endsWith('.css')) return 'text/css; charset=utf-8';
    if (filePath.endsWith('.json')) return 'application/json; charset=utf-8';
    if (filePath.endsWith('.svg')) return 'image/svg+xml';
    if (filePath.endsWith('.woff2')) return 'font/woff2';
    return 'application/octet-stream';
}

function remoteAssetPath(assetRoot, requestUrl) {
    const pathname = new URL(requestUrl, 'http://localhost').pathname;
    const relativePath = pathname === '/' ? 'remote.html' : pathname.slice(1);
    if (relativePath !== 'remote.html' && !relativePath.startsWith('assets/'))
        return undefined;
    const root = path.resolve(assetRoot);
    const filePath = path.resolve(root, relativePath);
    if (!filePath.startsWith(`${root}${path.sep}`)) return undefined;
    return filePath;
}

function remoteAssetAllowlist(assetRoot) {
    const root = path.resolve(assetRoot);
    const allowed = new Set();
    const pending = ['remote.html'];
    while (pending.length) {
        const relativePath = pending.pop();
        const filePath = path.resolve(root, relativePath);
        if (allowed.has(filePath) || !fs.existsSync(filePath)) continue;
        allowed.add(filePath);
        const source = fs.readFileSync(filePath, 'utf8');
        const references = source.matchAll(
            /(?:src|href)=["'](?:\.\/)?([^"']+)["']|(?:from|import)\s*["'](?:\.\/)?([^"']+)["']|url\(["']?(?:\.\/)?([^"')]+)["']?\)/g
        );
        for (const reference of references) {
            const candidate = reference[1] ?? reference[2] ?? reference[3];
            if (
                !candidate ||
                candidate.startsWith('http') ||
                candidate.startsWith('data:')
            )
                continue;
            const childPath = path.relative(
                root,
                path.resolve(path.dirname(filePath), candidate)
            );
            if (childPath.startsWith('..') || path.isAbsolute(childPath))
                continue;
            pending.push(childPath);
        }
    }
    return allowed;
}

function startRemoteServer({
    host,
    port,
    assetRoot,
    getCurrentUser,
    getFavoriteWorlds,
    logger = console
}) {
    if (host !== '127.0.0.1')
        throw new Error('Remote access must bind to 127.0.0.1');
    const allowedAssets = assetRoot
        ? remoteAssetAllowlist(assetRoot)
        : new Set();
    let requestSequence = 0;
    const server = http.createServer(async (request, response) => {
        const requestId = `remote-${++requestSequence}`;
        if (request.method !== 'GET')
            return write(response, 405, 'Method Not Allowed', {
                allow: 'GET',
                'x-request-id': requestId
            });
        if (request.headers.origin) {
            logger.warn?.('Rejected cross-origin remote request', {
                requestId
            });
            return write(
                response,
                403,
                'Cross-origin requests are not allowed',
                { 'x-request-id': requestId }
            );
        }
        if (request.url === '/healthz')
            return write(response, 200, 'ok', {
                'content-type': 'text/plain; charset=utf-8'
            });
        if (request.url === '/remote-capabilities')
            return write(
                response,
                200,
                JSON.stringify({
                    currentUser: true,
                    favoriteWorlds: true,
                    readOnly: true
                }),
                { 'content-type': 'application/json; charset=utf-8' }
            );
        if (request.url === '/api/remote/v1/me') {
            try {
                const user = await getCurrentUser();
                return write(response, 200, JSON.stringify({ user }), {
                    'content-type': 'application/json; charset=utf-8'
                });
            } catch (error) {
                logger.error('Remote current-user request failed', {
                    requestId
                });
                return write(
                    response,
                    502,
                    JSON.stringify({
                        error: 'Current user is currently unavailable'
                    }),
                    {
                        'content-type': 'application/json; charset=utf-8',
                        'x-request-id': requestId
                    }
                );
            }
        }
        if (request.url === '/api/remote/v1/favorite-worlds') {
            try {
                const worlds = await getFavoriteWorlds();
                return write(response, 200, JSON.stringify({ worlds }), {
                    'content-type': 'application/json; charset=utf-8'
                });
            } catch (error) {
                logger.error('Remote favorite-world request failed', {
                    requestId
                });
                return write(
                    response,
                    502,
                    JSON.stringify({
                        error: 'Favorite worlds are currently unavailable'
                    }),
                    {
                        'content-type': 'application/json; charset=utf-8',
                        'x-request-id': requestId
                    }
                );
            }
        }
        if (assetRoot && request.url) {
            const filePath = remoteAssetPath(assetRoot, request.url);
            if (
                filePath &&
                fs.existsSync(filePath) &&
                fs.statSync(filePath).isFile() &&
                allowedAssets.has(filePath)
            )
                return write(response, 200, fs.readFileSync(filePath), {
                    'content-type': contentType(filePath)
                });
        }
        return write(response, 404, 'Not Found');
    });
    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(port, host, () => {
            server.off('error', reject);
            logger.info(
                `VRCX remote access listening on http://${host}:${port}`
            );
            resolve(server);
        });
    });
}

module.exports = {
    SECURITY_HEADERS,
    remoteAssetPath,
    remoteAssetAllowlist,
    startRemoteServer
};
