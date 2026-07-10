import { describe, expect, test } from 'vitest';
import capabilityModule from './capabilities.js';

const { REMOTE_CAPABILITIES, createRemoteCapabilities } = capabilityModule;

describe('remote capability allowlist', () => {
    test('uses fixed VRChat requests and redacts response fields', async () => {
        const calls = [];
        const capabilities = createRemoteCapabilities({
            callMethod: async (className, methodName, [request]) => {
                calls.push({ className, methodName, request });
                if (request.url.endsWith('/auth/user'))
                    return [
                        200,
                        JSON.stringify({
                            id: 'usr_1',
                            displayName: 'Owner',
                            username: 'owner',
                            email: 'private@example.com'
                        })
                    ];
                return [
                    200,
                    JSON.stringify([
                        {
                            id: 'wrld_1',
                            name: 'World',
                            authorName: 'Creator',
                            thumbnailImageUrl:
                                'https://example.test/thumbnail.png',
                            privateTag: 'not exposed'
                        }
                    ])
                ];
            }
        });

        await expect(capabilities.getCurrentUser()).resolves.toEqual({
            id: 'usr_1',
            displayName: 'Owner',
            username: 'owner'
        });
        await expect(capabilities.getFavoriteWorlds()).resolves.toEqual([
            {
                id: 'wrld_1',
                name: 'World',
                authorName: 'Creator',
                thumbnailImageUrl: 'https://example.test/thumbnail.png'
            }
        ]);
        expect(calls).toEqual([
            {
                className: 'WebApi',
                methodName: 'Execute',
                request: {
                    url: 'https://api.vrchat.cloud/api/1/auth/user',
                    method: 'GET'
                }
            },
            {
                className: 'WebApi',
                methodName: 'Execute',
                request: {
                    url: 'https://api.vrchat.cloud/api/1/worlds/favorites?n=300&offset=0',
                    method: 'GET'
                }
            }
        ]);
        expect(REMOTE_CAPABILITIES).toEqual({
            currentUser: true,
            favoriteWorlds: true,
            readOnly: true
        });
    });

    test('converts backend failures into bounded failures', async () => {
        const capabilities = createRemoteCapabilities({
            callMethod: async () => [401, 'not-json']
        });
        await expect(capabilities.getCurrentUser()).rejects.toThrow(
            'VRChat returned 401'
        );
    });

    test('accepts the .NET tuple shape used by InteropApi', async () => {
        const capabilities = createRemoteCapabilities({
            callMethod: async () => ({
                Item1: 200,
                Item2: JSON.stringify({ id: 'usr_1' })
            })
        });
        await expect(capabilities.getCurrentUser()).resolves.toEqual({
            id: 'usr_1',
            displayName: undefined,
            username: undefined
        });
    });
});
