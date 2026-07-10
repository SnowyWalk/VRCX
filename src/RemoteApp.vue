<template>
    <main class="remote-page">
        <header class="remote-header">
            <div>
                <h1>VRCX Remote</h1>
                <p v-if="user">Signed in as {{ user.displayName || user.username || user.id }}</p>
            </div>
            <button @click="refresh" :disabled="loading">Refresh</button>
        </header>
        <input v-model="query" class="world-search" type="search" placeholder="Search favorite worlds" />
        <p v-if="loading" class="status">Loading local VRCX data…</p>
        <p v-else-if="error" class="status error">{{ error }}</p>
        <p v-else-if="!filteredWorlds.length" class="status">No favorite worlds found.</p>
        <section v-else class="worlds" aria-label="Favorite worlds">
            <article v-for="world in filteredWorlds" :key="world.id" class="world">
                <img v-if="world.thumbnailImageUrl" :src="world.thumbnailImageUrl" alt="" />
                <div>
                    <strong>{{ world.name || world.id }}</strong>
                    <small v-if="world.authorName">{{ world.authorName }}</small>
                </div>
            </article>
        </section>
    </main>
</template>

<script setup>
    import { computed, onMounted, ref } from 'vue';

    import { getRemoteCurrentUser, getRemoteFavoriteWorlds } from './ipc-remote/httpClient.js';

    const worlds = ref([]);
    const user = ref(null);
    const query = ref('');
    const error = ref('');
    const loading = ref(true);
    const filteredWorlds = computed(() => {
        const term = query.value.trim().toLocaleLowerCase();
        if (!term) return worlds.value;
        return worlds.value.filter((world) =>
            `${world.name ?? ''} ${world.authorName ?? ''}`.toLocaleLowerCase().includes(term)
        );
    });

    async function refresh() {
        loading.value = true;
        error.value = '';
        try {
            const [currentUser, favoriteWorlds] = await Promise.all([
                getRemoteCurrentUser(),
                getRemoteFavoriteWorlds()
            ]);
            user.value = currentUser;
            worlds.value = favoriteWorlds;
        } catch (reason) {
            error.value = reason instanceof Error ? reason.message : 'Unable to load the local VRCX session.';
        } finally {
            loading.value = false;
        }
    }

    onMounted(() => {
        document.title = 'VRCX Remote';
        refresh();
    });
</script>

<style>
    body {
        margin: 0;
        background: #111827;
        color: #f9fafb;
        font-family: system-ui, sans-serif;
    }
    .remote-page {
        max-width: 48rem;
        margin: 0 auto;
        padding: 1rem;
    }
    .remote-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 1rem;
    }
    .remote-header h1,
    .remote-header p {
        margin: 0;
    }
    .remote-header p,
    .status,
    small {
        color: #cbd5e1;
    }
    button,
    .world-search {
        border: 1px solid #475569;
        border-radius: 0.5rem;
        padding: 0.6rem 0.8rem;
        background: #1e293b;
        color: inherit;
    }
    .world-search {
        box-sizing: border-box;
        width: 100%;
        margin: 1rem 0;
    }
    .worlds {
        display: grid;
        gap: 0.75rem;
    }
    .world {
        display: flex;
        gap: 0.75rem;
        overflow: hidden;
        border-radius: 0.75rem;
        background: #1e293b;
    }
    .world img {
        width: 7rem;
        height: 4rem;
        object-fit: cover;
    }
    .world div {
        display: flex;
        flex-direction: column;
        justify-content: center;
        gap: 0.25rem;
    }
    .error {
        color: #fca5a5;
    }
</style>
