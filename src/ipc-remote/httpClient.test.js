import { describe, expect, test } from 'vitest';
import {
    RemoteAccessError,
    getRemoteCurrentUser,
    getRemoteFavoriteWorlds
} from './httpClient.js';

describe('remote HTTP client', () => {
    test('uses only the named same-origin read endpoints', async () => {
        const requests = [];
        const fetchImpl = async (path, options) => {
            requests.push({ path, options });
            return {
                ok: true,
                json: async () =>
                    path.endsWith('/me')
                        ? { user: { id: 'usr_1' } }
                        : { worlds: [{ id: 'wrld_1' }] }
            };
        };
        await expect(getRemoteCurrentUser(fetchImpl)).resolves.toEqual({
            id: 'usr_1'
        });
        await expect(getRemoteFavoriteWorlds(fetchImpl)).resolves.toEqual([
            { id: 'wrld_1' }
        ]);
        expect(requests).toEqual([
            {
                path: '/api/remote/v1/me',
                options: { method: 'GET', credentials: 'same-origin' }
            },
            {
                path: '/api/remote/v1/favorite-worlds',
                options: { method: 'GET', credentials: 'same-origin' }
            }
        ]);
    });

    test('uses a bounded error message for failed requests', async () => {
        await expect(
            getRemoteCurrentUser(async () => ({ ok: false, status: 502 }))
        ).rejects.toEqual(
            new RemoteAccessError('Unable to load the local VRCX session.', 502)
        );
    });
});
