<template>
    <div class="flex h-full min-h-0 flex-col" data-testid="world-photo-tab">
        <div class="flex shrink-0 items-center gap-2 border-b px-3 py-2 text-sm">
            <div class="min-w-0 flex-1 text-muted-foreground">
                <span v-if="isScanning">
                    {{
                        t('dialog.world.photos.indexing', {
                            processed: status.processed ?? 0,
                            discovered: status.discovered ?? 0
                        })
                    }}
                </span>
                <span v-else>{{ t('dialog.world.photos.count', { count: items.length }) }}</span>
            </div>
            <Button variant="outline" size="sm" :disabled="loading" @click="refresh">
                <RefreshCw class="size-4" />
                {{ t('dialog.world.photos.refresh') }}
            </Button>
            <Button variant="outline" size="sm" :disabled="isScanning" @click="confirmRebuild">
                <RotateCcw class="size-4" />
                {{ t('dialog.world.photos.rebuild') }}
            </Button>
        </div>

        <div v-if="error" class="flex flex-1 flex-col items-center justify-center gap-3 p-6 text-center">
            <TriangleAlert class="size-8 text-destructive" />
            <p>{{ t('dialog.world.photos.error') }}</p>
            <Button variant="outline" @click="refresh">{{ t('dialog.world.photos.retry') }}</Button>
        </div>
        <div v-else-if="loading && !items.length" class="flex flex-1 items-center justify-center">
            <Spinner />
        </div>
        <div
            v-else-if="!items.length"
            class="flex flex-1 flex-col items-center justify-center gap-2 p-6 text-center text-muted-foreground">
            <ImageIcon class="size-10" />
            <p>{{ t('dialog.world.photos.empty') }}</p>
        </div>
        <div
            v-else
            ref="scrollViewportRef"
            class="min-h-0 flex-1 overflow-y-auto p-3"
            data-testid="world-photo-viewport">
            <div class="relative w-full" :style="virtualContainerStyle">
                <div
                    v-for="row in virtualRows"
                    :key="row.virtualItem.key"
                    class="absolute top-0 left-0 grid w-full gap-3"
                    :style="rowStyle(row)">
                    <button
                        v-for="photo in row.photos"
                        :key="photo.publicToken"
                        type="button"
                        class="group overflow-hidden rounded-md border bg-muted text-left focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
                        :title="t('dialog.world.photos.open_hint')"
                        :data-photo-token="photo.publicToken"
                        @dblclick="openPhoto(photo.publicToken)">
                        <div class="flex aspect-video items-center justify-center overflow-hidden">
                            <img
                                v-if="photo.thumbnailPath"
                                :src="photo.thumbnailPath"
                                class="size-full object-cover transition-transform group-hover:scale-[1.02]"
                                loading="lazy"
                                alt=""
                                @error="retryThumbnail(photo.publicToken)" />
                            <Spinner v-else-if="isThumbnailPending(photo)" class="size-5" />
                            <ImageIcon v-else class="size-7 text-muted-foreground" />
                        </div>
                        <div class="truncate border-t px-2 py-1.5 text-xs text-muted-foreground">
                            {{ formatCapturedAt(photo.capturedAt) }}
                        </div>
                    </button>
                </div>
            </div>
            <div v-if="loadingMore" class="flex justify-center py-3"><Spinner /></div>
        </div>
    </div>
</template>

<script setup>
    import { useVirtualizer } from '@tanstack/vue-virtual';
    import { Image as ImageIcon, RefreshCw, RotateCcw, TriangleAlert } from 'lucide-vue-next';
    import { computed, nextTick, onBeforeUnmount, onMounted, ref, toRef, watch } from 'vue';
    import { useI18n } from 'vue-i18n';
    import { Button } from '@/components/ui/button';
    import { Spinner } from '@/components/ui/spinner';
    import { useWorldPhotos } from './useWorldPhotos';

    const props = defineProps({
        worldId: { type: String, required: true },
        active: { type: Boolean, required: true }
    });

    const { t } = useI18n();
    const scrollViewportRef = ref(null);
    const viewportWidth = ref(720);
    const columnCount = computed(() => Math.max(1, Math.floor(viewportWidth.value / 180)));
    const rowHeight = computed(() => {
        const gaps = (columnCount.value - 1) * 12;
        const cardWidth = (viewportWidth.value - gaps) / columnCount.value;
        return Math.ceil((cardWidth * 9) / 16 + 42);
    });
    const {
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
    } = useWorldPhotos({ worldId: toRef(props, 'worldId'), active: toRef(props, 'active') });

    const photoRows = computed(() => {
        const rows = [];
        for (let index = 0; index < items.value.length; index += columnCount.value) {
            rows.push(items.value.slice(index, index + columnCount.value));
        }
        return rows;
    });

    const virtualizer = useVirtualizer(
        computed(() => ({
            count: photoRows.value.length,
            getScrollElement: () => scrollViewportRef.value,
            estimateSize: () => rowHeight.value,
            getItemKey: (index) => photoRows.value[index]?.[0]?.publicToken ?? index,
            overscan: 3
        }))
    );

    const virtualRows = computed(() =>
        (virtualizer.value?.getVirtualItems?.() ?? []).map((virtualItem) => ({
            virtualItem,
            photos: photoRows.value[virtualItem.index] ?? []
        }))
    );
    const virtualContainerStyle = computed(() => ({
        height: `${virtualizer.value?.getTotalSize?.() ?? 0}px`
    }));
    const rowStyle = (row) => ({
        gridTemplateColumns: `repeat(${columnCount.value}, minmax(0, 1fr))`,
        transform: `translateY(${row.virtualItem.start}px)`
    });
    const isScanning = computed(() => status.value.state === 'starting' || status.value.state === 'scanning');

    let resizeObserver;
    onMounted(() => {
        resizeObserver = new ResizeObserver((entries) => {
            viewportWidth.value = entries[0]?.contentRect?.width || scrollViewportRef.value?.clientWidth || 720;
        });
        if (scrollViewportRef.value) resizeObserver.observe(scrollViewportRef.value);
    });
    onBeforeUnmount(() => resizeObserver?.disconnect());

    watch(
        virtualRows,
        (rows) => {
            const visible = rows.flatMap((row) => row.photos.map((photo) => photo.publicToken));
            setVisibleTokens(visible);
            const lastIndex = rows.at(-1)?.virtualItem.index ?? -1;
            if (nextCursor.value && lastIndex >= photoRows.value.length - 2) void loadMore();
        },
        { immediate: true }
    );

    watch([items, columnCount, rowHeight], async () => {
        await nextTick();
        virtualizer.value?.measure?.();
    });

    function formatCapturedAt(value) {
        if (!value) return '';
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? '' : date.toLocaleString();
    }

    function isThumbnailPending(photo) {
        return photo.thumbnailState === 1 || photo.thumbnailState === 'queued' || photo.thumbnailState === 'accepted';
    }

    async function confirmRebuild() {
        if (!window.confirm(t('dialog.world.photos.rebuild_confirm'))) return;
        await rebuild();
    }
</script>
