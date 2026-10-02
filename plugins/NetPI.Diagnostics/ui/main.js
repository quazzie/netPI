// Diagnostics tab (netpi.diagnostics): plugins, tools, RPC methods, live events, logs and the context preview.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { createTab } from '@netpi/kit';
import DiagTab from './DiagTab.svelte';

export const mount = createTab(DiagTab);