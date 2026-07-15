import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, test, vi } from 'vitest';
import WorldDialogPhotosTab from '../WorldDialogPhotosTab.vue';

const mocks = vi.hoisted(() => ({
    refresh: vi.fn(),
    loadMore: vi.fn(),
    setVisibleTokens: vi.fn(),
    retryThumbnail: vi.fn(),
    rebuild: vi.fn().mockResolvedValue(true),
    openPhoto: vi.fn().mockResolvedValue(true)
}));

vi.mock('vue-i18n', () => ({
    useI18n: () => ({
        t: (key, params) => (params ? `${key}:${JSON.stringify(params)}` : key)
    })
}));

vi.mock('../useWorldPhotos', async () => {
    const { ref } = await import('vue');
    return {
        useWorldPhotos: () => ({
            items: ref(
                Array.from({ length: 5000 }, (_, index) => ({
                    publicToken: `token-${index}`,
                    capturedAt: '2026-01-01T00:00:00Z',
                    thumbnailPath: index === 0 ? 'cache/ready.jpg' : null,
                    thumbnailState: index === 1 ? 'accepted' : 0
                }))
            ),
            nextCursor: ref('next-cursor'),
            status: ref({ state: 'idle', processed: 5000, discovered: 5000 }),
            loading: ref(false),
            loadingMore: ref(false),
            error: ref(''),
            refresh: mocks.refresh,
            loadMore: mocks.loadMore,
            setVisibleTokens: mocks.setVisibleTokens,
            retryThumbnail: mocks.retryThumbnail,
            rebuild: mocks.rebuild,
            openPhoto: mocks.openPhoto
        })
    };
});

vi.mock('@tanstack/vue-virtual', () => ({
    useVirtualizer: (optionsRef) => ({
        value: {
            getVirtualItems: () =>
                Array.from(
                    { length: Math.min(4, optionsRef.value.count) },
                    (_, index) => ({
                        index,
                        key: optionsRef.value.getItemKey(index),
                        start: index * 142
                    })
                ),
            getTotalSize: () => optionsRef.value.count * 142,
            measure: vi.fn()
        }
    })
}));

describe('WorldDialogPhotosTab', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    test('keeps a 5000-photo library to a bounded virtualized DOM', async () => {
        const wrapper = mount(WorldDialogPhotosTab, {
            props: { worldId: 'wrld_test', active: true }
        });
        await flushPromises();

        const cards = wrapper.findAll('[data-photo-token]');
        expect(cards.length).toBeGreaterThan(0);
        expect(cards.length).toBeLessThan(200);
        expect(wrapper.find('img[src="cache/ready.jpg"]').exists()).toBe(true);
        expect(mocks.setVisibleTokens).toHaveBeenCalledWith(
            expect.arrayContaining(['token-0', 'token-1'])
        );
        expect(
            wrapper
                .find('[data-photo-token="token-1"]')
                .find('[role="status"]')
                .exists()
        ).toBe(true);
    });

    test('opens the selected card with its public token on double-click', async () => {
        const wrapper = mount(WorldDialogPhotosTab, {
            props: { worldId: 'wrld_test', active: true }
        });
        await wrapper.find('[data-photo-token="token-0"]').trigger('dblclick');

        expect(mocks.openPhoto).toHaveBeenCalledWith('token-0');
    });

    test('requeues a thumbnail when its cached file cannot be rendered', async () => {
        const wrapper = mount(WorldDialogPhotosTab, {
            props: { worldId: 'wrld_test', active: true }
        });
        await wrapper.find('img[src="cache/ready.jpg"]').trigger('error');

        expect(mocks.retryThumbnail).toHaveBeenCalledWith('token-0');
    });
});
