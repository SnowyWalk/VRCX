<template>
    <SettingsGroup title="Remote access">
        <div class="flex flex-col gap-2 text-sm">
            <p v-if="loading" class="m-0 text-muted-foreground">Checking local listener status…</p>
            <template v-else-if="status">
                <p class="m-0">
                    <strong>VRCX listener:</strong>
                    {{ listenerLabel }}
                </p>
                <p class="m-0 text-muted-foreground">{{ listenerDescription }}</p>
                <p class="m-0"><strong>Tailscale Serve:</strong> {{ tailnetLabel }}</p>
                <p class="m-0 text-muted-foreground">
                    This app never enables Funnel or opens a router port. Remote access is controlled by the
                    <code>VRCX_REMOTE_ACCESS=1</code> launch environment variable.
                </p>
            </template>
            <p v-else class="m-0 text-muted-foreground">Remote access status is unavailable.</p>
        </div>
    </SettingsGroup>
</template>

<script setup>
    import { computed, onMounted, ref } from 'vue';

    import SettingsGroup from './SettingsGroup.vue';

    const loading = ref(true);
    const status = ref(null);

    const listenerLabel = computed(() => {
        if (status.value?.state === 'disabled') return 'Disabled';
        if (status.value?.state === 'local-ready') return status.value.listener;
        if (status.value?.state === 'error') return 'Failed to start';
        return 'Starting…';
    });
    const listenerDescription = computed(() => {
        if (status.value?.state === 'disabled')
            return 'Launch VRCX with VRCX_REMOTE_ACCESS=1 to enable the loopback-only listener.';
        if (status.value?.state === 'local-ready')
            return 'The listener is local-only. Tailscale Serve is required for phone access.';
        if (status.value?.state === 'error') return 'Check the VRCX console for the local listener error.';
        return 'VRCX is starting the local listener.';
    });
    const tailnetLabel = computed(() => {
        if (status.value?.tailnetState === 'disabled') return 'Not needed while remote access is disabled.';
        if (status.value?.tailnetState === 'ready') return `Ready at ${status.value.tailnetUrl}.`;
        if (status.value?.tailnetState === 'unavailable')
            return 'Tailscale is not installed or not running on this PC.';
        if (status.value?.tailnetState === 'misconfigured')
            return 'Tailscale Serve is not proxying the VRCX loopback port.';
        if (status.value?.tailnetState === 'url-missing')
            return 'Serve is running, but VRCX_REMOTE_TAILNET_URL is not set.';
        if (status.value?.tailnetState === 'invalid-config')
            return 'VRCX_REMOTE_TAILNET_URL is invalid. Use an https://…ts.net URL.';
        return 'Not configured. Run tailscale serve --bg 127.0.0.1:36742, then relaunch with VRCX_REMOTE_TAILNET_URL set to the reported HTTPS URL.';
    });

    onMounted(async () => {
        try {
            status.value = await window.electron.getRemoteAccessStatus();
        } finally {
            loading.value = false;
        }
    });
</script>
