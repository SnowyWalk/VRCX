import { flushPromises, mount } from '@vue/test-utils';
import { defineComponent, nextTick, ref } from 'vue';
import { beforeEach, describe, expect, test, vi } from 'vitest';
import { useWorldPhotos } from '../useWorldPhotos';

function deferred() {
    let resolve;
    const promise = new Promise((complete) => {
        resolve = complete;
    });
    return { promise, resolve };
}

function page(items, nextCursor = null, state = 'idle') {
    return JSON.stringify({
        items,
        nextCursor,
        status: { state, processed: items.length, discovered: items.length }
    });
}

function mountComposable(worldId = ref('wrld_one'), active = ref(true)) {
    let photos;
    const wrapper = mount(
        defineComponent({
            setup() {
                photos = useWorldPhotos({
                    worldId,
                    active,
                    pollInterval: 60_000,
                    pageSize: 25
                });
                return () => null;
            }
        })
    );
    return { wrapper, photos, worldId, active };
}

describe('useWorldPhotos', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
        globalThis.AppApi = {
            GetWorldPhotos: vi.fn().mockResolvedValue(page([])),
            GetWorldPhotoIndexStatus: vi
                .fn()
                .mockResolvedValue(JSON.stringify({ state: 'idle' })),
            RequestWorldPhotoThumbnails: vi.fn().mockResolvedValue('[]'),
            RebuildWorldPhotoIndex: vi.fn().mockResolvedValue(true),
            OpenIndexedWorldPhoto: vi.fn().mockResolvedValue(true)
        };
    });

    test('does not query until the tab becomes active', async () => {
        const active = ref(false);
        const { wrapper } = mountComposable(ref('wrld_one'), active);
        await flushPromises();
        expect(AppApi.GetWorldPhotos).not.toHaveBeenCalled();

        active.value = true;
        await nextTick();
        await flushPromises();
        expect(AppApi.GetWorldPhotos).toHaveBeenCalledWith(
            'wrld_one',
            null,
            25
        );
        wrapper.unmount();
    });

    test('ignores a stale response after switching worlds', async () => {
        const first = deferred();
        const second = deferred();
        AppApi.GetWorldPhotos.mockReturnValueOnce(
            first.promise
        ).mockReturnValueOnce(second.promise);

        const { wrapper, photos, worldId } = mountComposable();
        worldId.value = 'wrld_two';
        await nextTick();

        second.resolve(
            page([
                { publicToken: 'new-token', capturedAt: '2026-01-02T00:00:00Z' }
            ])
        );
        await flushPromises();
        first.resolve(
            page([
                { publicToken: 'old-token', capturedAt: '2026-01-01T00:00:00Z' }
            ])
        );
        await flushPromises();

        expect(photos.items.value.map((item) => item.publicToken)).toEqual([
            'new-token'
        ]);
        wrapper.unmount();
    });

    test('deduplicates concurrent cursor loads and merges by public token', async () => {
        const more = deferred();
        AppApi.GetWorldPhotos.mockResolvedValueOnce(
            page([{ publicToken: 'one' }], 'cursor-1')
        ).mockReturnValueOnce(more.promise);
        const { wrapper, photos } = mountComposable();
        await flushPromises();

        const firstLoad = photos.loadMore();
        const duplicateLoad = photos.loadMore();
        more.resolve(
            page([
                { publicToken: 'one', thumbnailPath: 'thumb.jpg' },
                { publicToken: 'two' }
            ])
        );
        await Promise.all([firstLoad, duplicateLoad]);

        expect(AppApi.GetWorldPhotos).toHaveBeenCalledTimes(2);
        expect(photos.items.value).toHaveLength(2);
        expect(photos.items.value[0].thumbnailPath).toBe('thumb.jpg');
        wrapper.unmount();
    });

    test('requests thumbnails and opens photos using public tokens only', async () => {
        AppApi.GetWorldPhotos.mockResolvedValueOnce(
            page([{ publicToken: 'safe-token' }])
        );
        AppApi.RequestWorldPhotoThumbnails.mockResolvedValueOnce(
            JSON.stringify([
                {
                    publicToken: 'safe-token',
                    status: 'ready',
                    thumbnailPath: 'cache/thumb.jpg'
                }
            ])
        );
        const { wrapper, photos } = mountComposable();
        await flushPromises();

        photos.setVisibleTokens(['safe-token', 'safe-token']);
        await flushPromises();
        await photos.openPhoto('safe-token');

        expect(AppApi.RequestWorldPhotoThumbnails).toHaveBeenCalledWith(
            '["safe-token"]'
        );
        expect(photos.items.value[0].thumbnailPath).toBe('cache/thumb.jpg');
        expect(AppApi.OpenIndexedWorldPhoto).toHaveBeenCalledWith('safe-token');
        wrapper.unmount();
    });

    test('keeps loaded photos and records malformed thumbnail responses', async () => {
        AppApi.GetWorldPhotos.mockResolvedValueOnce(
            page([{ publicToken: 'safe-token' }])
        );
        AppApi.RequestWorldPhotoThumbnails.mockResolvedValueOnce('{invalid');
        const { wrapper, photos } = mountComposable();
        await flushPromises();

        photos.setVisibleTokens(['safe-token']);
        await flushPromises();

        expect(photos.items.value).toEqual([
            expect.objectContaining({ publicToken: 'safe-token' })
        ]);
        expect(photos.status.value.lastError).toBeTruthy();
        wrapper.unmount();
    });

    test('bounds renderer-error retries for one thumbnail token', async () => {
        AppApi.GetWorldPhotos.mockResolvedValueOnce(
            page([
                {
                    publicToken: 'broken-token',
                    thumbnailPath: 'cache/broken.jpg',
                    thumbnailState: 'ready'
                }
            ])
        );
        AppApi.RequestWorldPhotoThumbnails.mockResolvedValue(
            JSON.stringify([
                {
                    publicToken: 'broken-token',
                    status: 'ready',
                    thumbnailPath: 'cache/broken.jpg'
                }
            ])
        );
        const { wrapper, photos } = mountComposable();
        await flushPromises();

        photos.retryThumbnail('broken-token');
        await flushPromises();
        photos.retryThumbnail('broken-token');
        await flushPromises();

        expect(AppApi.RequestWorldPhotoThumbnails).toHaveBeenCalledTimes(1);
        expect(photos.items.value[0]).toEqual(
            expect.objectContaining({
                thumbnailPath: null,
                thumbnailState: 'error',
                thumbnailError: 'thumbnail_render_failed'
            })
        );
        wrapper.unmount();
    });
});
