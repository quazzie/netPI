// Webhooks tab (netpi.webhooks): systems that POST to NetPI and start a chat.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { createTab } from '@netpi/kit';
import WebhooksTab from './WebhooksTab.svelte';

export const mount = createTab(WebhooksTab);
