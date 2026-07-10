const LOOPBACK_HOST = '127.0.0.1';
const DEFAULT_PORT = 36742;

function parsePort(value) {
    if (value === undefined || value === '') return DEFAULT_PORT;
    const port = Number(value);
    if (!Number.isInteger(port) || port < 1024 || port > 65535) {
        throw new Error(
            'VRCX_REMOTE_ACCESS_PORT must be a port from 1024 to 65535'
        );
    }
    return port;
}

function getRemoteAccessConfig(env = process.env) {
    const tailnetUrl = env.VRCX_REMOTE_TAILNET_URL;
    if (tailnetUrl && !/^https:\/\/[^/]+\.ts\.net\/?$/.test(tailnetUrl))
        throw new Error('VRCX_REMOTE_TAILNET_URL must be a tailnet HTTPS URL');
    return {
        enabled: env.VRCX_REMOTE_ACCESS === '1',
        host: LOOPBACK_HOST,
        port: parsePort(env.VRCX_REMOTE_ACCESS_PORT),
        tailnetUrl
    };
}

function getRemoteAccessStatus(env = process.env) {
    let config;
    try {
        config = getRemoteAccessConfig(env);
    } catch {
        return {
            enabled: false,
            host: LOOPBACK_HOST,
            port: DEFAULT_PORT,
            state: 'error',
            tailnetState: 'invalid-config'
        };
    }
    return {
        ...config,
        state: config.enabled ? 'starting' : 'disabled',
        tailnetState: config.enabled
            ? config.tailnetUrl
                ? 'declared'
                : 'requires-serve'
            : 'disabled'
    };
}

module.exports = {
    DEFAULT_PORT,
    LOOPBACK_HOST,
    getRemoteAccessConfig,
    getRemoteAccessStatus
};
