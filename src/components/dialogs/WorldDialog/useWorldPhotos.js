import { onBeforeUnmount, ref, watch } from 'vue';

const DEFAULT_PAGE_SIZE = 120;
const DEFAULT_POLL_INTERVAL = 1500;

function parseResponse(value) {
    if (typeof value !== 'string' || !value)
        throw new TypeError('invalid_response');
    return JSON.parse(value);
}

function isIndexBusy(value) {
    return value === 'starting' || value === 'scanning' || value === 'stopping';
}

/**
 * Loads one world's indexed photos while keeping native file paths outside the renderer.
 * @param {{ worldId: import('vue').Ref<string>, active: import('vue').Ref<boolean>, pageSize?: number, pollInterval?: number }} options
 */
export function useWorldPhotos({
    worldId,
    active,
    pageSize = DEFAULT_PAGE_SIZE,
    pollInterval = DEFAULT_POLL_INTERVAL
}) {
    const items = ref([]);
    const nextCursor = ref(null);
    const status = ref({
        state: 'stopped',
        processed: 0,
        discovered: 0,
        lastError: ''
    });
    const loading = ref(false);
    const loadingMore = ref(false);
    const error = ref('');
    const visibleTokens = ref([]);
    const thumbnailRequests = new Set();
    const thumbnailRenderRetries = new Set();
    let requestGeneration = 0;
    let statusTimer = null;
    let previousBusy = false;

    function mergePhotoItems(incoming) {
        const byToken = new Map(
            items.value.map((item) => [item.publicToken, item])
        );
        for (const item of incoming ?? []) {
            if (!item?.publicToken) continue;
            byToken.set(item.publicToken, {
                ...byToken.get(item.publicToken),
                ...item
            });
        }
        items.value = Array.from(byToken.values());
    }

    async function fetchPage(cursor, generation) {
        const response = parseResponse(
            await AppApi.GetWorldPhotos(worldId.value, cursor, pageSize)
        );
        if (generation !== requestGeneration || !response) return false;
        mergePhotoItems(response.items);
        nextCursor.value = response.nextCursor ?? null;
        if (response.status) status.value = response.status;
        return true;
    }

    async function refresh() {
        const generation = ++requestGeneration;
        items.value = [];
        nextCursor.value = null;
        error.value = '';
        if (!active.value || !worldId.value) return;

        loading.value = true;
        try {
            const loaded = await fetchPage(null, generation);
            if (!loaded && generation === requestGeneration)
                error.value = 'invalid_response';
        } catch (exception) {
            if (generation === requestGeneration)
                error.value = exception?.message ?? 'load_failed';
        } finally {
            if (generation === requestGeneration) loading.value = false;
        }
    }

    async function loadMore() {
        const cursor = nextCursor.value;
        if (!active.value || !cursor || loading.value || loadingMore.value)
            return;
        const generation = requestGeneration;
        loadingMore.value = true;
        try {
            const loaded = await fetchPage(cursor, generation);
            if (!loaded && generation === requestGeneration)
                error.value = 'invalid_response';
        } catch (exception) {
            if (generation === requestGeneration)
                error.value = exception?.message ?? 'load_failed';
        } finally {
            if (generation === requestGeneration) loadingMore.value = false;
        }
    }

    async function requestThumbnails(tokens) {
        if (!active.value) return;
        const uniqueTokens = [...new Set(tokens)].filter(
            (token) => token && !thumbnailRequests.has(token)
        );
        if (!uniqueTokens.length) return;
        uniqueTokens.forEach((token) => thumbnailRequests.add(token));
        try {
            const results = parseResponse(
                await AppApi.RequestWorldPhotoThumbnails(
                    JSON.stringify(uniqueTokens)
                )
            );
            const updates = new Map(
                (results ?? []).map((result) => [result.publicToken, result])
            );
            items.value = items.value.map((item) => {
                const update = updates.get(item.publicToken);
                if (!update) return item;
                return {
                    ...item,
                    thumbnailPath: update.thumbnailPath ?? item.thumbnailPath,
                    thumbnailState: update.status ?? item.thumbnailState,
                    thumbnailError: update.error ?? null
                };
            });
        } catch (exception) {
            status.value = {
                ...status.value,
                lastError: exception?.message ?? 'thumbnail_request_failed'
            };
        } finally {
            uniqueTokens.forEach((token) => thumbnailRequests.delete(token));
        }
    }

    function setVisibleTokens(tokens) {
        visibleTokens.value = [...new Set(tokens.filter(Boolean))];
        void requestThumbnails(visibleTokens.value);
    }

    function retryThumbnail(publicToken) {
        if (thumbnailRenderRetries.has(publicToken)) {
            items.value = items.value.map((item) =>
                item.publicToken === publicToken
                    ? {
                          ...item,
                          thumbnailPath: null,
                          thumbnailState: 'error',
                          thumbnailError: 'thumbnail_render_failed'
                      }
                    : item
            );
            return;
        }
        thumbnailRenderRetries.add(publicToken);
        items.value = items.value.map((item) =>
            item.publicToken === publicToken
                ? { ...item, thumbnailPath: null, thumbnailState: 'queued' }
                : item
        );
        void requestThumbnails([publicToken]);
    }

    async function pollStatus() {
        if (!active.value) return;
        try {
            const nextStatus = parseResponse(
                await AppApi.GetWorldPhotoIndexStatus()
            );
            if (nextStatus) {
                const busy = isIndexBusy(nextStatus.state);
                status.value = nextStatus;
                if (previousBusy && !busy) await refresh();
                previousBusy = busy;
            }
            const itemsByToken = new Map(
                items.value.map((item) => [item.publicToken, item])
            );
            await requestThumbnails(
                visibleTokens.value.filter((token) => {
                    const item = itemsByToken.get(token);
                    return (
                        item &&
                        !item.thumbnailPath &&
                        item.thumbnailState !== 'error'
                    );
                })
            );
        } catch (exception) {
            status.value = {
                ...status.value,
                lastError: exception?.message ?? 'status_poll_failed'
            };
        }
    }

    async function rebuild() {
        const accepted = await AppApi.RebuildWorldPhotoIndex();
        if (!accepted) return false;
        status.value = {
            ...status.value,
            state: 'scanning'
        };
        previousBusy = true;
        await refresh();
        return true;
    }

    async function openPhoto(publicToken) {
        if (!publicToken) return false;
        return AppApi.OpenIndexedWorldPhoto(publicToken);
    }

    watch(
        [worldId, active],
        () => {
            previousBusy = false;
            thumbnailRequests.clear();
            thumbnailRenderRetries.clear();
            visibleTokens.value = [];
            void refresh();
            if (statusTimer) clearInterval(statusTimer);
            statusTimer = active.value
                ? setInterval(() => void pollStatus(), pollInterval)
                : null;
        },
        { immediate: true }
    );

    onBeforeUnmount(() => {
        requestGeneration += 1;
        if (statusTimer) clearInterval(statusTimer);
    });

    return {
        items,
        nextCursor,
        status,
        loading,
        loadingMore,
        error,
        refresh,
        loadMore,
        setVisibleTokens,
        retryThumbnail,
        rebuild,
        openPhoto
    };
}
