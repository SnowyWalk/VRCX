export class RemoteAccessError extends Error {
    constructor(message, status) {
        super(message);
        this.name = 'RemoteAccessError';
        this.status = status;
    }
}

async function getJson(path, fetchImpl = fetch) {
    const response = await fetchImpl(path, {
        method: 'GET',
        credentials: 'same-origin'
    });
    if (!response.ok)
        throw new RemoteAccessError(
            'Unable to load the local VRCX session.',
            response.status
        );
    return response.json();
}

export function getRemoteCurrentUser(fetchImpl) {
    return getJson('/api/remote/v1/me', fetchImpl).then(({ user }) => user);
}

export function getRemoteFavoriteWorlds(fetchImpl) {
    return getJson('/api/remote/v1/favorite-worlds', fetchImpl).then(
        ({ worlds }) => worlds
    );
}
