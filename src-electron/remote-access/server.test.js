import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import config from './config.js';
import serverModule from './server.js';

const { getRemoteAccessConfig, getRemoteAccessStatus } = config;
const { startRemoteServer } = serverModule;

let server;
let assetRoot;

afterEach(async () => {
    if (server) await new Promise((resolve) => server.close(resolve));
    server = undefined;
    if (assetRoot) fs.rmSync(assetRoot, { recursive: true, force: true });
    assetRoot = undefined;
});

describe('remote access server', () => {
    test('is disabled by default and always uses loopback', () => {
        expect(getRemoteAccessConfig({})).toEqual({
            enabled: false,
            host: '127.0.0.1',
            port: 36742
        });
        expect(() =>
            getRemoteAccessConfig({ VRCX_REMOTE_ACCESS_PORT: '80' })
        ).toThrow('port');
        expect(getRemoteAccessStatus({})).toMatchObject({
            state: 'disabled',
            tailnetState: 'disabled'
        });
        expect(
            getRemoteAccessStatus({
                VRCX_REMOTE_TAILNET_URL: 'http://not-tailnet.example'
            })
        ).toMatchObject({ state: 'error', tailnetState: 'invalid-config' });
    });

    test('serves only the typed favorite-world endpoint', async () => {
        server = await startRemoteServer({
            host: '127.0.0.1',
            port: 0,
            getCurrentUser: async () => ({
                id: 'usr_1',
                displayName: 'Test User'
            }),
            getFavoriteWorlds: async () => [
                { id: 'wrld_1', name: 'Test World' }
            ],
            logger: { info() {}, error() {} }
        });
        const port = server.address().port;
        const response = await fetch(
            `http://127.0.0.1:${port}/api/remote/v1/favorite-worlds`
        );
        expect(response.status).toBe(200);
        await expect(response.json()).resolves.toEqual({
            worlds: [{ id: 'wrld_1', name: 'Test World' }]
        });
        await expect(
            (await fetch(`http://127.0.0.1:${port}/api/remote/v1/me`)).json()
        ).resolves.toEqual({
            user: { id: 'usr_1', displayName: 'Test User' }
        });
        expect(
            (await fetch(`http://127.0.0.1:${port}/api/remote/v1/rpc`)).status
        ).toBe(404);
    });

    test('redacts backend errors from remote responses', async () => {
        const errors = [];
        server = await startRemoteServer({
            host: '127.0.0.1',
            port: 0,
            getCurrentUser: async () => {
                throw new Error('cookie=private-session');
            },
            getFavoriteWorlds: async () => [],
            logger: {
                info() {},
                error(...args) {
                    errors.push(args);
                }
            }
        });
        const response = await fetch(
            `http://127.0.0.1:${server.address().port}/api/remote/v1/me`
        );
        expect(response.status).toBe(502);
        await expect(response.json()).resolves.toEqual({
            error: 'Current user is currently unavailable'
        });
        expect(response.headers.get('x-request-id')).toBe('remote-1');
        expect(errors).toEqual([
            ['Remote current-user request failed', { requestId: 'remote-1' }]
        ]);
    });

    test('rejects a non-loopback bind and cross-origin requests', async () => {
        expect(() =>
            startRemoteServer({
                host: '0.0.0.0',
                port: 0,
                getCurrentUser: async () => ({}),
                getFavoriteWorlds: async () => []
            })
        ).toThrow('127.0.0.1');
        server = await startRemoteServer({
            host: '127.0.0.1',
            port: 0,
            getCurrentUser: async () => ({}),
            getFavoriteWorlds: async () => [],
            logger: { info() {}, error() {} }
        });
        const response = await fetch(
            `http://127.0.0.1:${server.address().port}/api/remote/v1/favorite-worlds`,
            { headers: { Origin: 'https://untrusted.example' } }
        );
        expect(response.status).toBe(403);
    });

    test('serves only the remote entry point and its build assets', async () => {
        assetRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'vrcx-remote-'));
        fs.mkdirSync(path.join(assetRoot, 'assets'));
        fs.writeFileSync(
            path.join(assetRoot, 'remote.html'),
            '<script type="module" src="./assets/remote.js"></script>'
        );
        fs.writeFileSync(
            path.join(assetRoot, 'index.html'),
            '<h1>Desktop</h1>'
        );
        fs.writeFileSync(
            path.join(assetRoot, 'assets', 'remote.js'),
            'export {}'
        );
        fs.writeFileSync(
            path.join(assetRoot, 'assets', 'desktop.js'),
            'export {}'
        );
        server = await startRemoteServer({
            host: '127.0.0.1',
            port: 0,
            assetRoot,
            getCurrentUser: async () => ({}),
            getFavoriteWorlds: async () => [],
            logger: { info() {}, error() {} }
        });
        const baseUrl = `http://127.0.0.1:${server.address().port}`;
        await expect((await fetch(`${baseUrl}/`)).text()).resolves.toBe(
            '<script type="module" src="./assets/remote.js"></script>'
        );
        expect((await fetch(`${baseUrl}/index.html`)).status).toBe(404);
        expect((await fetch(`${baseUrl}/../index.html`)).status).toBe(404);
        expect((await fetch(`${baseUrl}/assets/remote.js`)).status).toBe(200);
        expect((await fetch(`${baseUrl}/assets/desktop.js`)).status).toBe(404);
    });
});
