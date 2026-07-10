const REMOTE_CAPABILITIES = Object.freeze({
    currentUser: true,
    favoriteWorlds: true,
    readOnly: true
});

function requireSuccess(result) {
    const statusCode = Array.isArray(result) ? result[0] : result?.Item1;
    const body = Array.isArray(result) ? result[1] : result?.Item2;
    if (!Number.isInteger(statusCode) || statusCode < 200 || statusCode >= 300)
        throw new Error(
            `VRChat returned ${statusCode ?? 'an invalid response'}`
        );
    return JSON.parse(body);
}

function createRemoteCapabilities(interopApi) {
    async function executeFixedRequest(url) {
        const result = await interopApi.callMethod('WebApi', 'Execute', [
            { url, method: 'GET' }
        ]);
        return requireSuccess(result);
    }

    return {
        getCurrentUser: async () => {
            const { id, displayName, username } = await executeFixedRequest(
                'https://api.vrchat.cloud/api/1/auth/user'
            );
            return { id, displayName, username };
        },
        getFavoriteWorlds: async () => {
            const worlds = await executeFixedRequest(
                'https://api.vrchat.cloud/api/1/worlds/favorites?n=300&offset=0'
            );
            return Array.isArray(worlds)
                ? worlds.map(
                      ({
                          id,
                          name: worldName,
                          authorName,
                          thumbnailImageUrl
                      }) => ({
                          id,
                          name: worldName,
                          authorName,
                          thumbnailImageUrl
                      })
                  )
                : [];
        }
    };
}

module.exports = { REMOTE_CAPABILITIES, createRemoteCapabilities };
